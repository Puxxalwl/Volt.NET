using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Volt.Compiler;
using Xunit;

namespace Volt.Compiler.Tests;

/// <summary>
/// .volt template pipeline: parse + emit tests. The E2E project additionally builds a
/// real .volt page and serves it (compile-level correctness of the emitted C#).
/// </summary>
public sealed class VoltFileGenerationTests
{
    private static string RunGenerator(params (string Path, string Text)[] files)
    {
        var additionalTexts = files.Select(f => (AdditionalText)new InMemoryText(f.Path, f.Text)).ToArray();

        var compilation = CSharpCompilation.Create("TestApp",
            syntaxTrees: [CSharpSyntaxTree.ParseText("class Empty {}", new CSharpParseOptions(LanguageVersion.Latest))]);

        var driver = CSharpGeneratorDriver.Create(
            [new VoltGenerator().AsSourceGenerator()],
            additionalTexts: additionalTexts);

        var result = driver.RunGenerators(compilation).GetRunResult();

        var sb = new StringBuilder();
        foreach (var tree in result.GeneratedTrees)
        {
            sb.AppendLine($"// ===== {tree.FilePath}");
            sb.AppendLine(tree.GetText().ToString());
        }
        foreach (var diagnostic in result.Diagnostics)
        {
            sb.AppendLine($"// ===== diagnostic {diagnostic.Id}");
            sb.AppendLine(diagnostic.GetMessage());
        }
        return sb.ToString();
    }

    private sealed class InMemoryText(string path, string content) : AdditionalText
    {
        public override string Path { get; } = path;
        public override SourceText? GetText(CancellationToken cancellationToken = default)
            => SourceText.From(content, Encoding.UTF8);
    }

    private const string HelloVolt = """
        @page /hello
        @mode SSG

        <!DOCTYPE html>
        <html lang="en">
        <head>
            <title>Hello — Volt</title>
        </head>
        <body>
            <h1 class="title">Hello, Volt!</h1>
            @if (ctx.Request.Path.Length > 1) {
                <p>Path: @(ctx.Request.Path)</p>
            }
        </body>
        </html>
        """;

    [Fact]
    public void VoltPage_RegistersRoute_AndEmitsPageClass()
    {
        var generated = RunGenerator(("/app/Pages/Hello.volt", HelloVolt));

        Assert.Contains("RouteRegistry.Root.Add(\"/hello\"", generated);
        Assert.Contains("public sealed partial class Volt_Hello : VoltPage", generated);
        Assert.Contains("RenderMode.SSG", generated);
        Assert.Contains("w.DocType();", generated);
        Assert.Contains("using (w.El(\"html\"))", generated);
        Assert.Contains("w.Attr(\"lang\", \"en\");", generated);
        Assert.Contains("w.Class(\"title\");", generated);
        Assert.Contains("w.Text(\"Hello, Volt!\");", generated);
        Assert.Contains("if (ctx.Request.Path.Length > 1)", generated);
        Assert.Contains("w.Text((ctx.Request.Path).ToString());", generated);
    }

    [Fact]
    public void VoltPage_RevalidateDirective_EmitsOverride()
    {
        var generated = RunGenerator(("/app/Pages/Re.volt",
            "@page /re\n@revalidate 60\n<h1>re</h1>"));

        Assert.Contains("RevalidateSeconds => 60", generated);
    }

    [Fact]
    public void VoltPage_LoopEmitsMarkupInsideCode()
    {
        var generated = RunGenerator(("/app/Pages/Loop.volt", """
            @page /loop
            <ul>
            @for (int i = 0; i < 3; i++) {
                <li>Item @(i + 1)</li>
            }
            </ul>
            """));

        Assert.Contains("for (int i = 0; i < 3; i++)", generated);
        Assert.Contains("using (w.El(\"li\"))", generated);
        Assert.Contains("w.Text((i + 1).ToString());", generated);
        // the </ul> close must still balance the <ul> using
        Assert.Contains("} // /ul", generated);
    }

    [Fact]
    public void VoltPage_VoidTagEmitsVoidEl()
    {
        var generated = RunGenerator(("/app/Pages/Void.volt",
            "@page /void\n<img src=\"/x.png\" alt=\"pic\">\n<br>"));

        Assert.Contains("using (w.VoidEl(\"img\"))", generated);
        Assert.Contains("w.Attr(\"src\", \"/x.png\");", generated);
        Assert.Contains("using (w.VoidEl(\"br\"));", generated);
    }

