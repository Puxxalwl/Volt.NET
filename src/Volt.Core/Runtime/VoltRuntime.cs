namespace Volt;

/// <summary>Global runtime state shared by core, transport and generated code.</summary>
public static class VoltRuntime
{
    /// <summary>Route table (filled by generated ModuleInitializers at startup).</summary>
    public static RouteRegistry Routes => RouteRegistry.Root;

    /// <summary>Island table (filled by generated ModuleInitializers at startup).</summary>
    public static VoltIslandRegistry Islands => VoltIslandRegistry.Root;

    /// <summary>Bounded no-JS fallback state store (the only server-side state).</summary>
    public static VoltFallbackStore FallbackStore { get; } = new();

    /// <summary>Dev mode: error pages show stack traces, no cache hardening.</summary>
    public static bool DevMode { get; set; }

    /// <summary>Cache-busting version of the hydration script; set by the transport at startup.</summary>
    public static string HydrateVersion { get; set; } = "1";

    /// <summary>
    /// The hydrate.js bytes (set by the host from the embedded resource at startup).
    /// The engine serves it from here with immutable caching.
    /// </summary>
    public static byte[]? HydrateScript { get; private set; }

    /// <summary>ETag for the hydrate script (quoted FNV-1a of the bytes).</summary>
    public static string? HydrateScriptETag { get; private set; }

    /// <summary>Installs the hydration script and computes its version/ETag. Call once at host startup.</summary>
    public static void SetHydrateScript(byte[] script)
    {
        HydrateScript = script;
        HydrateVersion = Fnv1a64(script).ToString("x16");
        HydrateScriptETag = "\"" + Fnv1a64(script).ToString("x16") + "\"";
    }

    private static ulong Fnv1a64(ReadOnlySpan<byte> data)
    {
        ulong hash = 14695981039346656037;
        foreach (byte b in data)
        {
            hash ^= b;
            hash *= 1099511628211;
        }
        return hash;
    }

    /// <summary>
    /// Versioned (content-hash) hydrate script URL — served with immutable caching.
    /// The legacy /_volt/hydrate.js URL keeps working (ETag/304).
    /// </summary>
    internal static string HydrateScriptSrc => "/_volt/hydrate." + HydrateVersion + ".js";
}
