using Volt;

namespace Starter.Pages;

public sealed class Index : VoltPage
{
    public override RenderMode Mode => RenderMode.SSG;

    public override void Render(HtmlWriter w, RenderContext ctx)
    {
        w.DocType();
        using (w.Html("en"))
        {
            using (w.Head())
            {
                w.MetaCharset();
                w.Title("Volt — starter");
                w.Meta("description", "A Volt app: SSG + SSR + island hydration in C#.");
                w.Link("stylesheet", "/styles.css");
            }
            using (w.Body())
            {
                using (w.Header()) w.H1Text("⚡ Volt starter");
                using (w.Main())
                {
                    using (w.P()) w.Text("Static page (SSG) with an interactive island:");
                    w.Island<Starter.Components.Counter, Starter.Components.CounterState>(
                        new Starter.Components.CounterState(0), ctx);
                    using (w.P())
                    {
                        w.Text("Dynamic route: ");
                        using (w.A()) { w.Href("/blog/hello-volt"); w.Text("/blog/hello-volt"); }
                    }
                }
            }
        }
    }
}
