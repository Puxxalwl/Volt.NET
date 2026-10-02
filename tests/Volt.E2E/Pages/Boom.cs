using Volt;

namespace Volt.E2E.Pages;

public sealed class Boom : VoltPage
{
    public override async Task OnPreRenderAsync(VoltRequest request)
    {
        await Task.Yield();
        throw new InvalidOperationException("E2E: boom page always fails");
    }

    public override void Render(HtmlWriter w, RenderContext ctx) { }
}
