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
                new TComponent { State = TComponent.DeserializeState(stateJson) }.Render(w, ctx);
            }
        }

        w.EnsureHydrationScript();
    }
}