    [Fact]
    public void VoltPage_RouteFallsBackToPathConvention()
    {
        var generated = RunGenerator(("/app/Pages/Docs/Guide.volt", "<h1>g</h1>"));

        Assert.Contains("RouteRegistry.Root.Add(\"/docs/guide\"", generated);
        Assert.Contains("Volt_Docs_Guide", generated);
    }

    [Fact]
    public void VoltPage_NotFoundAndErrorRegisterSpecialFactories()
    {
        var generated = RunGenerator(
            ("/app/Pages/notfound.volt", "<h1>404</h1>"),
            ("/app/Pages/error.volt", "<h1>500</h1>"));

        Assert.Contains("SetNotFound", generated);
        Assert.Contains("SetError", generated);
    }

    [Fact]
    public void VoltPage_RawCodeBlockEmitsVerbatimCSharp()
    {
        var generated = RunGenerator(("/app/Pages/Raw.volt",
            "@page /raw\n@{ var total = 1 + 2; }\n<p>@(total)</p>"));

        Assert.Contains("var total = 1 + 2;", generated);
        Assert.Contains("w.Text((total).ToString());", generated);
    }

    [Fact]
    public void VoltPage_AttributeWithExpression()
    {
        var generated = RunGenerator(("/app/Pages/Attr.volt",
            "@page /attr\n<a href=\"/blog/@(ctx.Request.Path)\">link</a>"));

        Assert.Contains("w.Attr(\"href\", \"/blog/\" + (ctx.Request.Path).ToString());", generated);
    }

    [Fact]
    public void VoltPage_MalformedTemplate_SkipsRegistration()
    {
        // parse errors surface as VOLT020 diagnostics (not trees); the broken page
        // must not be registered
        var generated = RunGenerator(("/app/Pages/Bad.volt",
            "@page /bad\n@if (x { <p>unclosed"));

        Assert.DoesNotContain("RouteRegistry.Root.Add(\"/bad\"", generated);
        Assert.DoesNotContain("class Volt_Bad", generated);
    }

    [Fact]
    public void VoltPage_CommentIsSkipped()
    {
        var generated = RunGenerator(("/app/Pages/Cm.volt",
            "@page /cm\n<!-- a comment --><p>text</p>"));

        Assert.DoesNotContain("a comment", generated.Replace("VOLT", "V"));
        Assert.Contains("w.Text(\"text\");", generated);
    }

    [Fact]
    public void VoltPage_VoltIslandNodeEmitsIslandByName()
    {
        var generated = RunGenerator(("/app/Pages/Isle.volt", """
            @page /isle
            <volt-island name="Todo" state='{"text":"hi","count":1,"done":false}' />
            """));

        Assert.Contains("""w.IslandByName("Todo", "{\"text\":\"hi\",\"count\":1,\"done\":false}", ctx);""", generated);
    }

    [Fact]
    public void VoltPage_VoltIslandWithSidEmitsSidArgument()
    {
        var generated = RunGenerator(("/app/Pages/IsleSid.volt", """
            @page /isle2
            <volt-island name="Todo" state='{"done":true}' sid="hero" />
            """));

        Assert.Contains("""w.IslandByName("Todo", "{\"done\":true}", ctx, "hero");""", generated);
    }

    [Fact]
    public void VoltPage_VoltIslandClosingTagEmitsNothing()
    {
        var generated = RunGenerator(("/app/Pages/IsleClose.volt", """
            @page /isle3
            <volt-island name="Todo" state='{}'></volt-island>
            """));

        Assert.Contains("""w.IslandByName("Todo", "{}", ctx);""", generated);
        Assert.DoesNotContain("} // /volt-island", generated);
    }

    [Fact]
    public void VoltPage_VoltIslandMissingName_SkipsRegistration()
    {
        var generated = RunGenerator(("/app/Pages/IsleBad.volt", """
            @page /isle4
            <volt-island state='{}' />
            """));

        Assert.DoesNotContain("RouteRegistry.Root.Add(\"/isle4\"", generated);
    }

    // ---- M6: layouts + partials ------------------------------------------------------

