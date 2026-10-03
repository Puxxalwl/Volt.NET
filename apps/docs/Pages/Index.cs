using Volt;

namespace Docs.Pages;

public sealed class Index : VoltPage
{
    public override RenderMode Mode => RenderMode.SSG;

    public override void Render(HtmlWriter w, RenderContext ctx) => Docs.Shared.DocsLayout.Page(w, "Volt.NET — docs",
        "Volt.NET framework documentation: routing, islands, SSG/SSR, CLI.", body =>
    {
        w.H1Text("Volt.NET");
        using (w.P()) w.Text("Fullstack C# framework: file routing, island hydration, SSG/SSR, Native AOT.");
        using (w.Ul())
        {
            using (w.Li()) { using (w.A()) { w.Href("/routing"); w.Text("Routing"); } }
            using (w.Li()) { using (w.A()) { w.Href("/islands"); w.Text("Islands & hydration"); } }
            using (w.Li()) { using (w.A()) { w.Href("/templated"); w.Text("Templates (.volt)"); } }
            using (w.Li()) { using (w.A()) { w.Href("/whats-new"); w.Text("M6: forms, testing, metrics, minify"); } }
        }
    });
}
