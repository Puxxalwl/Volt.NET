using Volt;

namespace Volt.Demo;

/// <summary>Keep exported links relative to the site root, including GitHub project Pages.</summary>
public static class Site
{
    public const string ExampleEmail = "developer@example.com";

    // IslandByName renders through a generated fragment delegate which does not forward
    // the export request. Render the component directly for a safe, non-hydrated preview.
    public static void RenderCounterPreview(HtmlWriter w, RenderContext ctx)
        => new Components.Counter { State = new Components.CounterState(0) }.Render(w, ctx);
    public static string Root(RenderContext ctx)
    {
        // The showcase has a root page and one-segment demo routes.
        // Export produces route/index.html; local routes are linked with trailing slashes too.
        return ctx.Request?.Path.Trim('/') is { Length: > 0 } ? "../" : "./";
    }
}
