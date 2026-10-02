namespace Volt;

/// <summary>Framework options, wired by VoltApp.Run or from environment variables.</summary>
public sealed class VoltOptions
{
    /// <summary>Site origin for canonical URLs, sitemap and robots (e.g. https://example.com).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Serve /sitemap.xml (default true).</summary>
    public bool EnableSitemap { get; set; } = true;

    /// <summary>Serve /robots.txt (default true).</summary>
    public bool EnableRobots { get; set; } = true;

    /// <summary>Show stack traces on error pages (default: development only).</summary>
    public bool DevMode { get; set; }

    /// <summary>Output directory for `volt export` (default: dist).</summary>
    public string ExportPath { get; set; } = "dist";

    /// <summary>Capacity of the in-memory SSG page cache (pages).</summary>
    public int SsgCacheCapacity { get; set; } = 256;

    /// <summary>
    /// M4: shared file directory for the SSG cache. When set, rendered pages persist
    /// (atomic writes) and are visible to other instances using the same directory —
    /// warm caches across restarts and multi-node shared-disk deployments.
    /// Env: VOLT_SSG_CACHE_DIR.
    /// </summary>
    public string? SsgCacheDirectory { get; set; }

    // ---- M6: middleware pipeline ------------------------------------------------------

    private List<VoltMiddleware>? _middleware;

    /// <summary>
    /// M6: middleware wraps every request that goes through the async pipeline
    /// (pages, actions, fallback, internal endpoints). While any middleware is
    /// registered the zero-allocation fast path is bypassed — middleware must not
    /// be skipped for cached pages, so correctness wins over the last microsecond.
    /// </summary>
    public IReadOnlyList<VoltMiddleware> Middleware => _middleware ?? (IReadOnlyList<VoltMiddleware>)Array.Empty<VoltMiddleware>();

    /// <summary>true when any middleware is registered (disables the fast path).</summary>
    public bool HasMiddleware => _middleware is { Count: > 0 };

    /// <summary>Registers a middleware (order = registration order). Fluent.</summary>
    public VoltOptions Use(VoltMiddleware middleware)
    {
        _middleware ??= new List<VoltMiddleware>(4);
        _middleware.Add(middleware);
        return this;
    }

    /// <summary>
    /// M6: custom error handler (replaces the default error page). Gets the exception;
    /// write to <paramref name="ctx"/> what you want the client to see.
    /// </summary>
    public Func<VoltHttpContext, Exception, Task>? OnException { get; set; }

    public static VoltOptions FromEnvironment()
    {
        var dev = Environment.GetEnvironmentVariable("VOLT_DEV") == "1"
            || Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development";
        var baseUrl = Environment.GetEnvironmentVariable("VOLT_BASE_URL");
        var exportPath = Environment.GetEnvironmentVariable("VOLT_DIST") ?? "dist";
        var ssgCacheDir = Environment.GetEnvironmentVariable("VOLT_SSG_CACHE_DIR");
        return new VoltOptions { BaseUrl = baseUrl, DevMode = dev, ExportPath = exportPath, SsgCacheDirectory = ssgCacheDir };
    }
}
