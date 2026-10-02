using Volt;

namespace Volt.Demo.Components;

public sealed record CounterState(int Count);

[VoltIsland]
public sealed partial class Counter : VoltComponent<CounterState>
{
    public override void Render(HtmlWriter w, RenderContext ctx)
    {
        using (w.Div())
        {
            w.Class("counter");
            w.Attr("data-volt-island-container", "true");
            using (w.Span()) w.Text("count: " + State.Count);
            using (w.Button())
            {
                w.Type("submit");
                w.Name("__volt_action");
                w.Value("increment");
                w.DataVoltOn("click:increment");
                w.Text("+1");
            }
        }
    }

    [VoltAction]
    public static CounterState Increment(CounterState state, System.Text.Json.JsonElement args)
        => state with { Count = state.Count + 1 };
}
