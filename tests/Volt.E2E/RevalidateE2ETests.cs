using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Volt;
using Xunit;

namespace Volt.E2E;

/// <summary>M6: on-demand revalidation — @tag + POST /_volt/revalidate + VoltRuntime.RevalidateTag.</summary>
public sealed class RevalidateE2ETests : IAsyncLifetime
{
    private string _url = null!;
    private WebApplication _app = null!;

    public async Task InitializeAsync()
    {
        (_url, _app) = await VoltApp.StartTestServerAsync(new VoltOptions
        {
            RevalidateToken = "secret-token",
            BaseUrl = "http://127.0.0.1",
        });
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private FormUrlEncodedContent Form(params (string, string)[] fields)
        => new(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));

    [Fact]
    public async Task TagEviction_RendersPageFresh_OnNextRequest()
    {
        using var http = new HttpClient();
        var first = await http.GetAsync(_url + "/tagged"); // fills the SSG cache
        Assert.True(first.IsSuccessStatusCode);

        // runtime API: evict by tag
        int evicted = VoltRuntime.RevalidateTag("products");
        Assert.Equal(1, evicted);

        // second evict: nothing left
        Assert.Equal(0, VoltRuntime.RevalidateTag("products"));

        // page still serves after eviction (re-render + refill)
        var second = await http.GetAsync(_url + "/tagged");
        Assert.True(second.IsSuccessStatusCode);
    }

    [Fact]
    public async Task RevalidateEndpoint_EvictsAndReturnsCount()
    {
        using var http = new HttpClient();
        await http.GetAsync(_url + "/tagged"); // fill the cache

        using var res = await http.PostAsync(_url + "/_volt/revalidate",
            Form(("tag", "catalog"), ("token", "secret-token")));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("1", (await res.Content.ReadAsStringAsync()).Trim());
    }

    [Fact]
    public async Task RevalidateEndpoint_RejectsBadToken()
    {
        using var http = new HttpClient();
        await http.GetAsync(_url + "/tagged");

        using var res = await http.PostAsync(_url + "/_volt/revalidate",
            Form(("tag", "catalog"), ("token", "wrong")));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task RevalidateEndpoint_TokenAlsoViaHeader()
    {
        using var http = new HttpClient();
        await http.GetAsync(_url + "/tagged");

        using var content = Form(("tag", "catalog"));
        using var request = new HttpRequestMessage(HttpMethod.Post, _url + "/_volt/revalidate") { Content = content };
        request.Headers.Add("X-Volt-Token", "secret-token");
        using var res = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task RevalidateEndpoint_DisabledWithoutToken_Is404()
    {
        var (url, app) = await VoltApp.StartTestServerAsync(new VoltOptions { BaseUrl = "http://127.0.0.1" });
        try
        {
            using var http = new HttpClient();
            using var res = await http.PostAsync(url + "/_volt/revalidate", Form(("tag", "x")));
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }
}
