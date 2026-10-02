using Volt;
using Volt.E2E.Components;

namespace Volt.E2E;

/// <summary>M6: typed form binding + validation, entirely without JavaScript.</summary>
public sealed class Subscribe : VoltPage
{
    public string? Saved;
    public IReadOnlyList<VoltFormError> Errors = Array.Empty<VoltFormError>();
    public NewsletterForm Form = new();

    public override Task<VoltPostResult> OnPostAsync(VoltRequest request)
    {
        var valid = request.TryForm<NewsletterForm>(out var form, out var errors);
        Form = form;
        Errors = errors;
        Saved = valid ? "saved:" + form.Email : null;
        return Task.FromResult(VoltPostResult.Render);
    }

    public override void Render(HtmlWriter w, RenderContext ctx)
    {
        w.DocType();
        using (w.El("html"))
        {
            w.Attr("lang", "en");
            using (w.El("body"))
            {
                using (w.El("h1")) w.Text("Subscribe");
                if (Saved is not null)
                {
                    using (w.El("p")) { w.Class("saved"); w.Text(Saved); }
                }
                foreach (var error in Errors)
                {
                    using (w.El("p"))
                    {
                        w.Class("field-error");
                        w.Attr("data-field", error.Field);
                        w.Text(error.Message);
                    }
                }
                using (w.El("form"))
                {
                    w.Attr("method", "post");
                    using (w.VoidEl("input")) { w.Attr("type", "email"); w.Attr("name", "email"); w.Attr("value", Form.Email); }
                    using (w.VoidEl("input")) { w.Attr("type", "number"); w.Attr("name", "rating"); w.Attr("value", Form.Rating?.ToString() ?? ""); }
                    using (w.VoidEl("input")) { w.Attr("type", "checkbox"); w.Attr("name", "subscribe"); if (Form.Subscribe) w.Attr("checked", "checked"); }
                    using (w.VoidEl("input")) { w.Attr("type", "text"); w.Attr("name", "name"); w.Attr("value", Form.Name ?? ""); }
                    using (w.VoidEl("button")) { w.Attr("type", "submit"); w.Text("Go"); }
                }
            }
        }
    }
}
