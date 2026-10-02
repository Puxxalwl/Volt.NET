using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Volt.Compiler;
using Xunit;

namespace Volt.Compiler.Tests;

/// <summary>
/// String-level emission tests: run the generator over in-memory page files and
/// assert the generated registrations. (Compile-level correctness of the emitted
/// code is covered by the E2E project, which builds real pages.)
/// </summary>
public sealed class RouteGenerationTests
{
    private static string RunGenerator(params (string Path, string Text)[] files)
    {
        var additionalTexts = files.Select(f => (AdditionalText)new InMemoryAdditionalText(f.Path, f.Text)).ToArray();

        // an empty compilation is enough: we assert the emitted TEXT (compile-level
        // correctness of emitted code is covered by the E2E project)
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

    private const string PageSource = """
        namespace App.Pages
        {
            public sealed class P : Volt.VoltPage
            {
                public override void Render(Volt.HtmlWriter w, Volt.RenderContext ctx) { }
            }
        }
        """;

    public static TheoryData<string, string> RouteCases => new()
    {
        { "/app/Pages/Index.cs", "/" },
        { "/app/Pages/about.cs", "/about" },
        { "/app/Pages/Blog/[slug].cs", "/blog/{slug}" },
        { "/app/Pages/Shop/[...rest].cs", "/shop/{*rest}" },
        { "/app/Pages/docs/guide.cs", "/docs/guide" },
        { "/app/Pages/docs/index.cs", "/docs" },
        { "/app/Pages/About.Volt.cs", "/about.volt" },
    };

    [Theory]
    [MemberData(nameof(RouteCases))]
    public void RoutesGeneratedFromFilePaths(string filePath, string expectedRoute)
    {
        var generated = RunGenerator((filePath, PageSource));
        Assert.Contains($"RouteRegistry.Root.Add(\"{expectedRoute}\",", generated);
        Assert.Contains("new global::App.Pages.P()", generated);
    }

    [Fact]
    public void StaticSegmentsAreLowercased()
    {
        var generated = RunGenerator(("/app/Pages/Pricing/FAQ.cs", PageSource));
        Assert.Contains("RouteRegistry.Root.Add(\"/pricing/faq\",", generated);
    }

    [Fact]
    public void NotFoundPageRegistersSpecialFactory()
    {
        var generated = RunGenerator(("/app/Pages/notfound.cs", PageSource));
        Assert.Contains("RouteRegistry.Root.SetNotFound(static () => new global::App.Pages.P());", generated);
        Assert.DoesNotContain("RouteRegistry.Root.Add(", generated);
    }

    [Fact]
    public void ErrorPageRegistersSpecialFactory()
    {
        var generated = RunGenerator(("/app/Pages/error.cs", PageSource));
        Assert.Contains("RouteRegistry.Root.SetError(static () => new global::App.Pages.P());", generated);
    }

    [Fact]
    public void NonPageFilesAreIgnored()
    {
        var generated = RunGenerator(("/app/README.md", "hello"));
        Assert.DoesNotContain("RouteRegistry.Root.Add", generated);
        Assert.DoesNotContain("SetNotFound", generated);
    }

    [Fact]
    public void ModuleInitializerBootsOnce()
    {
        var generated = RunGenerator(("/app/Pages/Index.cs", PageSource));
        Assert.Contains("[ModuleInitializer]", generated);
    }

    private sealed class InMemoryAdditionalText(string path, string content) : AdditionalText
    {
        public override string Path { get; } = path;
        public override SourceText? GetText(CancellationToken cancellationToken = default)
            => SourceText.From(content, Encoding.UTF8);
    }

}

// ---- M6: [VoltForm] binder emission ------------------------------------------------

/// <summary>Runs the generator over real compiled sources (forms need symbols).</summary>
public sealed class FormGenerationTests
{
    private static string RunGeneratorWithSources(params (string Path, string Text)[] sources)
    {
        var compilation = CSharpCompilation.Create("TestApp",
            syntaxTrees: sources.Select(s => CSharpSyntaxTree.ParseText(s.Text, new CSharpParseOptions(LanguageVersion.Latest))),
            references:
            [
                // Attribute lives in System.Runtime (a facade next to CoreLib) —
                // typeof(object/Attribute) both resolve to CoreLib, so take the real file
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
                MetadataReference.CreateFromFile(typeof(Volt.VoltPage).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<string>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
            ]);

        var driver = CSharpGeneratorDriver.Create([new Volt.Compiler.VoltGenerator().AsSourceGenerator()]);
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

    private const string FormSource = """
        namespace App
        {
            [Volt.VoltForm]
            public sealed partial class Subscribe
            {
                [Volt.VoltRequired(Message = "email is required"), Volt.VoltEmail]
                public string Email { get; set; } = "";

                [Volt.VoltRange(1, 5)]
                public int? Rating { get; set; }

                [Volt.VoltMaxLength(20)]
                public string? Name { get; set; }

                public bool Agree { get; set; }
            }
        }
        """;

    [Fact]
    public void Form_BinderImplementsInterface_AndBindsCaseInsensitively()
    {
        var generated = RunGeneratorWithSources(("/app/Form.cs", FormSource));

        Assert.Contains("partial class Subscribe : IVoltForm", generated);
        Assert.Contains("static IVoltForm? IVoltForm.VoltBind", generated);
        Assert.Contains("case \"email\":", generated);
        Assert.Contains("case \"rating\":", generated);
        Assert.Contains("case \"agree\":", generated);
        // checkbox semantics
        Assert.Contains("value is \"on\" or \"true\" or \"1\" or \"yes\"", generated);
        // int binding
        Assert.Contains("int.TryParse(value, out var parsed)", generated);
        // presence flags for required value types
        Assert.Contains("bool p_Email_present = false;", generated);
    }

    [Fact]
    public void Form_Validators_EmitWithConstructorArgs()
    {
        var generated = RunGeneratorWithSources(("/app/Form.cs", FormSource));
        Assert.Contains("string.IsNullOrWhiteSpace(form.Email)", generated);
        Assert.Contains("\"email is required\"", generated);
        // range from ctor args (1..5), not named args
        Assert.Contains("v_Rating < 1L || v_Rating > 5L", generated);
        Assert.Contains("\"must be between 1 and 5\"", generated);
        // maxlength from ctor arg
        Assert.Contains("form.Name is not null && form.Name.Length > 20", generated);
        Assert.Contains("too long", generated);
        // email shape check
        Assert.Contains("IndexOf('@') <= 0", generated);
    }

    [Fact]
    public void Form_NonPartial_ReportsDiagnostic()
    {
        var generated = RunGeneratorWithSources(("/app/Form.cs", FormSource.Replace("partial ", "")));

        Assert.Contains("must be declared partial", generated);
    }
}
