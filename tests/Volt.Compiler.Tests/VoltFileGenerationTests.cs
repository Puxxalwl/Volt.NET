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
        Assert.Contains("public sealed class Volt_Hello : VoltPage", generated);
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
}
