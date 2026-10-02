using System.Buffers;
using Xunit;

namespace Volt.Core.Tests;

/// <summary>
/// M6 middleware pipeline: onion order, short-circuit, headers, the fast-path
/// bypass rule and the custom error handler.
/// </summary>
public sealed class MiddlewareTests
{
    private static async Task<VoltHttpContext> ServeAsync(VoltOptions options, string path, string method = "GET")
    {
        var ctx = new VoltHttpContext { Method = method, Path = path, Output = new PooledBufferWriter() };
        await VoltEngine.HandleAsync(ctx, options);
        return ctx;
    }

    private sealed class OkPage : VoltPage
    {
        public override RenderMode Mode => RenderMode.SSR;
        public override void Render(HtmlWriter w, RenderContext ctx)
        {
            using (w.El("html")) w.Text("ok");
        }
    }

    private static string NewRoute()
    {
        var route = "/__mw_" + Guid.NewGuid().ToString("N");
        VoltRuntime.Routes.Add(route, static () => new OkPage());
        return route;
    }

    [Fact]
    public async Task Middleware_RunsInRegistrationOrder_AroundThePipeline()
    {
        var route = NewRoute();
        var order = new List<string>();
        var options = new VoltOptions();
        options
            .Use((ctx, next) => { order.Add("mw1:before"); return next(); })
            .Use((ctx, next) => { order.Add("mw2:before"); var t = next(); order.Add("mw2:after"); return t; });

        var ctx = await ServeAsync(options, route);

        Assert.Equal(200, ctx.StatusCode);
        Assert.Equal(new[] { "mw1:before", "mw2:before", "mw2:after" }, order);
    }

    [Fact]
    public async Task Middleware_CanShortCircuit_WithAResponse()
    {
        var route = NewRoute();
        var options = new VoltOptions();
        options.Use((ctx, next) =>
        {
            ctx.StatusCode = 403;
            ctx.ResponseStarted = true;
            return Task.CompletedTask; // no next() — the page never renders
        });

        var ctx = await ServeAsync(options, route);

        Assert.Equal(403, ctx.StatusCode);
        // nothing was written by the page
        var body = ((PooledBufferWriter)ctx.Output!).WrittenSpan;
        Assert.Equal(0, body.Length);
    }

    [Fact]
    public async Task Middleware_CanAddHeaders_ExtraHeadersSurface()
    {
        var route = NewRoute();
        var options = new VoltOptions();
        options.Use((ctx, next) =>
        {
            ctx.ExtraHeaders ??= new List<(string, string)>();
            ctx.ExtraHeaders.Add(("X-Volt-Mw", "1"));
            return next();
        });

        var ctx = await ServeAsync(options, route);

        Assert.Equal(200, ctx.StatusCode);
        Assert.NotNull(ctx.ExtraHeaders);
        Assert.Contains(("X-Volt-Mw", "1"), ctx.ExtraHeaders);
    }

    [Fact]
    public async Task Middleware_CanRedirect()
    {
        var route = NewRoute();
        var options = new VoltOptions();
        options.Use((ctx, next) =>
        {
            ctx.StatusCode = 302;
            ctx.Location = "/login";
            ctx.ResponseStarted = true;
            return Task.CompletedTask;
        });

        var ctx = await ServeAsync(options, route);

        Assert.Equal(302, ctx.StatusCode);
        Assert.Equal("/login", ctx.Location);
    }

    [Fact]
    public async Task Middleware_Registration_DisablesTheFastPath()
    {
        // warm the SSG cache via the async pipeline once — the cached page
        // is then served by the zero-allocation fast path
        var route = "/__mw_fast_" + Guid.NewGuid().ToString("N")[..8];
        VoltRuntime.Routes.Add(route, static () => new FastSsgPage());
        await ServeAsync(new VoltOptions(), route);

        var options = new VoltOptions();
        Assert.False(options.HasMiddleware);

        var response = new VoltHttpContext { Output = new PooledBufferWriter() };
        Assert.True(VoltEngine.TryServeFast(route, "", null, response, options));

        options.Use((ctx, next) => next());
        var response2 = new VoltHttpContext { Output = new PooledBufferWriter() };
        Assert.False(VoltEngine.TryServeFast(route, "", null, response2, options));
        Assert.True(options.HasMiddleware);
    }

    private sealed class FastSsgPage : VoltPage
    {
        public override RenderMode Mode => RenderMode.SSG;
        public override void Render(HtmlWriter w, RenderContext ctx) { using (w.El("p")) w.Text("fast"); }
    }

    [Fact]
    public async Task OnException_ReplacesTheDefaultErrorPage()
    {
        var route = "/__mw_boom_" + Guid.NewGuid().ToString("N");
        VoltRuntime.Routes.Add(route, static () => new ThrowingPage());
        var options = new VoltOptions
        {
            OnException = (ctx, ex) =>
            {
                ctx.StatusCode = 418; // I'm a teapot
                ctx.ContentType = "text/plain; charset=utf-8";
                ctx.ResponseStarted = true;
                return Task.CompletedTask;
            }
        };

        var ctx = await ServeAsync(options, route);

        Assert.Equal(418, ctx.StatusCode);
    }

    private sealed class ThrowingPage : VoltPage
    {
        public override RenderMode Mode => RenderMode.SSR;
        public override void Render(HtmlWriter w, RenderContext ctx) => throw new InvalidOperationException("boom");
    }
}
