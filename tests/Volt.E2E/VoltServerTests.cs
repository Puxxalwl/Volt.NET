using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Volt.E2E;

/// <summary>E2E coverage for the built-in zero-allocation server (M2 transport).</summary>
public sealed class VoltServerTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _fx;
    public VoltServerTests(ServerFixture fx) => _fx = fx;

    [Fact]
    public async Task Index_ServesHtml_WithIslandMarkup()
    {
        using var http = new HttpClient();
        var html = await http.GetStringAsync(_fx.Url + "/");
        Assert.Contains("<volt-island", html);
        Assert.Contains("data-v", html);
        Assert.Contains("hydrate.js", html);
        Assert.Contains("<!DOCTYPE html>", html);
    }

    [Fact]
    public async Task StaticPage_Serves200()
    {
        using var http = new HttpClient();
        using var res = await http.GetAsync(_fx.Url + "/about");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/html; charset=utf-8", res.Content.Headers.ContentType?.ToString());
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("About", html);
    }

    [Fact]
    public async Task DynamicRoute_ServesContent()
    {
        using var http = new HttpClient();
        var html = await http.GetStringAsync(_fx.Url + "/blog/hello-volt");
        Assert.Contains("hello-volt", html);
    }

    [Fact]
    public async Task UnknownRoute_Serves404Page()
    {
        using var http = new HttpClient();
        using var res = await http.GetAsync(_fx.Url + "/definitely-not-a-page");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("404", html);
    }

    [Fact]
    public async Task HydrateJs_ServesWithImmutableCache_And304()
    {
        using var http = new HttpClient();
        using var first = await http.GetAsync(_fx.Url + "/_volt/hydrate.js");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("text/javascript; charset=utf-8", first.Content.Headers.ContentType?.ToString());
        Assert.Equal("public, max-age=31536000, immutable", first.Headers.CacheControl?.ToString());
        var etag = first.Headers.ETag?.Tag;
        Assert.NotNull(etag);

        using var req = new HttpRequestMessage(HttpMethod.Get, _fx.Url + "/_volt/hydrate.js");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var second = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Theory]
    [InlineData("/sitemap.xml")]
    [InlineData("/robots.txt")]
    public async Task SeoEndpoints_Serve200(string path)
    {
        using var http = new HttpClient();
        using var res = await http.GetAsync(_fx.Url + path);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        if (path.Contains("sitemap")) Assert.Contains("<urlset", body);
        else Assert.Contains("Sitemap:", body);
    }

    [Fact]
    public async Task StaticAsset_ServesWithContentType()
    {
        using var http = new HttpClient();
        using var res = await http.GetAsync(_fx.Url + "/test.css");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/css; charset=utf-8", res.Content.Headers.ContentType?.ToString());
        var css = await res.Content.ReadAsStringAsync();
        Assert.Contains("color", css);
    }

    [Fact]
    public async Task Post_ToPage_Returns405()
    {
        using var http = new HttpClient();
        using var res = await http.PostAsync(_fx.Url + "/", new StringContent("x"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, res.StatusCode);
        Assert.Contains("GET,HEAD", string.Join(",", res.Content.Headers.Allow));
    }

    [Fact]
    public async Task Head_Request_HasNoBody_ButLength()
    {
        using var http = new HttpClient();
        using var res = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, _fx.Url + "/about"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Equal(string.Empty, body);
        Assert.True(res.Content.Headers.ContentLength is > 0);
    }

    [Fact]
    public async Task KeepAlive_ServesMultipleRequestsOnOneConnection()
    {
        using var handler = new SocketsHttpHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri(_fx.Url) };
        for (int i = 0; i < 5; i++)
        {
            using var res = await http.GetAsync("/about");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }
    }

    [Fact]
    public async Task Action_DispatchesAndReturnsFragment()
    {
        using var http = new HttpClient();
        var payload = """{"island":"Todo","sid":"i0","action":"toggle","state":{"text":"buy milk","count":0,"done":false},"args":{}}""";
        using var res = await http.PostAsync(_fx.Url + "/_volt/action",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var fragmentHtml = doc.RootElement.GetProperty("html").GetString();
        Assert.NotNull(fragmentHtml);
        Assert.Contains("[done]", fragmentHtml);
    }

    [Fact]
    public async Task Action_UnknownIsland_Returns404()
    {
        using var http = new HttpClient();
        var payload = """{"island":"Ghost","sid":"i0","action":"x","state":{},"args":{}}""";
        using var res = await http.PostAsync(_fx.Url + "/_volt/action",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Action_MalformedJson_Returns400()
    {
        using var http = new HttpClient();
        using var res = await http.PostAsync(_fx.Url + "/_volt/action",
            new StringContent("{not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task NoJsFallback_RedirectsWithToken_AndRendersState()
    {
        using var noRedirect = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        var form = new Dictionary<string, string>
        {
            ["__volt_island"] = "Todo",
            ["__volt_state"] = """{"text":"buy milk","count":0,"done":false}""",
            ["__volt_sid"] = "i0",
            ["__volt_action"] = "toggle",
        };
        using var post = await noRedirect.PostAsync(_fx.Url + "/_volt/fallback", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.SeeOther, post.StatusCode);
        var location = post.Headers.Location?.ToString();
        Assert.NotNull(location);
        Assert.Contains("__v=", location);

        // follow the token: state must be overridden in the island markup
        using var page = await noRedirect.GetAsync(_fx.Url + location);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("&quot;done&quot;:true", html);
    }

    [Fact]
    public async Task SsgCache_ServesSameEtagAcrossRequests()
    {
        using var http = new HttpClient();
        using var first = await http.GetAsync(_fx.Url + "/about");
        var etag1 = first.Headers.ETag?.Tag;
        using var second = await http.GetAsync(_fx.Url + "/about");
        var etag2 = second.Headers.ETag?.Tag;
        Assert.Equal(etag1, etag2);
    }

    [Fact]
    public async Task MalformedRequestLine_ClosesConnectionWith400()
    {
        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        await socket.ConnectAsync(new Uri(_fx.Url).Host, new Uri(_fx.Url).Port);
        await socket.SendAsync("GARBAGE\r\n\r\n"u8.ToArray(), System.Net.Sockets.SocketFlags.None);
        var buffer = new byte[256];
        int n = await socket.ReceiveAsync(buffer, System.Net.Sockets.SocketFlags.None);
        var response = Encoding.ASCII.GetString(buffer, 0, n);
        Assert.StartsWith("HTTP/1.1 400", response);
    }
}

public sealed class ServerFixture : IAsyncLifetime
{
    public string Url { get; private set; } = "";
    private VoltServerHandle? _handle;

    public async Task InitializeAsync()
    {
        (Url, _handle) = await VoltServerApp.StartTestServerAsync(new VoltOptions
        {
            DevMode = true,
            BaseUrl = "http://127.0.0.1",
        });
    }

    public Task DisposeAsync()
    {
        _handle?.Dispose();
        return Task.CompletedTask;
    }
}
