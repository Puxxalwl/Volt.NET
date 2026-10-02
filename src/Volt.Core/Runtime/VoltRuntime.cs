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

    internal static string HydrateScriptSrc => "/_volt/hydrate.js?v=" + HydrateVersion;
}
