using Volt;

namespace Starter.Pages;

public sealed class About : VoltPage
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
                w.Title("About — Volt");
                w.Link("stylesheet", "/styles.css");
            }
            using (w.Body())
            {
                using (w.Main())
                {
                    w.H1Text("About");
                    using (w.P()) w.Text("Server-side rendered by Volt with zero allocations.");
                    using (w.P())
                    {
                        w.Text("Back to ");
                        using (w.A()) { w.Href("/"); w.Text("home"); }
                    }
                }
            }
        }
    }
}
