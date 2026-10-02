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

    public static VoltOptions FromEnvironment()
    {
        var dev = Environment.GetEnvironmentVariable("VOLT_DEV") == "1"
            || Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development";
        var baseUrl = Environment.GetEnvironmentVariable("VOLT_BASE_URL");
        var exportPath = Environment.GetEnvironmentVariable("VOLT_DIST") ?? "dist";
        return new VoltOptions { BaseUrl = baseUrl, DevMode = dev, ExportPath = exportPath };
    }
}
