using Volt.Testing;
using Xunit;

namespace Volt.Testing.Tests;

/// <summary>
/// The Volt.Testing package testing itself: in-process GET/POST, golden-file
/// snapshots (VOLT_UPDATE_SNAPSHOTS=1 recreates __snapshots__/).
/// </summary>
public sealed class VoltTestServerTests
{
    [Fact]
    public async Task GetAsync_RendersSsrPage()
    {
        using var server = VoltTestServer.Create();
        var response = await server.GetAsync("/");

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.ContentType);
        Assert.Contains("<h1>Home</h1>", response.Body);
        Assert.Contains("<li>item 2</li>", response.Body);
    }

    [Fact]
    public async Task Snapshot_Home_MatchesGoldenFile()
    {
        using var server = VoltTestServer.Create();
        var response = await server.GetAsync("/");
        response.AssertMatchesSnapshot();
    }

    [Fact]
    public async Task Snapshot_TemplatedVoltPage_MatchesGoldenFile()
    {
        using var server = VoltTestServer.Create();
        var response = await server.GetAsync("/templated");
        response.AssertMatchesSnapshot();
    }

    [Fact]
    public async Task PostAsync_RunsFormFlow_WithoutSockets()
    {
        using var server = VoltTestServer.Create();
        var response = await server.PostAsync("/hello", ("email", "nope"));

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("data-field=\"Email\"", response.Body);
        Assert.Contains("must be an email address", response.Body);
    }

    [Fact]
    public async Task PostAsync_ValidForm_RendersSuccess()
    {
        using var server = VoltTestServer.Create();
        var response = await server.PostAsync("/hello", ("email", "hi@volt.dev"));

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("class=\"saved\"", response.Body);
    }

    [Fact]
    public async Task NotFound_Renders404()
    {
        using var server = VoltTestServer.Create();
        var response = await server.GetAsync("/missing-page");

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task Sitemap_IsDeterministic_InSnapshot()
    {
        using var server = VoltTestServer.Create(options => options.BaseUrl = "http://snap.test");
        var response = await server.GetAsync("/sitemap.xml");
        response.AssertMatchesSnapshot();
    }
}
