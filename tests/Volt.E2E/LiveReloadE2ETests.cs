using System.Net;
using Microsoft.AspNetCore.Builder;
using Volt;
using Xunit;

namespace Volt.E2E;

/// <summary>M6: DevMode live reload — /_volt/ping poll target + script injection.</summary>
public sealed class LiveReloadE2ETests : IAsyncLifetime
{
    private string _url = null!;
    private WebApplication _app = null!;

    public async Task InitializeAsync()
    {
        (_url, _app) = await VoltApp.StartTestServerAsync(new VoltOptions
        {
            DevMode = true,
            BaseUrl = "http://127.0.0.1",
        });
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task DevPages_ContainLiveReloadPollScript()
    {
        using var http = new HttpClient();
        var html = await http.GetStringAsync(_url + "/about"); // SSR page
        Assert.Contains("/_volt/ping", html);
        Assert.Contains("location.reload()", html);
    }

    [Fact]
    public async Task DevSsgPages_RenderFresh_ContainScript_Too()
    {
        using var http = new HttpClient();
        // /layouted is SSG — in DevMode the cache is bypassed and the script is injected
        var html = await http.GetStringAsync(_url + "/layouted");
        Assert.Contains("<h1>", html);
        Assert.Contains("/_volt/ping", html);
    }

    [Fact]
    public async Task Ping_ReturnsStamp_NoStore()
    {
        using var http = new HttpClient();
        using var res = await http.GetAsync(_url + "/_volt/ping");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", res.Content.Headers.ContentType?.ToString());
        Assert.Equal("no-store", res.Headers.CacheControl?.ToString());
        var stamp1 = await res.Content.ReadAsStringAsync();
        Assert.NotEqual("", stamp1);
    }

    [Fact]
    public async Task Ping_StampChanges_AfterNotifyChanged()
    {
        using var http = new HttpClient();
        var stamp1 = await http.GetStringAsync(_url + "/_volt/ping");
        VoltLiveReload.NotifyChanged();
        var stamp2 = await http.GetStringAsync(_url + "/_volt/ping");
        Assert.NotEqual(stamp1, stamp2);
    }

    [Fact]
    public async Task Ping_Is404_WhenNotDevMode()
    {
        var (url, app) = await VoltApp.StartTestServerAsync(new VoltOptions { DevMode = false });
        try
        {
            using var http = new HttpClient();
            using var res = await http.GetAsync(url + "/_volt/ping");
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }
}
