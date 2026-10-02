using Volt;

namespace Volt.E2E;

/// <summary>M6: PRG flow — OnPostAsync returning a redirect instead of a render.</summary>
public sealed class Login : VoltPage
{
    public override Task<VoltPostResult> OnPostAsync(VoltRequest request)
        => Task.FromResult(VoltPostResult.Redirect("/about"));

    public override void Render(HtmlWriter w, RenderContext ctx)
    {
        using (w.El("p")) w.Text("login form");
    }
}
