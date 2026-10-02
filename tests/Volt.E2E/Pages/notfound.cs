using Volt;

namespace Volt.E2E.Pages;

public sealed class NotFound : VoltPage
{
    public override void Render(HtmlWriter w, RenderContext ctx)
    {
        w.DocType();
        using (w.Html("en"))
        {
            using (w.Head()) { w.MetaCharset(); w.Title("E2E 404"); }
            using (w.Body())
            {
                using (w.Main())
                {
                    w.H1Text("404");
                    using (w.P()) w.Text("Nothing here.");
                }
            }
        }
    }
}
