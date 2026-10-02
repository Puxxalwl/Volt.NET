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
