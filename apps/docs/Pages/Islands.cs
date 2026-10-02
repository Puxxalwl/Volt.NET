using Volt;

namespace Docs.Pages;

public sealed class Islands : VoltPage
{
    public override RenderMode Mode => RenderMode.SSG;

    public override void Render(HtmlWriter w, RenderContext ctx) => Docs.Shared.DocsLayout.Page(w, "Islands — Volt.NET",
        "Interactive islands: server-rendered components with hydration actions and no-JS fallback.", body =>
    {
        w.H1Text("Islands");
        using (w.P())
        {
            w.Text("A component derives from ");
            using (w.Code()) w.Text("VoltComponent<TState>");
            w.Text(", is marked ");
            using (w.Code()) w.Text("[VoltIsland]");
            w.Text(" and declares static actions with ");
            using (w.Code()) w.Text("[VoltAction]");
            w.Text(".");
        }
        using (w.Pre())
        {
            w.Text("""
                public sealed record CounterState(int Count);

                [VoltIsland]
                public sealed partial class Counter : VoltComponent<CounterState>
                {
                    public override void Render(HtmlWriter w, RenderContext ctx)
                    {
                        using (w.Button())
                        {
                            w.Type("submit");
                            w.Name("__volt_action");
                            w.Value("increment");
                            w.DataVoltOn("click:increment");
                            w.Text("+1");
                        }
                    }

                    [VoltAction]
                    public static CounterState Increment(CounterState state)
                        => state with { Count = state.Count + 1 };
                }
                """);
        }
        using (w.P())
        {
            w.Text("The generator emits typed (de)serializers, action dispatch and fragment rendering — ");
            w.Text("no reflection, Native AOT safe. Pages embed islands via ");
            using (w.Code()) w.Text("w.Island<Counter, CounterState>(new(0), ctx)");
            w.Text(".");
        }
        using (w.P()) w.Text("Without JavaScript, the island still works: the form posts to /_volt/fallback and the server restores state via a one-time token.");
    });
}
