using System.Text;

namespace Volt;

/// <summary>Island rendering entry point (see CONTRACT.md for the emitted markup).</summary>
public static class HtmlWriterIslands
{
    /// <summary>
    /// Renders an interactive island: a no-JS fallback form wrapping the volt-island
    /// element with the serialized state, then the component's server-rendered HTML.
    /// Emits the hydration boot script once per document.
    /// </summary>
    /// <param name="sid">Stable island id; defaults to the page render's auto counter.</param>
    public static void Island<TComponent, TState>(
        this HtmlWriter w, TState state, RenderContext ctx, string? sid = null)
        where TComponent : VoltComponent<TState>, IVoltIsland<TState>, new()
    {
        sid ??= ctx.NextSid();
        string name = TComponent.IslandName;

        // A live fallback token overrides the passed state (no-JS form flow).
        byte[] stateJson = ctx.TryGetFallbackState(name, sid) ?? TComponent.SerializeState(state);

        using (w.Form())
        {
            w.Method("post");
            w.Action("/_volt/fallback");
            w.DataVoltForm();
            w.HiddenInput("__volt_island", name);
            w.HiddenInput("__volt_state", stateJson);
            w.HiddenInput("__volt_sid", sid);

            using (w.El("volt-island"))
            {
                w.Attr("data-v"u8, name);
                w.Attr("data-sid"u8, sid);
                w.Attr("data-props"u8, stateJson);
                if (TComponent.WasmModule is { Length: > 0 } wasmUrl)
                    w.Attr("data-v-wasm"u8, wasmUrl);
                new TComponent { State = TComponent.DeserializeState(stateJson) }.Render(w, ctx);
            }
        }

        w.EnsureHydrationScript();
    }

    /// <summary>
    /// Renders an island by NAME through the runtime registry (the .volt template path):
    /// the state is the CONTRACT wire-format JSON. Emits the same form + volt-island
    /// markup and the hydration script as the typed overload.
    /// </summary>
    public static void IslandByName(
        this HtmlWriter w, string name, ReadOnlySpan<char> stateJson, RenderContext ctx, string? sid = null)
    {
        var entry = VoltRuntime.Islands.Find(name)
            ?? throw new InvalidOperationException(
                $"Volt: island '{name}' is not registered — check the [VoltIsland] component name.");
        sid ??= ctx.NextSid();

        // A live fallback token overrides the template state (no-JS form flow).
        byte[] stateBytes;
        if (ctx.TryGetFallbackState(name, sid) is { } overrideState)
        {
            stateBytes = overrideState;
        }
        else
        {
            stateBytes = new byte[Encoding.UTF8.GetMaxByteCount(stateJson.Length)];
            int written = Encoding.UTF8.GetBytes(stateJson, stateBytes);
            if (written < stateBytes.Length) stateBytes = stateBytes[..written];
        }

        using (var fragment = entry.Render(stateBytes, sid, ctx.FallbackToken))
        {
            w.RawUtf8(fragment.Html);
        }

        w.EnsureHydrationScript();
    }
}
