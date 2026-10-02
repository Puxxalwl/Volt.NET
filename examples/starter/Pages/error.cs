using Volt;

namespace Starter.Pages;

public sealed class Error : VoltErrorPage
{
    public override void Render(HtmlWriter w, RenderContext ctx)
    {
        w.DocType();
        using (w.Html("en"))
        {
            using (w.Head()) { w.MetaCharset(); w.Title("500 — Volt"); }
            using (w.Body())
            {
                using (w.Main())
                {
                    w.H1Text("Something went wrong");
                    if (VoltRuntime.DevMode && Exception is not null)
                    {
                        using (w.Pre()) w.Text(Exception.ToString());
                    }
                }
            }
        }
    }
}
