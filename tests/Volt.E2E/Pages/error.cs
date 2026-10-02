using Volt;

namespace Volt.E2E.Pages;

public sealed class Error : VoltErrorPage
{
    public override void Render(HtmlWriter w, RenderContext ctx)
    {
        w.DocType();
        using (w.Html("en"))
        {
            using (w.Head()) { w.MetaCharset(); w.Title("E2E 500"); }
            using (w.Body())
            {
                using (w.Main())
                {
                    w.H1Text("Error");
                    using (w.P()) w.Text("boom");
                }
            }
        }
    }
}
