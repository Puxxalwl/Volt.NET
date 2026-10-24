using Volt;

namespace Volt.Demo.Pages;

/// <summary>Local SSR companion to the static typed-form walkthrough. Does not store data.</summary>
public sealed class Signup : VoltPage
{
    public bool Valid;
    public SignupForm Form = new();
    public IReadOnlyList<VoltFormError> Errors = Array.Empty<VoltFormError>();

    public override Task<VoltPostResult> OnPostAsync(VoltRequest request)
    {
        Valid = request.TryForm<SignupForm>(out var form, out var errors);
        Form = form;
        Errors = errors;
        return Task.FromResult(VoltPostResult.Render);
    }

    public override void Render(HtmlWriter w, RenderContext ctx)
    {
        w.DocType();
        using (w.Html("en"))
        {
            using (w.Head())
            {
                w.MetaCharset();
                w.Meta("viewport", "width=device-width, initial-scale=1");
                w.Title("Typed form / Volt.NET local demo");
                w.Link("stylesheet", "/styles.css");
            }
            using (w.Body())
            using (w.Main())
            {
                w.Class("shell prose");
                using (w.P()) { using (w.A()) { w.Href("/features/#forms"); w.Text("← Form walkthrough"); } }
                w.H1Text("Typed form / local server");
                using (w.P()) w.Text("This response is rendered by C#. The example validates input; it does not store it or subscribe you to anything.");
                if (Valid)
                    using (w.P()) { w.Class("notice"); w.Text("Valid email: " + Form.Email); }
                foreach (var error in Errors)
                    using (w.P()) { w.Class("notice"); w.Attr("role", "alert"); w.Text(error.Field + ": " + error.Message); }
                using (w.Form())
                {
                    w.Class("form-preview");
                    w.Method("post");
                    w.Action("/signup/");
                    // Server validation remains observable without browser constraint validation.
                    w.Attr("novalidate", "novalidate");
                    using (w.El("label")) { w.Attr("for", "email"); w.Text("Email"); }
                    using (w.VoidEl("input"))
                    {
                        w.Attr("id", "email"); w.Type("email"); w.Name("email"); w.Value(Form.Email);
                    }
                    using (w.Button()) { w.Type("submit"); w.Text("Validate on the server"); }
                }
            }
        }
    }
}
