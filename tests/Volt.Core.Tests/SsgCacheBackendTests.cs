using System.Text;
using Volt;
using Xunit;

namespace Volt.Core.Tests;

/// <summary>
/// M4 shared SSG cache: file backend semantics, cross-instance visibility through
/// VoltEngine (render counter proves the second engine serves from the shared store),
/// and persistence across backend instances.
/// </summary>
public sealed class SsgCacheBackendTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "volt-ssg-" + Guid.NewGuid().ToString("N"));
    private static int _renderCount;

    private sealed class CountingPage : VoltPage
    {
        public override RenderMode Mode => RenderMode.SSG;

        public override void Render(HtmlWriter w, RenderContext ctx)
        {
            Interlocked.Increment(ref _renderCount);
            using (w.El("p"))
            {
                w.Text("shared ssg " + _renderCount);
            }
        }
    }

    private static string NewRoute()
    {
        var route = "/__ssg_" + Guid.NewGuid().ToString("N");
        VoltRuntime.Routes.Add(route, static () => new CountingPage());
        return route;
    }

    private static async Task<VoltHttpContext> ServeAsync(VoltOptions options, string path)
    {
        var ctx = new VoltHttpContext { Method = "GET", Path = path, Output = new PooledBufferWriter() };
        await VoltEngine.HandleAsync(ctx, options);
        return ctx;
    }

    [Fact]
    public void FileBackend_SaveAndLoad_RoundTrips()
    {
        var backend = new FileSsgCacheBackend(_dir);
        var html = "<p>hello</p>"u8.ToArray();
        backend.Save("/x", html, "\"abc\"", 1_680_000_000_000_000_000L, 42);

        Assert.True(backend.TryLoad("/x", out var loadedHtml, out var etag, out var ticks, out var revalidate));
        Assert.Equal(html, loadedHtml);
        Assert.Equal("\"abc\"", etag);
        Assert.Equal(1_680_000_000_000_000_000L, ticks);
        Assert.Equal(42, revalidate);
    }

    [Fact]
    public void FileBackend_Load_MissesUnknownPaths()
    {
        var backend = new FileSsgCacheBackend(_dir);
        Assert.False(backend.TryLoad("/missing", out _, out _, out _, out _));
    }

    [Fact]
    public async Task Engine_SecondInstance_ServesFromSharedBackend_WithoutRerender()
    {
        var route = NewRoute();
        var options = new VoltOptions { SsgCacheDirectory = _dir };

        // instance A renders once → persists into the shared file backend
        int before = _renderCount;
        var first = await ServeAsync(options, route);
        Assert.Equal(200, first.StatusCode);
        Assert.Equal(before + 1, _renderCount);

        // instance B: fresh engine state (different identity → separate in-memory LRU)
        var optionsB = new VoltOptions { SsgCacheDirectory = _dir, SsgCacheCapacity = 64 };
        var second = await ServeAsync(optionsB, route);
        Assert.Equal(200, second.StatusCode);
        Assert.Equal(before + 1, _renderCount); // served from the shared backend, not re-rendered
    }

    [Fact]
    public async Task FileBackend_PersistsAcrossBackendInstances()
    {
        var route = NewRoute();
        var options = new VoltOptions { SsgCacheDirectory = _dir };

        var first = await ServeAsync(options, route);
        Assert.Equal(200, first.StatusCode);
        int renderedAfterFirst = _renderCount;

        // brand-new backend over the same directory (simulates a restart)
        var backend = new FileSsgCacheBackend(_dir);
        Assert.True(backend.TryLoad(route, out var html, out var etag, out _, out _));
        Assert.True(html.Length > 0);
        Assert.StartsWith("\"", etag);
        Assert.Equal(renderedAfterFirst, _renderCount);
    }

    [Fact]
    public void FileBackend_AtomicWrites_ProduceOneFilePerPage()
    {
        var backend = new FileSsgCacheBackend(_dir);
        var html = Encoding.UTF8.GetBytes("<p>stable</p>");
        for (int i = 0; i < 5; i++)
            backend.Save("/stable", html, "\"etag" + i + "\"", 1000L, 5);

        var files = System.IO.Directory.GetFiles(_dir, "*.volt");
        Assert.Single(files); // overwritten, not appended
        Assert.True(backend.TryLoad("/stable", out _, out var etag, out _, out _));
        Assert.Equal("\"etag4\"", etag);
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
