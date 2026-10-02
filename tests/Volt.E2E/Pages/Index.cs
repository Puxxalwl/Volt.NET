using Volt;

namespace Volt.E2E.Pages;

public sealed class Index : VoltPage
{
    public override RenderMode Mode => RenderMode.SSG;

    public override void Render(HtmlWriter w, RenderContext ctx)
    {
        w.DocType();
        using (w.Html("en"))
        {
            using (w.Head()) { w.MetaCharset(); w.Title("E2E home"); }
            using (w.Body())
            {
                using (w.Main())
                {
                    w.H1Text("Home");
                    w.Island<Volt.E2E.Components.Todo, Volt.E2E.Components.TodoState>(
                        new Volt.E2E.Components.TodoState("hello", 0, false), ctx);
                    using (w.A()) { w.Href("/about"); w.Text("about"); }
                }
            }
        }
    }
}
