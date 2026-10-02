using System.Runtime.CompilerServices;
using Volt;
using Xunit;

namespace Volt.Core.Tests;

/// <summary>
/// Allocation gates: route matching and page rendering must allocate ZERO bytes
/// on the steady-state hot path (islands and JSON are excluded by design — see README).
/// </summary>
public class ZeroAllocGateTests
{
    /// <summary>A realistic page: ~40 elements, nav, list, footer — all constant strings.</summary>
    private class CatalogPage : VoltPage
    {
        public override void Render(HtmlWriter w, RenderContext ctx) => RenderCatalog(w, ctx);

        public static void RenderCatalog(HtmlWriter w, RenderContext ctx)
        {
            w.DocType();
            using (w.Html("en"))
            {
                using (w.Head())
                {
                    w.MetaCharset();
                    w.Title("Catalog — Volt demo");
                    w.Meta("description", "A catalog page for allocation benchmarks.");
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

    private static RouteRegistry BuildCatalogRoutes()
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

    private static readonly RouteRegistry CatalogRoutes = BuildCatalogRoutes();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RenderOnce(CatalogPage page, PooledBufferWriter buffer)
    {
        var w = HtmlWriter.Rent(buffer);
        try
        {
            Span<RawParam> slices = stackalloc RawParam[16];
            var match = CatalogRoutes.Match("/products".AsSpan(), slices);
            var ctx = new RenderContext { Params = match.Params };
            page.Render(w, ctx);
        }
        finally
        {
            w.Return();
        }
    }

    [Fact]
    public void PageRenderIsZeroAlloc()
    {
        // warm-up: rent pools, grow the buffer once, JIT the hot path
        var page = new CatalogPage();
        using var buffer = new PooledBufferWriter();
        for (int i = 0; i < 10; i++)
        {
            buffer.Reset();
            RenderOnce(page, buffer);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 200;
        for (int i = 0; i < iterations; i++)
        {
            buffer.Reset();
            RenderOnce(page, buffer);
        }
        var delta = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(delta == 0,
            $"page render allocated {delta} bytes over {iterations} iterations (expected 0)");
    }

    [Fact]
    public void RouteMatchIsZeroAlloc()
    {
        var registry = BuildCatalogRoutes();
        Span<RawParam> slices = stackalloc RawParam[16];

        // warm-up
        for (int i = 0; i < 10; i++)
            _ = registry.Match("/shop/books/42".AsSpan(), slices);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            _ = registry.Match("/shop/books/42".AsSpan(), slices);
            _ = registry.Match("/about".AsSpan(), slices);
        }
        var delta = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(delta == 0,
            $"route matching allocated {delta} bytes over 1000 iterations (expected 0)");
    }

    [Fact]
    public void RenderedCatalogIsWellFormed()
    {
        var page = new CatalogPage();
        using var buffer = new PooledBufferWriter();
        RenderOnce(page, buffer);
        var html = System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan.ToArray());
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.EndsWith("</html>", html);
        Assert.Equal(9, html.Split("<article").Length); // 8 articles + prefix
    }

    /// <summary>SSG page served through the zero-allocation fast path (M2 server hot path).</summary>
    private sealed class SsgCatalogPage : CatalogPage
    {
        public override RenderMode Mode => RenderMode.SSG;
        public override void Render(HtmlWriter w, RenderContext ctx) => RenderCatalog(w, ctx);
    }

    private static readonly string FastRoute = "/__zero_fast_" + Guid.NewGuid().ToString("N")[..8];

    private static async Task WarmFastPathAsync()
    {
        var route = FastRoute;
        VoltRuntime.Routes.Add(route, static () => new SsgCatalogPage());
        var ctx = new VoltHttpContext
        {
            Method = "GET",
            Path = route,
            Output = new PooledBufferWriter(),
        };
        await VoltEngine.HandleAsync(ctx, new VoltOptions());
    }

    private static readonly VoltOptions GateOptions = new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ServeFastOnce(ReadOnlySpan<char> path, VoltHttpContext response)
        => VoltEngine.TryServeFast(path, default, null, response, GateOptions);

    [Fact]
    public async Task FastPathSsgServeIsZeroAlloc()
    {
        // populate the SSG cache via the async pipeline once (allocations allowed here)
        await WarmFastPathAsync();

        using var buffer = new PooledBufferWriter();
        var response = new VoltHttpContext { Output = buffer };
        var path = FastRoute.AsSpan();

        // warm-up: JIT + pools
        for (int i = 0; i < 20; i++)
        {
            buffer.Reset();
            response.Reset();
            response.Output = buffer;
            Assert.True(ServeFastOnce(path, response));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 500;
        for (int i = 0; i < iterations; i++)
        {
            buffer.Reset();
            response.Reset();
            response.Output = buffer;
            Assert.True(ServeFastOnce(path, response));
        }
        var delta = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(delta == 0,
            $"fast-path SSG serving allocated {delta} bytes over {iterations} iterations (expected 0)");
    }
}
