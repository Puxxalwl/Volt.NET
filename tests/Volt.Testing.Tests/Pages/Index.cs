using Volt;

namespace Volt.Testing.Tests;

public sealed class Home : VoltPage
{
    public override RenderMode Mode => RenderMode.SSR;
    public override void Render(HtmlWriter w, RenderContext ctx)
    {
        w.DocType();
        using (w.El("html"))
        {
            using (w.El("head")) using (w.El("title")) w.Text("home");
            using (w.El("body"))
            {
                using (w.El("h1")) w.Text("Home");
                using (w.El("ul"))
                {
                    for (int i = 1; i <= 3; i++)
                        using (w.El("li")) w.Text("item " + i);
                }
            }
        }
    }
}
