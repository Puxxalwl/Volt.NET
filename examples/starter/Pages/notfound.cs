using Volt;

namespace Starter.Pages;

public sealed class NotFound : VoltPage
{
    public override void Render(HtmlWriter w, RenderContext ctx)
    {
        w.DocType();
        using (w.Html("en"))
        {
            using (w.Head()) { w.MetaCharset(); w.Title("404 — Volt"); }
            using (w.Body())
            {
                using (w.Main())
                {
                    w.H1Text("404");
                    using (w.P())
                    {
                        w.Text("Nothing here. ");
                        using (w.A()) { w.Href("/"); w.Text("Go home"); }
                    }
                }
            }
        }
    }
}
