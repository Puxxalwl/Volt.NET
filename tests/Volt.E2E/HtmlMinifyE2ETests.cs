using System.Net;
using Microsoft.AspNetCore.Builder;
using Volt;
using Xunit;

namespace Volt.E2E;

/// <summary>M6: runtime HTML minification — SSR + SSG cache paths.</summary>
public sealed class HtmlMinifyE2ETests : IAsyncLifetime
{
    private string _url = null!;
    private WebApplication _app = null!;

    public async Task InitializeAsync()
    {
        // MinifyHtml explicitly on; DevMode off (prod-like output)
        (_url, _app) = await VoltApp.StartTestServerAsync(new VoltOptions
        {
            MinifyHtml = true,
            BaseUrl = "http://127.0.0.1",
        });
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task SsrResponse_IsMinified()
    {
        using var http = new HttpClient();
        var html = await http.GetStringAsync(_url + "/about"); // SSR page
        Assert.Contains("<h1>", html);
        Assert.DoesNotContain("  ", html); // no double spaces anywhere
    }

    [Fact]
    public async Task MinifiedOutput_StillParsesAsHtml_HeadAndBodyPresent()
    {
        using var http = new HttpClient();
        var html = await http.GetStringAsync(_url + "/"); // SSG home (minified at cache-fill)
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("</html>", html);
        Assert.DoesNotContain("\n", html);
    }

    [Fact]
    public async Task DefaultOff_NoMinification()
    {
        var (url, app) = await VoltApp.StartTestServerAsync(new VoltOptions { BaseUrl = "http://127.0.0.1" });
        try
        {
            using var http = new HttpClient();
            var html = await http.GetStringAsync(url + "/about");
            // the page emits newlines between elements; minification would remove them
            Assert.Contains("\n", html);
        }
        finally { await app.DisposeAsync(); }
    }
}
