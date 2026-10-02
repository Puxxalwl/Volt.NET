using System.Text.Json;
using Volt;

namespace Starter.Components;

public sealed record CounterState(int Count);

[VoltIsland]
public sealed partial class Counter : VoltComponent<CounterState>
{
    public override void Render(HtmlWriter w, RenderContext ctx)
    {
        using (w.Div())
        {
            w.Class("counter");
            using (w.Span())
            {
                w.Class("count");
                w.Text("Count: " + State.Count);
            }
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
    public static CounterState Increment(CounterState state, JsonElement args)
        => state with { Count = state.Count + 1 };
}
