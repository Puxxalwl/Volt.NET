using Volt;

namespace Starter.Pages;

public sealed class Post : VoltPage
{
    // dynamic SSG: StaticPaths prerenders each slug at export time
    public override RenderMode Mode => RenderMode.SSG;

    public override IEnumerable<string> StaticPaths() => ["hello-volt", "why-csharp"];

    public override void Render(HtmlWriter w, RenderContext ctx)
    {
        var slug = ctx.Params.Get("slug") ?? "unknown";
        var title = slug == "hello-volt" ? "Hello, Volt" : "Why C# for the web";
        w.DocType();
        using (w.Html("en"))
        {
            using (w.Head())
            {
                w.MetaCharset();
                w.Title(title + " — Volt");
                w.Meta("description", "A dynamic SSG page: " + slug);
                w.Link("stylesheet", "/styles.css");
            }
            using (w.Body())
            {
                using (w.Main())
                {
                    w.H1Text(title);
                    using (w.P()) w.Text("Slug: " + slug);
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
