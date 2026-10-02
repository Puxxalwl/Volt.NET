using Volt;

namespace Docs.Shared;

/// <summary>Shared layout for docs pages.</summary>
public static class DocsLayout
{
    public static void Page(HtmlWriter w, string title, string description, Action<HtmlWriter> body)
    {
        w.DocType();
        using (w.Html("en"))
        {
            using (w.Head())
            {
                w.MetaCharset();
                w.Title(title);
                w.Meta("description", description);
                w.Link("stylesheet", "/styles.css");
            }
            using (w.Body())
            {
                using (w.Nav())
                {
                    using (w.A()) { w.Href("/"); w.Text("Volt.NET docs"); }
                    using (w.A()) { w.Href("/routing"); w.Text("Routing"); }
                    using (w.A()) { w.Href("/islands"); w.Text("Islands"); }
                }
                using (w.Main()) body(w);
                using (w.Footer())
                {
                    using (w.P()) w.Text("Built with Volt.NET — SSG, zero-alloc render.");
                }
            }
        }
    }
}
