using Volt;

namespace Docs.Pages;

public sealed class Routing : VoltPage
{
    public override RenderMode Mode => RenderMode.SSG;

    public override void Render(HtmlWriter w, RenderContext ctx) => Docs.Shared.DocsLayout.Page(w, "Routing — Volt.NET",
        "File-based routing in Volt.NET: Pages folder, dynamic segments, special pages.", body =>
    {
        w.H1Text("Routing");
        using (w.P()) w.Text("Routes come from the file tree under Pages/. Static segments are lowercased:");
        using (w.Table())
        {
            w.Class("routes");
            using (w.Tr())
            {
                using (w.Td()) w.Text("Pages/Index.cs");
                using (w.Td()) { using (w.Code()) w.Text("/"); }
            }
            using (w.Tr())
            {
                using (w.Td()) w.Text("Pages/about.cs");
                using (w.Td()) { using (w.Code()) w.Text("/about"); }
            }
            using (w.Tr())
            {
                using (w.Td()) w.Text("Pages/Blog/[slug].cs");
                using (w.Td()) { using (w.Code()) w.Text("/blog/{slug}"); }
            }
            using (w.Tr())
            {
                using (w.Td()) w.Text("Pages/Shop/[...rest].cs");
                using (w.Td()) { using (w.Code()) w.Text("/shop/{*rest}"); }
            }
            using (w.Tr())
            {
                using (w.Td()) w.Text("Pages/notfound.cs");
                using (w.Td()) w.Text("404 page");
            }
            using (w.Tr())
            {
                using (w.Td()) w.Text("Pages/error.cs");
                using (w.Td()) w.Text("500 page (VoltErrorPage)");
            }
        }
        using (w.P())
        {
            w.Text("Parameters: ");
            using (w.Code()) w.Text("ctx.Params.Get(\"slug\")");
        }
    });
}
