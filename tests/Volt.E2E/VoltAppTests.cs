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
        Assert.Matches("/_volt/hydrate\\.[0-9a-f]{16}\\.js", html); // M4: versioned immutable URL
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

    // ---- M6: layouts + partials (compile-time splice, zero runtime cost) ----------

    [Fact]
    public async Task LayoutSplicesAroundSsgPage()
    {
        var html = await _client.GetStringAsync("/layouted");
        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("<title>Volt — layouted</title>", html);
        Assert.Contains("<nav class=\"site-nav\">layout-nav</nav>", html);
        Assert.Contains("<main>", html);
        Assert.Contains("<h1>Layouted index</h1>", html);
        Assert.Contains("<footer>layout-footer</footer>", html);
        // the body lands inside <main> — layout before, page, layout after
        Assert.True(html.IndexOf("site-nav") < html.IndexOf("Layouted index"));
        Assert.True(html.IndexOf("Layouted index") < html.IndexOf("layout-footer"));
    }

    [Fact]
    public async Task LayoutSplicesAroundSsrPage_AndPartialRendersModel()
    {
        var html = await _client.GetStringAsync("/layouted/about");
        Assert.Contains("<nav class=\"site-nav\">layout-nav</nav>", html);
        Assert.Contains("<h1>Layouted about</h1>", html);
        // partial call @Card(widget) inside the layouted page
        Assert.Contains("<div class=\"card\">", html);
        Assert.Contains("<h3>About widget</h3>", html);
        Assert.Contains("<span>7</span>", html);
    }

    [Fact]
    public async Task LayoutedRoutesAreInSitemap()
    {
        var sitemap = await _client.GetStringAsync("/sitemap.xml");
        Assert.Contains("/layouted</loc>", sitemap);
        Assert.Contains("/layouted/about</loc>", sitemap);
        // partials and layouts never become routes
        Assert.DoesNotContain("partials", sitemap);
    }

    // ---- M6: typed forms — bind + validate without JavaScript ----------------------

    private static FormUrlEncodedContent Form(params (string, string)[] fields)
        => new(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));

    [Fact]
    public async Task Form_InvalidEmail_ShowsErrorsWithoutJs()
    {
        using var res = await _client.PostAsync("/subscribe", Form(("email", "not-an-email"), ("name", "Ann")));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("field-error", html);
        Assert.Contains("data-field=\"Email\"", html);
        Assert.Contains("must be an email address", html);
        Assert.DoesNotContain("class=\"saved\"", html);
        // the submitted value survives re-render
        Assert.Contains("value=\"not-an-email\"", html);
        Assert.Contains("value=\"Ann\"", html);
    }

    [Fact]
    public async Task Form_MissingRequiredField_ReportsRequired()
    {
        using var res = await _client.PostAsync("/subscribe", Form(("name", "x")));
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("data-field=\"Email\"", html);
        Assert.Contains("email is required", html);
    }

    [Fact]
    public async Task Form_OutOfRange_ShowsRangeError()
    {
        using var res = await _client.PostAsync("/subscribe", Form(("email", "a@b.io"), ("rating", "9")));
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("data-field=\"Rating\"", html);
        Assert.Contains("must be between 1 and 5", html);
    }

    [Fact]
    public async Task Form_TooLongName_ShowsMaxLengthError()
    {
        using var res = await _client.PostAsync("/subscribe",
            Form(("email", "a@b.io"), ("name", new string('x', 21))));
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("data-field=\"Name\"", html);
        Assert.Contains("too long", html);
    }

    [Fact]
    public async Task Form_ValidSubmission_RendersSuccess_AndCheckboxParsesOn()
    {
        using var res = await _client.PostAsync("/subscribe",
            Form(("email", "hi@volt.dev"), ("rating", "4"), ("subscribe", "on"), ("name", "Ann")));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("class=\"saved\"", html);
        Assert.Contains("saved:hi@volt.dev", html);
        Assert.DoesNotContain("field-error", html);
        // checkbox "on" → true → re-render keeps checked
        Assert.Contains("checked=\"checked\"", html);
    }

    [Fact]
    public async Task Form_PostRedirectGet_Returns303WithLocation()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = _client.BaseAddress };
        using var res = await client.PostAsync("/login", Form(("user", "x")));
        Assert.Equal(HttpStatusCode.SeeOther, res.StatusCode);
        Assert.Equal("/about", res.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Form_PostToUnknownRoute_Is404()
    {
        using var res = await _client.PostAsync("/definitely-missing", Form(("a", "b")));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
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
        // the SSG cache (and its ETag) is a production feature — DevMode renders
        // every request fresh, so this test runs its own server with DevMode off
        var (url, app) = await VoltApp.StartTestServerAsync(new VoltOptions { BaseUrl = "http://127.0.0.1" });
        try
        {
            using var client = new HttpClient();
            var first = await client.GetAsync(url + "/");
            var etag = first.Headers.ETag?.Tag;
            Assert.NotNull(etag);
            Assert.DoesNotContain("/_volt/ping", await first.Content.ReadAsStringAsync()); // prod: no dev script

            var request = new HttpRequestMessage(HttpMethod.Get, url + "/");
            request.Headers.IfNoneMatch.ParseAdd(etag!);
            var second = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        }
        finally { await app.DisposeAsync(); }
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
    public async Task PostMethodOnPageRoute_RendersPage_M6()
    {
        // M6: POST reaches the page's OnPostAsync (form flow); a page without
        // a handler falls back to rendering — the old behavior was a bare 405
        var response = await _client.PostAsync("/", new StringContent(""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PutMethodIsStill405()
    {
        var response = await _client.PutAsync("/", new StringContent(""));
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
        Assert.Contains("</html>", html);
        // DevMode appends the live-reload poll script after </html> (valid: it
        // parses as trailing body content, and prod output never contains it)
        Assert.EndsWith("1000)})()</script>", html);
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
        Assert.Matches("/_volt/hydrate\\.[0-9a-f]{16}\\.js", html); // M4 versioned URL
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

    [Fact]
    public async Task HydrateJs_ServesImmutableVersionedUrl()
    {
        using var http = new HttpClient();
        // the page references the hashed URL
        var html = await http.GetStringAsync(_url + "/");
        var srcMatch = System.Text.RegularExpressions.Regex.Match(html, "src=\"([^\"]*hydrate[^\"]*)\"");
        Assert.True(srcMatch.Success, "page must reference hydrate.js");
        var src = srcMatch.Groups[1].Value;
        Assert.Matches("/_volt/hydrate\\.[0-9a-f]{16}\\.js", src);

        // the versioned asset is served with immutable caching
        using var res = await http.GetAsync(_url + src);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("public, max-age=31536000, immutable", res.Headers.CacheControl?.ToString());
        Assert.NotNull(res.Headers.ETag);

        // a wrong version must not be cached forever by a stale client
        using var stale = await http.GetAsync(_url + "/_volt/hydrate.deadbeef.js");
        Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
    }
}