    private const string LayoutSource = """
        <html>
        <head><title>site</title></head>
        <body>
        <nav>nav</nav>
        @renderbody
        <footer>foot</footer>
        </body>
        </html>
        """;

    /// <summary>Extracts one generated file from the RunGenerator concatenation
    /// (tree paths are prefixed with the generator name, e.g. Volt.Compiler/…/Volt_Home.g.cs).</summary>
    private static string ExtractFile(string all, string name)
    {
        int at = -1;
        int search = 0;
        while (at < 0)
        {
            int line = all.IndexOf("// ===== ", search, StringComparison.Ordinal);
            if (line < 0) break;
            int lineEnd = all.IndexOf('\n', line);
            var header = all.Substring(line, lineEnd - line);
            if (header.EndsWith("/" + name, StringComparison.Ordinal)
                || header.EndsWith("// ===== " + name, StringComparison.Ordinal))
            {
                at = line;
                break;
            }
            search = lineEnd + 1;
        }
        Assert.True(at >= 0, $"generated file {name} not found");
        int next = all.IndexOf("// ===== ", at + 10, StringComparison.Ordinal);
        return next < 0 ? all.Substring(at) : all.Substring(at, next - at);
    }

    [Fact]
    public void Layout_SplicesAroundPage_WithZeroRuntimeBaseClass()
    {
        var generated = RunGenerator(
            ("/app/Pages/_Layout.volt", LayoutSource),
            ("/app/Pages/Home.volt", "@page /\n@mode SSG\n<h1>Home</h1>"));

        Assert.Contains("RouteRegistry.Root.Add(\"/\"", generated);
        var page = ExtractFile(generated, "Volt_Home.g.cs");
        Assert.Contains("using (w.El(\"nav\"))", page);
        Assert.Contains("w.Text(\"nav\");", page);
        Assert.Contains("w.Text(\"Home\");", page);
        Assert.Contains("w.Text(\"foot\");", page);
        Assert.True(page.IndexOf("w.Text(\"nav\");") < page.IndexOf("w.Text(\"Home\");"));
        Assert.True(page.IndexOf("w.Text(\"Home\");") < page.IndexOf("w.Text(\"foot\");"));
        Assert.DoesNotContain("VoltLayout", page);
    }

    [Fact]
    public void Layout_DoesNotBecomeRoute()
    {
        var generated = RunGenerator(
            ("/app/Pages/_Layout.volt", LayoutSource),
            ("/app/Pages/Home.volt", "@page /x\n<h1>Home</h1>"));

        Assert.DoesNotContain("\"/_layout\"", generated);
        Assert.DoesNotContain("Volt__Layout", generated);
    }

    [Fact]
    public void Layout_Nested_ConventionAppliesParentFirst()
    {
        var generated = RunGenerator(
            ("/app/Pages/_Layout.volt", "<html><body>\n@renderbody\n<footer>outer</footer>\n</body></html>"),
            ("/app/Pages/Admin/_Layout.volt", "<div class=\"admin\">\n@renderbody\n</div>"),
            ("/app/Pages/Admin/Dash.volt", "@page /admin/dash\n<h1>Dash</h1>"));

        var page = ExtractFile(generated, "Volt_Admin_Dash.g.cs");
        Assert.Contains("using (w.El(\"body\"))", page);
        Assert.Contains("w.Class(\"admin\");", page);
        Assert.Contains("w.Text(\"Dash\");", page);
        Assert.Contains("w.Text(\"outer\");", page);
        Assert.True(page.IndexOf("w.Class(\"admin\");") < page.IndexOf("w.Text(\"Dash\");"));
        Assert.True(page.IndexOf("w.Text(\"Dash\");") < page.IndexOf("w.Text(\"outer\");"));
    }

    [Fact]
    public void Layout_NamedOverride_Resolves_Nearest()
    {
        // a named layout nests into the nearest default _Layout by convention;
        // @layout none on the named layout itself opts out of the nesting
        var generated = RunGenerator(
            ("/app/Pages/_Layout.volt", "<main>\n@renderbody\n</main>"),
            ("/app/Pages/_BareLayout.volt", "@layout none\n<pre>\n@renderbody\n</pre>"),
            ("/app/Pages/Bare.volt", "@layout Bare\n@page /bare\n<b>b</b>"));

        var page = ExtractFile(generated, "Volt_Bare.g.cs");
        Assert.Contains("using (w.El(\"pre\"))", page);
        Assert.DoesNotContain("using (w.El(\"main\"))", page);
    }

