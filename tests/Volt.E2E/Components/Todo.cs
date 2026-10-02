using System.Text.Json;
using Volt;

namespace Volt.E2E.Components;

public sealed record TodoState(string Text, int Count, bool Done);

[VoltIsland]
public sealed partial class Todo : VoltComponent<TodoState>
{
    public override void Render(HtmlWriter w, RenderContext ctx)
    {
        using (w.Div())
        {
            w.Class("todo");
            using (w.Span()) w.Text(State.Text + " #" + State.Count + (State.Done ? " [done]" : ""));
            using (w.Button())
            {
                w.Type("submit");
                w.Name("__volt_action");
                w.Value("toggle");
                w.DataVoltOn("click:toggle");
                w.Text("Toggle");
            }
            using (w.Button())
            {
                w.Type("submit");
                w.Name("__volt_action");
                w.Value("bump");
                w.DataVoltOn("click:bump");
                w.Text("+1");
            }
        }
    }

    [VoltAction]
    public static TodoState Toggle(TodoState state, JsonElement args)
        => state with { Done = !state.Done };

    [VoltAction]
    public static TodoState Bump(TodoState state) => state with { Count = state.Count + 1 };

    [VoltAction]
    public static TodoState WithArgs(TodoState state, JsonElement args)
        => state with { Text = args.GetProperty("text").GetString() ?? "?" };
}
