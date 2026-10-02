using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Volt;
using Xunit;

namespace Volt.E2E;

/// <summary>
/// M6 middleware on a real running server: headers survive the transport,
/// short-circuits never reach the page, and the custom error handler replaces
/// the default 500 page.
/// </summary>
public sealed class MiddlewareE2ETests : IAsyncLifetime
{
    private WebApplication? _app;
    private HttpClient _client = null!;
    private string _url = null!;

    public async Task InitializeAsync()
    {
        (_url, _app) = await VoltApp.StartTestServerAsync(new VoltOptions
        {
            BaseUrl = "http://127.0.0.1",
        }.Use((ctx, next) =>
        {
            ctx.ExtraHeaders ??= new List<(string, string)>();
            ctx.ExtraHeaders.Add(("X-Volt-Mw", "e2e"));
            return next();
        }).Use((ctx, next) =>
        {
            if (ctx.Path.StartsWith("/admin", StringComparison.Ordinal))
            {
                ctx.StatusCode = 403;
                ctx.ResponseStarted = true;
                return Task.CompletedTask; // short-circuit: the page never renders
            }
            return next();
        }));
        _client = new HttpClient { BaseAddress = new Uri(_url) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        if (_app is not null) await _app.StopAsync();
        await (_app?.DisposeAsync() ?? ValueTask.CompletedTask);
    }

    [Fact]
    public async Task MiddlewareHeaders_ReachTheClient()
    {
        using var res = await _client.GetAsync("/about");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("e2e", res.Headers.GetValues("X-Volt-Mw").FirstOrDefault());
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("<h1>About</h1>", html);
    }

    [Fact]
    public async Task MiddlewareShortCircuit_Returns403_WithoutPageRender()
    {
        using var res = await _client.GetAsync("/admin/secret");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Equal("", body); // the page (404 route or real page) never rendered
    }

    [Fact]
    public async Task Middleware_RunsForActions_Too()
    {
        // POST island action goes through the same onion
        using var res = await _client.PostAsync("/_volt/action", new StringContent(
            JsonSerializer.Serialize(new { island = "Todo", sid = "i0", action = "add" }),
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")));
        // the action exists and runs — but our middleware header must be present either way
        Assert.Contains("X-Volt-Mw", res.Headers.Select(h => h.Key));
    }
}

/// <summary>Custom error handler (OnException) replaces the default error page.</summary>
public sealed class OnExceptionE2ETests : IAsyncLifetime
{
    private WebApplication? _app;
    private HttpClient _client = null!;
    private string _url = null!;

    public async Task InitializeAsync()
    {
        (_url, _app) = await VoltApp.StartTestServerAsync(new VoltOptions
        {
            BaseUrl = "http://127.0.0.1",
            OnException = (ctx, ex) =>
            {
                ctx.StatusCode = 599;
                ctx.ContentType = "text/plain; charset=utf-8";
                ctx.HasBody = true;
                ctx.ResponseStarted = true;
                if (ctx.Output is { } output)
                {
                    var span = output.GetSpan(8);
                    "handled"u8.CopyTo(span);
                    output.Advance("handled"u8.Length);
                }
                return Task.CompletedTask;
            },
        });
        _client = new HttpClient { BaseAddress = new Uri(_url) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        if (_app is not null) await _app.StopAsync();
        await (_app?.DisposeAsync() ?? ValueTask.CompletedTask);
    }

    [Fact]
    public async Task CustomErrorPage_ReplacesDefault5xx()
    {
        using var res = await _client.GetAsync("/boom");
        Assert.Equal(599, (int)res.StatusCode);
        Assert.Equal("handled", await res.Content.ReadAsStringAsync());
    }
}
