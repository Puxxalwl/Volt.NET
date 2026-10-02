using System.Net;
using Microsoft.AspNetCore.Builder;
using Volt;
using Xunit;

namespace Volt.E2E;

/// <summary>M6: request metrics — /_volt/metrics (Prometheus text) + /_volt/hud.</summary>
public sealed class MetricsE2ETests : IAsyncLifetime
{
    private string _url = null!;
    private WebApplication _app = null!;

    public async Task InitializeAsync()
    {
        (_url, _app) = await VoltApp.StartTestServerAsync(new VoltOptions
        {
            EnableMetrics = true,
            BaseUrl = "http://127.0.0.1",
        });
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task<int> Counter(string name)
    {
        var text = await new HttpClient().GetStringAsync(_url + "/_volt/metrics");
        foreach (var line in text.Split('\n'))
            if (line.StartsWith(name + " "))
                return int.Parse(line[(name.Length + 1)..].TrimEnd('\r'));
        throw new Xunit.Sdk.XunitException($"counter {name} not found in:\n{text}");
    }

    [Fact]
    public async Task Metrics_ArePrometheusText_AndCountRequests()
    {
        using var http = new HttpClient();
        using var res = await http.GetAsync(_url + "/_volt/metrics");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", res.Content.Headers.ContentType?.ToString());
        var text = await res.Content.ReadAsStringAsync();
        Assert.StartsWith("volt_requests_total ", text);
        Assert.Contains("volt_request_us_bucket{le=\"10\"} ", text);
        Assert.Contains("volt_request_us_bucket{le=\"+Inf\"} ", text);
        Assert.Contains("volt_responses_2xx_total ", text);

        var before = await Counter("volt_requests_total");
        await http.GetAsync(_url + "/about");
        await http.GetAsync(_url + "/definitely-404");
        var after = await Counter("volt_requests_total");
        Assert.True(after >= before + 3, $"requests counter didn't grow: {before} → {after}");

        var notFound = await Counter("volt_responses_4xx_total");
        Assert.True(notFound >= 1, "404 status class not counted");
    }

    [Fact]
    public async Task SsgHits_AreCounted()
    {
        using var http = new HttpClient();
        await http.GetAsync(_url + "/"); // first request fills the SSG cache (miss)
        await http.GetAsync(_url + "/"); // second request is a cache hit
        var hits = await Counter("volt_ssg_hits_total");
        Assert.True(hits >= 1, "SSG cache hit not counted");
    }

    [Fact]
    public async Task Hud_IsALiveHtmlPage()
    {
        using var http = new HttpClient();
        using var res = await http.GetAsync(_url + "/_volt/hud");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/html; charset=utf-8", res.Content.Headers.ContentType?.ToString());
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("volt metrics", html);
        Assert.Contains("volt_requests_total", html);
        Assert.Contains("http-equiv=\"refresh\"", html); // self-refreshing, zero JS
    }

    [Fact]
    public async Task Metrics_OffByDefault_Is404()
    {
        var (url, app) = await VoltApp.StartTestServerAsync(new VoltOptions { BaseUrl = "http://127.0.0.1" });
        try
        {
            using var http = new HttpClient();
            using var res = await http.GetAsync(url + "/_volt/metrics");
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            using var hud = await http.GetAsync(url + "/_volt/hud");
            Assert.Equal(HttpStatusCode.NotFound, hud.StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }
}
