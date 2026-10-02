using Volt;

namespace Volt.Testing.Tests;

public sealed class Hello : VoltPage
{
    public override RenderMode Mode => RenderMode.SSR;
    public string? Saved;
    public IReadOnlyList<VoltFormError> Errors = Array.Empty<VoltFormError>();
    public HelloForm Form = new();

    public override Task<VoltPostResult> OnPostAsync(VoltRequest request)
    {
        var valid = request.TryForm<HelloForm>(out var form, out var errors);
        Form = form;
        Errors = errors;
        Saved = valid ? "ok" : null;
        return Task.FromResult(VoltPostResult.Render);
    }

    public override void Render(HtmlWriter w, RenderContext ctx)
    {
        w.DocType();
        using (w.El("html"))
        {
            using (w.El("body"))
            {
                using (w.El("h1")) w.Text("Hello");
                if (Saved is not null) { using (w.El("p")) { w.Class("saved"); w.Text(Saved); } }
                foreach (var e in Errors)
                {
                    using (w.El("p")) { w.Class("field-error"); w.Attr("data-field", e.Field); w.Text(e.Message); }
                }
            }
        }
    }
}

[VoltForm]
public sealed partial class HelloForm
{
    [VoltRequired, VoltEmail]
    public string Email { get; set; } = "";
}
