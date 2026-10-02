namespace Volt;

/// <summary>Marker base for island components.</summary>
public abstract class VoltComponent
{
}

/// <summary>An island component bound to a serializable state type.</summary>
/// <typeparam name="TState">State DTO; must be JSON-serializable (source-generated context).</typeparam>
public abstract class VoltComponent<TState> : VoltComponent
{
    public TState State { get; init; } = default!;

    /// <summary>Renders the island's inner HTML (inside the volt-island wrapper).</summary>
    public abstract void Render(HtmlWriter w, RenderContext ctx);
}

/// <summary>
/// Implemented by source generation on island components: reflection-free state
/// (de)serialization and island name. Requires the component class to be partial.
/// </summary>
public interface IVoltIsland<TState>
{
    static abstract string IslandName { get; }

    /// <summary>WASM module URL for client-side dispatch, or null for server actions.</summary>
    static abstract string? WasmModule { get; }

    static abstract byte[] SerializeState(TState state);
    static abstract TState DeserializeState(ReadOnlySpan<byte> utf8Json);
}

/// <summary>
/// Marks a partial <see cref="VoltComponent{TState}"/> as an island for the source
/// generator: generates state serialization, the action dispatch and fragment rendering.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class VoltIslandAttribute : Attribute
{
    /// <summary>
    /// OPTIONAL (M3, experimental): URL of a WebAssembly module that dispatches this
    /// island's actions CLIENT-SIDE (no /_volt/action round-trip). See CONTRACT.md
    /// for the required module exports.
    /// </summary>
    public string? Wasm { get; set; }
}

/// <summary>
/// Marks a static method on an island component as an action invocable from the client.
/// Supported signatures (returns the new state):
/// <code>static TState Name(TState state)</code>
/// <code>static TState Name(TState state, JsonElement args)</code>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class VoltActionAttribute : Attribute
{
}

/// <summary>Thrown when an action is requested that the island does not declare.</summary>
public sealed class VoltActionNotFoundException : Exception
{
    public VoltActionNotFoundException(string island, string action)
        : base($"Volt: island '{island}' has no action '{action}'.")
    {
    }
}
