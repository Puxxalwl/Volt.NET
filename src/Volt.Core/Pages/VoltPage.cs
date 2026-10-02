namespace Volt;

public enum RenderMode
{
    /// <summary>Static site generation: rendered once, cached (optionally revalidated), exported by `volt export`.</summary>
    SSG,

    /// <summary>Server-side rendering: rendered per request.</summary>
    SSR,
}

/// <summary>
/// A page. The route is derived from the file location under Pages/ by the Volt source generator.
/// The page writes the whole HTML document (doctype, html, head, body) in <see cref="Render"/>.
/// </summary>
public abstract class VoltPage
{
    /// <summary>SSG pages are rendered once and cached (and exported); SSR pages render per request.</summary>
    public virtual RenderMode Mode => RenderMode.SSR;

    /// <summary>For SSG pages: revalidation interval in seconds (0 = never). Serving stale + background re-render.</summary>
    public virtual int RevalidateSeconds => 0;

    /// <summary>
    /// For dynamic SSG routes: raw values to prerender. "a/b" fills the route's
    /// dynamic segments in order (e.g. [slug] gets "a", [id] gets "b").
    /// </summary>
    public virtual IEnumerable<string> StaticPaths() => [];

    /// <summary>Async data loading before rendering. Runs per request (SSR) or per render (SSG).</summary>
    public virtual Task OnPreRenderAsync(VoltRequest request) => Task.CompletedTask;

    /// <summary>Renders the full HTML document.</summary>
    public abstract void Render(HtmlWriter w, RenderContext ctx);
}

/// <summary>Base class for Pages/error.cs: renders unhandled exceptions.</summary>
public abstract class VoltErrorPage : VoltPage
{
    public Exception? Exception { get; set; }
}
