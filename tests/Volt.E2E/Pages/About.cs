using Volt;

namespace Volt.E2E.Pages;

public sealed class About : VoltPage
{
    public override void Render(HtmlWriter w, RenderContext ctx)
    {
        w.DocType();
        using (w.Html("en"))
        {
            using (w.Head()) { w.MetaCharset(); w.Title("E2E about"); }
            using (w.Body())
            {
                using (w.Main())
                {
                    w.H1Text("About");
                    using (w.P()) w.Text("SSR page, rendered per request.");
                }
            }
        }
    }
}
