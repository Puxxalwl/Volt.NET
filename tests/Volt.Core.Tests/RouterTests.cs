using Volt;
using Xunit;

namespace Volt.Core.Tests;

public class RouterTests
{
    private static RouteRegistry Build(params string[] patterns)
    {
        var registry = new RouteRegistry();
        foreach (var pattern in patterns)
            registry.Add(pattern, static () => null!);
        return registry;
    }

    private static bool Found(RouteRegistry registry, string path)
    {
        Span<RawParam> buffer = stackalloc RawParam[16];
        return registry.Match(path.AsSpan(), buffer).Found;
    }

    private static int ParamCount(RouteRegistry registry, string path)
    {
        Span<RawParam> buffer = stackalloc RawParam[16];
        var match = registry.Match(path.AsSpan(), buffer);
        return match.Found ? match.Params.Count : -1;
    }

    /// <summary>Raw (undecoded) value of the named parameter, or null when absent.</summary>
    private static string? RawParam(RouteRegistry registry, string path, string name)
    {
        Span<RawParam> buffer = stackalloc RawParam[16];
        var match = registry.Match(path.AsSpan(), buffer);
        if (!match.Found) return null;
        for (int i = 0; i < match.Params.Count; i++)
            if (match.Params.Name(i) == name)
                return match.Params.RawValue(i).ToString();
        return null;
    }

    [Fact]
    public void StaticRoutesMatchExactly()
    {
        var routes = Build("/", "/about", "/blog/index");
        Assert.True(Found(routes, "/"));
        Assert.True(Found(routes, "/about"));
        Assert.True(Found(routes, "/blog/index"));
        Assert.True(Found(routes, "/about/")); // trailing slash normalized
        Assert.False(Found(routes, "/contact"));
        Assert.False(Found(routes, "about"));  // leading slash required
    }

    [Fact]
    public void StaticWinsOverDynamic()
    {
        // /about is static, /{slug} is dynamic — static must win regardless of registration order
        var routes = Build("/{slug}", "/about");
        Assert.Equal(0, ParamCount(routes, "/about"));
        Assert.Equal(1, ParamCount(routes, "/contact"));
    }

    [Fact]
    public void ParamRouteCapturesValue()
    {
        var routes = Build("/blog/{slug}");
        Assert.Equal("hello-volt", RawParam(routes, "/blog/hello-volt", "slug"));
    }

    [Fact]
    public void MultiParamRoute()
    {
        var routes = Build("/shop/{category}/{id}");
        Assert.Equal("books", RawParam(routes, "/shop/books/42", "category"));
        Assert.Equal("42", RawParam(routes, "/shop/books/42", "id"));
    }

    [Fact]
    public void CatchAllCapturesRest()
    {
        var routes = Build("/files/{*path}");
        Assert.Equal("a/b/c.txt", RawParam(routes, "/files/a/b/c.txt", "path"));
        Assert.True(Found(routes, "/files")); // catch-all segment is optional
    }

    [Fact]
    public void CatchAllLowerPriorityThanParam()
    {
        var routes = Build("/x/{*rest}", "/x/{id}");
        Assert.Equal("1", RawParam(routes, "/x/1", "id"));
        Assert.Null(RawParam(routes, "/x/1", "rest"));
    }

    [Fact]
    public void NoMatchOnWrongSegmentCount()
    {
        var routes = Build("/a/{b}");
        Assert.False(Found(routes, "/a"));
        Assert.False(Found(routes, "/a/b/c"));
    }

    [Fact]
    public void ParamStopsAtSlash()
    {
        var routes = Build("/a/{b}");
        Assert.False(Found(routes, "/a/x/y"));
    }

    [Fact]
    public void MakePathBuildsCleanUrls()
    {
        var routes = Build("/blog/{slug}");
        Assert.Equal("/blog/hello-volt", routes.Routes[0].MakePath("hello-volt"));
    }

    [Fact]
    public void EmptyRegistryMatchesNothing()
    {
        var routes = Build();
        Assert.False(Found(routes, "/"));
    }

    [Fact]
    public void DuplicateRouteRejected()
    {
        var registry = new RouteRegistry();
        registry.Add("/a", static () => null!);
        Assert.Throws<InvalidOperationException>(() => registry.Add("/a", static () => null!));
    }
}

public class UrlDecoderTests
{
    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("a%20b", "a b")]
    [InlineData("a+b", "a b")]
    [InlineData("caf%C3%A9", "café")]
    [InlineData("100%25", "100%")]
    [InlineData("a%2Fb", "a/b")]
    public void DecodesExpectedValues(string input, string expected)
    {
        Assert.Equal(expected, UrlDecoder.Decode(input));
    }
}
