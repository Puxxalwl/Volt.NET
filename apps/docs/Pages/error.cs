using Volt;

namespace Docs.Pages;

public sealed class Error : VoltErrorPage
{
    public override void Render(HtmlWriter w, RenderContext ctx) => Docs.Shared.DocsLayout.Page(w, "500 — Volt.NET",
        "Server error.", _ =>
    {
        w.H1Text("Something went wrong");
        if (VoltRuntime.DevMode && Exception is not null)
            using (w.Pre()) w.Text(Exception.ToString());
    });
}
