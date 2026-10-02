using System.Text;
using Volt;
using Xunit;

namespace Volt.Core.Tests;

public class FallbackStoreTests
{
    [Fact]
    public void PutReturnsTokenAndGetReturnsState()
    {
        var store = new VoltFallbackStore();
        var token = store.Put("Counter", "i0", "{\"count\":42}"u8.ToArray());
        Assert.Equal(32, token.Length);
        Assert.True(VoltFallbackStore.IsHexToken(token));

        var state = store.Get(token, "Counter", "i0");
        Assert.NotNull(state);
        Assert.Equal("{\"count\":42}", Encoding.UTF8.GetString(state!));
    }

    [Fact]
    public void TokenIsSingleUse()
    {
        var store = new VoltFallbackStore();
        var token = store.Put("Counter", "i0", "{}"u8.ToArray());
        Assert.NotNull(store.Get(token, "Counter", "i0"));
        Assert.Null(store.Get(token, "Counter", "i0")); // consumed
        Assert.Null(store.Get(token, "Counter", "i0"));
    }

    [Fact]
    public void GetRequiresMatchingIslandAndSid()
    {
        var store = new VoltFallbackStore();
        var token = store.Put("Counter", "i0", "{}"u8.ToArray());
        Assert.Null(store.Get(token, "Counter", "i9")); // wrong sid
        Assert.Null(store.Get(token, "Other", "i0"));   // wrong island
        // failed lookups do not consume the token
        Assert.NotNull(store.Get(token, "Counter", "i0"));
    }

    [Fact]
    public void UnknownTokenFails()
    {
        var store = new VoltFallbackStore();
        Assert.Null(store.Get("00000000000000000000000000000000", "Counter", "i0"));
        Assert.Null(store.Get(null, "Counter", "i0"));
        Assert.Null(store.Get("", "Counter", "i0"));
        Assert.Null(store.Get("not-hex!", "Counter", "i0"));
    }

    [Fact]
    public void TokensAreUnique()
    {
        var store = new VoltFallbackStore();
        var tokens = new HashSet<string>();
        for (int i = 0; i < 100; i++)
            tokens.Add(store.Put("Counter", "i0", "{}"u8.ToArray()));
        Assert.Equal(100, tokens.Count);
    }

    [Fact]
    public void ExpiredEntriesEvicted()
    {
        var store = new VoltFallbackStore(ttl: TimeSpan.FromMilliseconds(1));
        var token = store.Put("Counter", "i0", "{}"u8.ToArray());
        Thread.Sleep(20);
        Assert.Null(store.Get(token, "Counter", "i0"));
    }

    [Fact]
    public void CapacityBoundEvictsOldest()
    {
        var store = new VoltFallbackStore(capacity: 4, ttl: TimeSpan.FromMinutes(10));
        var first = store.Put("Counter", "i0", "{}"u8.ToArray());
        for (int i = 1; i <= 8; i++)
            store.Put("Counter", "i" + i, "{}"u8.ToArray());
        Assert.Null(store.Get(first, "Counter", "i0")); // evicted
    }
}

public class SitemapTests
{
    private sealed class BlankPage : VoltPage
    {
        public override void Render(HtmlWriter w, RenderContext ctx) { }
    }

    private static RouteRegistry BuildRegistry(params string[] patterns)
    {
        var registry = new RouteRegistry();
        foreach (var pattern in patterns)
            registry.Add(pattern, static () => new BlankPage());
        return registry;
    }

    [Fact]
    public void BuildXmlListsStaticRoutes()
    {
        var registry = BuildRegistry("/", "/about", "/blog/{slug}");
        var xml = Encoding.UTF8.GetString(Sitemap.BuildXml(registry, "https://example.com"));
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", xml);
        Assert.Contains("<loc>https://example.com/about</loc>", xml);
        Assert.DoesNotContain("blog", xml); // dynamic routes excluded (no StaticPaths)
        Assert.EndsWith("</urlset>", xml.TrimEnd());
    }

    [Fact]
    public void BuildXmlEscapesUrls()
    {
        var registry = BuildRegistry("/a&b");
        var xml = Encoding.UTF8.GetString(Sitemap.BuildXml(registry, "https://example.com"));
        Assert.Contains("<loc>https://example.com/a&amp;b</loc>", xml);
    }

    [Fact]
    public void BuildRobotsReferencesSitemap()
    {
        var txt = Encoding.UTF8.GetString(Sitemap.BuildRobots("https://example.com"));
        Assert.Contains("User-agent: *", txt);
        Assert.Contains("Allow: /", txt);
        Assert.Contains("Sitemap: https://example.com/sitemap.xml", txt);
    }
}
