namespace Volt;

/// <summary>
/// Render-phase context: ref struct, carries route params as path slices plus island state.
/// Valid only during a render, on the render call stack.
/// </summary>
public ref struct RenderContext
{
    public VoltRequest Request;
    public RouteParams Params;

    internal int IslandCounter;

    /// <summary>One-time fallback token (?__v=...) wired by the transport for no-JS form flow.</summary>
    public string? FallbackToken;

    /// <summary>Fallback state store wired by the transport.</summary>
    public VoltFallbackStore? FallbackStore;

    /// <summary>A minimal context for rendering island fragments after an action.</summary>
    public static RenderContext ForFragment()
        => new() { Request = new VoltRequest { Method = "POST", Path = "/_volt/action" } };

    internal string NextSid()
    {
        int n = IslandCounter++;
        Span<char> buf = stackalloc char[8];
        buf[0] = 'i';
        bool ok = n.TryFormat(buf[1..], out int len);
        return ok ? new string(buf[..(len + 1)]) : $"i{n}";
    }

    internal byte[]? TryGetFallbackState(string islandName, string sid)
        => FallbackStore?.Get(FallbackToken, islandName, sid);
}
