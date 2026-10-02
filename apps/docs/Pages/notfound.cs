using Volt;

namespace Docs.Pages;

public sealed class NotFound : VoltPage
{
    public override void Render(HtmlWriter w, RenderContext ctx) => Docs.Shared.DocsLayout.Page(w, "404 — Volt.NET",
        "Page not found.", _ =>
    {
        w.H1Text("404");
        using (w.P())
        {
            w.Text("Missing page. Back to ");
            using (w.A()) { w.Href("/"); w.Text("docs home"); }
            w.Text(".");
        }
    });
}