    [Fact]
    public void Layout_NamedLayout_NestsIntoDefault_ByConvention()
    {
        var generated = RunGenerator(
            ("/app/Pages/_Layout.volt", "<main>\n@renderbody\n</main>"),
            ("/app/Pages/_BareLayout.volt", "<pre>\n@renderbody\n</pre>"),
            ("/app/Pages/Bare.volt", "@layout Bare\n@page /bare\n<b>b</b>"));

        var page = ExtractFile(generated, "Volt_Bare.g.cs");
        Assert.Contains("using (w.El(\"main\"))", page);
        Assert.Contains("using (w.El(\"pre\"))", page);
        Assert.True(page.IndexOf("w.El(\"main\")") < page.IndexOf("w.El(\"pre\")"));
        Assert.True(page.IndexOf("w.El(\"pre\")") < page.IndexOf("w.El(\"b\")"));
    }

    [Fact]
    public void Layout_SubfolderAppliesToSubtreeOnly()
    {
        var generated = RunGenerator(
            ("/app/Pages/Shop/_Layout.volt", "<section>\n@renderbody\n</section>"),
            ("/app/Pages/Shop/Item.volt", "@page /shop/item\n<i>item</i>"),
            ("/app/Pages/Top.volt", "@page /top\n<i>top</i>"));

        var shop = ExtractFile(generated, "Volt_Shop_Item.g.cs");
        var top = ExtractFile(generated, "Volt_Top.g.cs");
        Assert.Contains("using (w.El(\"section\"))", shop);
        Assert.DoesNotContain("using (w.El(\"section\"))", top);
    }

    [Fact]
    public void Layout_MissingRenderbody_ReportsDiagnostic()
    {
        var generated = RunGenerator(
            ("/app/Pages/_Layout.volt", "<html><body>no slot</body></html>"),
            ("/app/Pages/Home.volt", "@page /h\n<h1>h</h1>"));

        Assert.Contains("layout must contain @renderbody", generated);
        Assert.Contains("RouteRegistry.Root.Add(\"/h\"", generated);
    }

    [Fact]
    public void Partial_EmitsStaticMethod_AndCallSiteInvokesIt()
    {
        var generated = RunGenerator(
            ("/app/Pages/Partials/Card.volt",
                "@partial Card(global::App.Widget widget)\n<div class=\"card\"><h3>@(widget.Title)</h3></div>"),
            ("/app/Pages/Home.volt",
                "@page /\n<h1>Home</h1>\n@{ var widget = new global::App.Widget(); }\n@Card(widget)"));

        var partials = ExtractFile(generated, "VoltPartials.g.cs");
        Assert.Contains("internal static class VoltPartials", partials);
        Assert.Contains("public static void Card(HtmlWriter w, RenderContext ctx, global::App.Widget widget)", partials);
        Assert.Contains("w.Text((widget.Title).ToString());", partials);

        var page = ExtractFile(generated, "Volt_Home.g.cs");
        Assert.Contains("global::Volt.Generated.VoltPartials.Card(w, ctx, widget);", page);
        Assert.DoesNotContain("RouteRegistry.Root.Add(\"/partials/card\"", generated);
    }

    [Fact]
    public void Partial_WithoutParentheses_ReportsDiagnostic()
    {
        var generated = RunGenerator(
            ("/app/Pages/Partials/Card.volt", "@partial Card(global::App.Widget widget)\n<div>c</div>"),
            ("/app/Pages/Home.volt", "@page /\n@Card"));

        Assert.Contains("partial call '@Card' requires parentheses", generated);
    }

    [Fact]
    public void UnderscoreFileWithoutDirective_ReportsDiagnostic()
    {
        var generated = RunGenerator(
            ("/app/Pages/_Side.volt", "<div>side</div>"));

        Assert.Contains("files starting with '_' are non-routable", generated);
    }

    [Fact]
    public void RenderBodyOutsideLayout_ReportsDiagnostic()
    {
        var generated = RunGenerator(
            ("/app/Pages/Bad.volt", "@page /bad\n@renderbody"));

        Assert.Contains("@renderbody is only allowed in layout files", generated);
    }
}
