using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Volt;

// InProcess toolchain: BDN 0.15 doesn't recognize the net10.0 sdk for child processes
var config = ManualConfig.Create(DefaultConfig.Instance)
    .AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance).WithInvocationCount(64).WithIterationCount(32))
    .AddDiagnoser(MemoryDiagnoser.Default);
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);

public partial class Program;

/// <summary>
/// Hot-path benchmarks: route match + full page render into a pooled buffer.
/// Run: dotnet run -c Release
/// </summary>
[MemoryDiagnoser]
public class VoltBenchmarks
{
    private readonly CatalogPage _page = new();
    private readonly RouteRegistry _routes = BuildRoutes();
    private readonly PooledBufferWriter _buffer = new();
    private readonly string _path = "/shop/books/42";

    private static RouteRegistry BuildRoutes()
    {
        var registry = new RouteRegistry();
        registry.Add("/", static () => null!);
        registry.Add("/products", static () => null!);
        registry.Add("/about", static () => null!);
        registry.Add("/blog/{slug}", static () => null!);
        registry.Add("/shop/{category}/{id}", static () => null!);
        registry.Add("/docs/{*path}", static () => null!);
        registry.Add("/contact", static () => null!);
        registry.Add("/pricing", static () => null!);
        return registry;
    }

    [Benchmark(Baseline = true, Description = "route match (8 routes)")]
    public bool RouteMatch()
    {
        Span<RawParam> slices = stackalloc RawParam[16];
        return _routes.Match(_path.AsSpan(), slices).Found;
    }

    [Benchmark(Description = "route match + page render (~3KB)")]
    public byte[]? RenderPage()
    {
        _buffer.Reset();
        var w = HtmlWriter.Rent(_buffer);
        try
        {
            Span<RawParam> slices = stackalloc RawParam[16];
            var match = _routes.Match("/products".AsSpan(), slices);
            var ctx = new RenderContext { Params = match.Params };
            _page.Render(w, ctx);
        }
        finally
        {
            w.Return();
        }
        // return a checksum-ish token without keeping spans alive
        return _buffer.WrittenSpan.IsEmpty ? [] : null;
    }

    [IterationSetup(Target = nameof(RenderPage))]
    public void SetupRender() => _buffer.Reset();
}

/// <summary>A realistic catalog page (~3KB output, constant strings only).</summary>
internal class CatalogPage : VoltPage
{
    public override void Render(HtmlWriter w, RenderContext ctx)
    {
        w.DocType();
        using (w.Html("en"))
        {
            using (w.Head())
            {
                w.MetaCharset();
                w.Title("Catalog — Volt demo");
                w.Meta("description", "A catalog page for benchmarks.");
                w.Link("stylesheet", "/styles.css");
                w.MetaProperty("og:title", "Catalog");
            }
            using (w.Body())
            {
                using (w.Header())
                {
                    using (w.Nav())
                    {
                        w.Class("topnav");
                        using (w.A()) { w.Href("/"); w.Text("Home"); }
                        using (w.A()) { w.Href("/products"); w.Text("Products"); }
                        using (w.A()) { w.Href("/about"); w.Text("About"); }
                    }
                    w.H1Text("Product catalog");
                }
                using (w.Main())
                {
                    for (int i = 0; i < 8; i++)
                    {
                        using (w.Article())
                        {
                            w.Class("card");
                            using (w.H2()) w.Text("Widget model X-100");
                            using (w.P()) w.Text("Description of the widget with some length to it.");
                            using (w.Span()) { w.Class("price"); w.Text("$42.00"); }
                        }
                    }
                }
                using (w.Footer())
                {
                    using (w.P()) w.Text("© 2025 Volt demo. All rights reserved.");
                }
            }
        }
    }
}

/// <summary>
/// M2 fast-path benchmark: cached SSG serving through the engine (what the built-in
/// zero-allocation server does per request, minus socket I/O). Run with --filter "*Fast*".
/// </summary>
[MemoryDiagnoser]
public class FastPathBenchmarks
{
    private sealed class SsgCatalogPage : CatalogPage
    {
        public override RenderMode Mode => RenderMode.SSG;
    }

    private static readonly VoltOptions Options = new();
    private readonly PooledBufferWriter _buffer = new();
    private readonly VoltHttpContext _response = new();
    private readonly string _path = WarmCache();

    private static string WarmCache()
    {
        var route = "/bench-fast";
        VoltRuntime.Routes.Add(route, static () => new SsgCatalogPage());
        var ctx = new VoltHttpContext { Method = "GET", Path = route, Output = new PooledBufferWriter() };
        VoltEngine.HandleAsync(ctx, Options).GetAwaiter().GetResult();
        return route;
    }

    [IterationSetup]
    public void Setup()
    {
        _buffer.Reset();
        _response.Reset();
        _response.Output = _buffer;
    }

    [Benchmark(Description = "fast path: cached SSG serve (route match + cache hit + body write)")]
    public bool FastPathServe() => VoltEngine.TryServeFast(_path.AsSpan(), default, null, _response, Options);
}
