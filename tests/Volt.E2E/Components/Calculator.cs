using Volt;

namespace Volt.E2E.Components;

public sealed record CalcState(int Value);

// M3 experimental: the markup carries data-v-wasm; actions can be dispatched
// client-side by a WebAssembly module (see CONTRACT.md). The server registry
// still serves the fallback/no-JS flow when the module is absent.
[VoltIsland(Wasm = "/islands/calc.wasm")]
public sealed partial class Calc : VoltComponent<CalcState>
{
    public override void Render(HtmlWriter w, RenderContext ctx)
    {
        using (w.El("output"))
        {
            w.Text(State.Value.ToString());
        }
    }

    [VoltAction]
    public static CalcState Add(CalcState state) => state with { Value = state.Value + 1 };
}
