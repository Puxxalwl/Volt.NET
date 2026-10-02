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
    /// M6: revalidation tags — cached SSG output carrying a tag can be evicted on demand
    /// (VoltRuntime.RevalidateTag / POST /_volt/revalidate / `volt revalidate`).
    /// .volt pages set them with the @tag directive.
    /// </summary>
    public virtual string[] Tags => Array.Empty<string>();

    /// <summary>
    /// For dynamic SSG routes: raw values to prerender. "a/b" fills the route's
    /// dynamic segments in order (e.g. [slug] gets "a", [id] gets "b").
    /// </summary>
    public virtual IEnumerable<string> StaticPaths() => [];

    /// <summary>Async data loading before rendering. Runs per request (SSR) or per render (SSG).</summary>
    public virtual Task OnPreRenderAsync(VoltRequest request) => Task.CompletedTask;

    // ---- M6: forms ---------------------------------------------------------------------

    /// <summary>
    /// POST handler. Runs for POSTs to this page's route before rendering:
    /// bind a typed form via <see cref="VoltRequest.TryForm{T}"/>, keep the
    /// result on instance fields and render it in <see cref="Render"/> — the
    /// whole flow works without JavaScript. Return <see cref="VoltPostResult.Redirect"/>
    /// for PRG instead of rendering.
    /// </summary>
    public virtual Task<VoltPostResult> OnPostAsync(VoltRequest request)
        => Task.FromResult(VoltPostResult.Render);

    /// <summary>Renders the full HTML document.</summary>
    public abstract void Render(HtmlWriter w, RenderContext ctx);
}

/// <summary>What the engine does after <see cref="VoltPage.OnPostAsync"/>.</summary>
public sealed record VoltPostResult
{
    private VoltPostResult(int status, string? location)
    {
        StatusCode = status;
        Location = location;
    }

    /// <summary>Render the page (default) — errors/success visible immediately, no JS needed.</summary>
    public static VoltPostResult Render { get; } = new(200, null);

    /// <summary>Redirect instead of rendering (PRG pattern): 303 by default.</summary>
    public static VoltPostResult Redirect(string url, int statusCode = 303) => new(statusCode, url);

    public int StatusCode { get; }
    public string? Location { get; }
    public bool IsRedirect => Location is not null;
}

/// <summary>Base class for Pages/error.cs: renders unhandled exceptions.</summary>
public abstract class VoltErrorPage : VoltPage
{
    public Exception? Exception { get; set; }
}
