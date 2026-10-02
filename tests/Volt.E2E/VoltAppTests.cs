using System.Net;
using Microsoft.AspNetCore.Builder;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Volt;
using Xunit;

namespace Volt.E2E;

public sealed class VoltAppTests : IAsyncLifetime
{
    private WebApplication? _app;
    private HttpClient _client = null!;
    private string _url = null!;

    public async Task InitializeAsync()
    {
        (_url, _app) = await VoltApp.StartTestServerAsync(new VoltOptions
        {
            DevMode = true,
            BaseUrl = "http://127.0.0.1",
            EnableSitemap = true,
            EnableRobots = true,
        });
        _client = new HttpClient { BaseAddress = new Uri(_url) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        if (_app is not null) await _app.StopAsync();
        await (_app?.DisposeAsync() ?? ValueTask.CompletedTask);
    }

    // ------------------------------------------------------------------

    [Fact]
    public async Task IndexRendersWithIslandMarkup()
    {
        var html = await _client.GetStringAsync("/");
        Assert.Contains("<title>E2E home</title>", html);
        Assert.Contains("<volt-island data-v=\"Todo\" data-sid=\"i0\"", html);
        Assert.Contains("data-props=\"{&quot;text&quot;:&quot;hello&quot;,&quot;count&quot;:0,&quot;done&quot;:false}\"", html);
        Assert.Contains("action=\"/_volt/fallback\"", html);
        Assert.Contains("name=\"__volt_island\" value=\"Todo\"", html);
        Assert.Contains("/_volt/hydrate.js?v=", html);
        Assert.Contains("hello #0", html);
    }

    [Fact]
    public async Task SsrPageRendersPerRequest()
    {
        var html = await _client.GetStringAsync("/about");
        Assert.Contains("<h1>About</h1>", html);
        Assert.DoesNotContain("volt-island", html);
    }

    [Fact]
    public async Task DynamicSsgRouteResolvesParams()
    {
        Assert.Contains("<h1>Post: first</h1>", await _client.GetStringAsync("/blog/first"));
        Assert.Contains("<h1>Post: second</h1>", await _client.GetStringAsync("/blog/second"));
    }

    [Fact]
    public async Task UnknownRouteReturns404Page()
    {
        var response = await _client.GetAsync("/definitely-missing");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<h1>404</h1>", html);
    }

    [Fact]
    public async Task FailingPageReturns500Page()
    {
        var response = await _client.GetAsync("/boom");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<h1>Error</h1>", html);
    }

    [Fact]
    public async Task HydrateScriptServedWithImmutableCache()
    {
        var response = await _client.GetAsync("/_volt/hydrate.js");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString() ?? "");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("volt-island", body);
    }

    [Fact]
    public async Task SsgPageServesEtagAnd304()
    {
        var first = await _client.GetAsync("/");
        var etag = first.Headers.ETag?.Tag;
        Assert.NotNull(etag);

        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.IfNoneMatch.ParseAdd(etag!);
        var second = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    // ------------------------------------------------------------------
    // islands: action + no-JS fallback
    // ------------------------------------------------------------------

    private static StringContent JsonBody(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static HttpClient NoRedirectClient(string url) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
    })
    { BaseAddress = new Uri(url) };

