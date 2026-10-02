using Volt;

namespace Volt.E2E.Pages;

public sealed class Post : VoltPage
{
    public override RenderMode Mode => RenderMode.SSG;

    public override IEnumerable<string> StaticPaths() => ["first", "second"];

    public override void Render(HtmlWriter w, RenderContext ctx)
    {
        var slug = ctx.Params.Get("slug") ?? "unknown";
        w.DocType();
        using (w.Html("en"))
        {
            using (w.Head()) { w.MetaCharset(); w.Title("E2E post " + slug); }
            using (w.Body())
            {
                using (w.Main())
                {
                    w.H1Text("Post: " + slug);
                }
            }
        }
    }
}