    [Fact]
    public async Task ActionTogglesBoolState()
    {
        var response = await _client.PostAsync("/_volt/action", JsonBody(new
        {
            island = "Todo",
            sid = "i0",
            action = "toggle",
            state = new { text = "hello", count = 0, done = false },
            args = new { },
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var html = payload.GetProperty("html").GetString()!;
        Assert.Contains("data-props=\"{&quot;text&quot;:&quot;hello&quot;,&quot;count&quot;:0,&quot;done&quot;:true}\"", html);
        Assert.Contains("[done]", html);
    }

    [Fact]
    public async Task ActionWithoutArgsWorks()
    {
        var response = await _client.PostAsync("/_volt/action", JsonBody(new
        {
            island = "Todo",
            sid = "i0",
            action = "bump",
            state = new { text = "hello", count = 41, done = false },
            args = new { },
        }));
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var html = payload.GetProperty("html").GetString()!;
        Assert.Contains("hello #42", html);
    }

    [Fact]
    public async Task ActionCanUseArgs()
    {
        var response = await _client.PostAsync("/_volt/action", JsonBody(new
        {
            island = "Todo",
            sid = "i0",
            action = "withargs",
            state = new { text = "hello", count = 0, done = false },
            args = new { text = "renamed" },
        }));
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var html = payload.GetProperty("html").GetString()!;
        Assert.Contains("renamed #0", html);
    }

    [Fact]
    public async Task UnknownIslandReturns404()
    {
        var response = await _client.PostAsync("/_volt/action", JsonBody(new
        {
            island = "Nope", sid = "i0", action = "x", state = new { }, args = new { },
        }));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnknownActionReturns404()
    {
        var response = await _client.PostAsync("/_volt/action", JsonBody(new
        {
            island = "Todo", sid = "i0", action = "nope", state = new { text = "a", count = 0, done = false }, args = new { },
        }));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MalformedActionBodyReturns400()
    {
        var response = await _client.PostAsync("/_volt/action",
            new StringContent("{not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NoJsFallbackFlowRestoresState()
    {
        // 1. POST the form (as a JS-less browser would)
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__volt_island"] = "Todo",
            ["__volt_state"] = "{\"text\":\"hello\",\"count\":7,\"done\":false}",
            ["__volt_sid"] = "i0",
            ["__volt_action"] = "bump",
        });
        using var noRedirect = NoRedirectClient(_url);
        using var redirect = new HttpRequestMessage(HttpMethod.Post, "/_volt/fallback");
        redirect.Content = form;
        redirect.Headers.Referrer = new Uri(_url + "/");
        var redirectResponse = await noRedirect.SendAsync(redirect);
        Assert.Equal(HttpStatusCode.SeeOther, redirectResponse.StatusCode);
        var location = redirectResponse.Headers.Location?.ToString() ?? "";
        Assert.StartsWith("/", location);
        Assert.Contains("__v=", location);

        // 2. follow the redirect: island state restored
        var html = await _client.GetStringAsync(location);
        Assert.Contains("hello #8", html);
    }

    // ------------------------------------------------------------------
    // SEO endpoints
    // ------------------------------------------------------------------

    [Fact]
    public async Task SitemapListsStaticAndDynamicSsgRoutes()
    {
        var xml = await _client.GetStringAsync("/sitemap.xml");
        Assert.StartsWith("<?xml", xml);
        Assert.Contains("<loc>http://127.0.0.1/</loc>", xml);
        Assert.Contains("<loc>http://127.0.0.1/about</loc>", xml);
        Assert.Contains("<loc>http://127.0.0.1/blog/first</loc>", xml);
        Assert.Contains("<loc>http://127.0.0.1/blog/second</loc>", xml);
        Assert.Contains("<loc>http://127.0.0.1/boom</loc>", xml); // SSR routes are indexable
        Assert.DoesNotContain("{slug}", xml); // raw patterns never leak
    }

    [Fact]
    public async Task RobotsTxtServed()
    {
        var txt = await _client.GetStringAsync("/robots.txt");
        Assert.Contains("User-agent: *", txt);
        Assert.Contains("Sitemap:", txt);
    }

    [Fact]
    public async Task PostMethodOnPageRouteReturns405()
    {
        var response = await _client.PostAsync("/", new StringContent(""));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task VoltTemplatePage_RendersMarkupCodeAndExpressions()
    {
        using var http = new HttpClient();
        var html = await http.GetStringAsync(_url + "/templated");
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<html lang=\"en\">", html);
        Assert.Contains("<h1 class=\"headline\">From a .volt template</h1>", html);
        Assert.Contains("<li>Item 1 of 3</li>", html);
        Assert.Contains("<li>Item 3 of 3</li>", html);
        Assert.Contains("<p id=\"path-echo\">path: /templated</p>", html);
        Assert.Contains("@literal", html);
        Assert.EndsWith("</html>", html);
    }

    [Fact]
    public async Task VoltIslandNode_InTemplatePage_RendersFormAndState()
    {
        using var http = new HttpClient();
        var html = await http.GetStringAsync(_url + "/volt-isle");
        // the island registry path emits the full CONTRACT form markup
        Assert.Contains("<form method=\"post\" action=\"/_volt/fallback\" data-v-form>", html);
        Assert.Contains("<volt-island", html);
        Assert.Contains("data-v=\"Todo\"", html);
        Assert.Contains("wire state #2", html);
        Assert.Contains("hydrate.js", html);
    }

    [Fact]
    public async Task WasmIsland_MarkupCarriesWasmModuleUrl()
    {
        using var http = new HttpClient();
        var html = await http.GetStringAsync(_url + "/wasm-isle");
        Assert.Contains("<volt-island", html);
        Assert.Contains("data-v=\"Calc\"", html);
        Assert.Contains("data-v-wasm=\"/islands/calc.wasm\"", html);
        Assert.Contains("data-props=\"{&quot;value&quot;:7}\"", html);
    }
}