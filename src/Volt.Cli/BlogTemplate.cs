namespace Volt.Cli;

/// <summary>
/// M6: `volt new myblog --template blog` — a .volt template: layouts, partials,
/// a typed form (no JavaScript), @tag revalidation and compile-time minification.
/// </summary>
internal static class BlogTemplate
{
    public static IEnumerable<(string Rel, string Content)> Files(string appName) =>
    [
        (appName + ".csproj", Csproj),
        ("Program.cs", ProgramCs),
        ("Pages/_Layout.volt", LayoutVolt),
        ("Pages/index.volt", IndexVolt),
        ("Pages/posts.volt", PostsVolt),
        ("Pages/subscribe.volt", SubscribeVolt),
        ("Code/Subscribe.cs", SubscribeCode), // code-behind: OnPostAsync for the .volt page
        ("Models/SubscribeForm.cs", SubscribeFormCs),
        ("Pages/notfound.cs", NotFoundCs),
        ("Pages/error.cs", ErrorCs),
        ("Components/Counter.cs", CounterCs),
        ("wwwroot/styles.css", Styles),
        (".gitignore", "bin/\nobj/\ndist/\n"),
    ];

    private const string Csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <InvariantGlobalization>true</InvariantGlobalization>
            <PublishAot>true</PublishAot>
            <!-- M6: minify static text at compile time + rendered HTML at runtime -->
            <VoltMinify>true</VoltMinify>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Volt.Kestrel" Version="*" />
            <PackageReference Include="Volt.Server" Version="*" />
            <PackageReference Include="Volt.Compiler" Version="*" PrivateAssets="all"
                              OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
          </ItemGroup>
        </Project>
        """;

    private const string ProgramCs = """
        using Volt;

        var options = new VoltOptions
        {
            BaseUrl = "http://localhost:5000",
            // typed forms POST to page routes and re-render — no JavaScript needed
            // EnableMetrics = true,          // /_volt/metrics (Prometheus) + /_volt/hud
            // RevalidateToken = "secret",    // enables POST /_volt/revalidate
        };

        // default: the built-in zero-allocation server; VOLT_TRANSPORT=kestrel for the Kestrel bridge
        return Environment.GetEnvironmentVariable("VOLT_TRANSPORT") == "kestrel"
            ? VoltApp.Run(args, options)
            : VoltServerApp.Run(args, options);
        """;

    private const string LayoutVolt = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>@(ctx.Request.Path == "/subscribe" ? "Subscribe" : "Volt blog")</title>
          <link rel="stylesheet" href="/styles.css" />
        </head>
        <body>
          <header>
            <nav>
              <a class="brand" href="/">⚡ Volt blog</a>
              <a href="/posts">Posts</a>
              <a href="/subscribe">Subscribe</a>
            </nav>
          </header>
          <main>
            @renderbody
          </main>
          <footer><p>Built with Volt.NET — fullstack C#, no npm.</p></footer>
        </body>
        </html>
        """;

    private const string IndexVolt = """
        @namespace Starter.Pages
        @page /
        @mode SSG

        <section class="hero">
          <h1>Fullstack C#, zero JavaScript bloat</h1>
          <p>Layouts, partials and typed forms — all .volt templates compiled into your assembly.</p>
        </section>
        <volt-island name="Counter" state='{"count":0}' />
        """;

    private const string PostsVolt = """
        @namespace Starter.Pages
        @page /posts
        @mode SSG
        @tag posts

        <h1>Posts</h1>
        @{
            var posts = new (string Title, string Slug)[]
            {
                ("Hello, Volt", "hello-volt"),
                ("Why C# for the web", "why-csharp"),
            };
        }
        <ul>
        @foreach (var post in posts)
        {
            <li><a href="/posts/@post.Slug">@post.Title</a></li>
        }
        </ul>
        """;

    private const string SubscribeVolt = """
        @namespace Starter.Pages
        @class Subscribe
        @page /subscribe
        @mode SSR

        <h1>Subscribe</h1>
        @if (SavedEmail is not null)
        {
            <p class="saved">Subscribed: @SavedEmail</p>
        }
        @foreach (var error in FormErrors)
        {
            <p class="field-error" data-field="@error.Field">@error.Message</p>
        }
        <form method="post">
          <input type="email" name="Email" placeholder="your email" value="@EmailValue" />
          <input type="text" name="Name" placeholder="Name (optional)" value="@NameValue" />
          <button type="submit">Subscribe</button>
        </form>
        """;

    private const string SubscribeCode = """
        using Volt;

        namespace Starter.Pages;

        /// <summary>Code-behind for Pages/subscribe.volt: typed form handling without JavaScript.</summary>
        public sealed partial class Subscribe
        {
            public string? SavedEmail;
            public IReadOnlyList<VoltFormError> FormErrors = Array.Empty<VoltFormError>();
            public string EmailValue = "";
            public string NameValue = "";

            public override Task<VoltPostResult> OnPostAsync(VoltRequest request)
            {
                if (request.TryForm<Starter.Models.SubscribeForm>(out var form, out var errors))
                {
                    SavedEmail = form.Email;
                    FormErrors = Array.Empty<VoltFormError>();
                }
                else
                {
                    EmailValue = form.Email;
                    NameValue = form.Name ?? "";
                    FormErrors = errors;
                }
                return Task.FromResult(VoltPostResult.Render);
            }
        }
        """;

    private const string SubscribeFormCs = """
        using Volt;

        namespace Starter.Models;

        /// <summary>[VoltForm] — the generator emits a reflection-free binder + validators.</summary>
        [VoltForm]
        public sealed partial class SubscribeForm
        {
            [VoltRequired(Message = "email is required"), VoltEmail]
            public string Email { get; set; } = "";

            [VoltMaxLength(100)]
            public string? Name { get; set; }
        }
        """;

    private const string NotFoundCs = """
        using Volt;

        namespace Starter.Pages;

        public sealed class NotFound : VoltPage
        {
            public override void Render(HtmlWriter w, RenderContext ctx)
            {
                w.DocType();
                using (w.Html("en"))
                {
                    using (w.Head()) { w.MetaCharset(); w.Title("404 — Volt"); }
                    using (w.Body()) using (w.Main())
                    {
                        w.H1Text("404");
                        using (w.P()) { w.Text("Nothing here. "); using (w.A()) { w.Href("/"); w.Text("Go home"); } }
                    }
                }
            }
        }
        """;

    private const string ErrorCs = """
        using Volt;

        namespace Starter.Pages;

        public sealed class Error : VoltErrorPage
        {
            public override void Render(HtmlWriter w, RenderContext ctx)
            {
                w.DocType();
                using (w.Html("en"))
                {
                    using (w.Head()) { w.MetaCharset(); w.Title("500 — Volt"); }
                    using (w.Body()) using (w.Main())
                    {
                        w.H1Text("Something went wrong");
                        if (VoltRuntime.DevMode && Exception is not null)
                            using (w.Pre()) w.Text(Exception.ToString());
                    }
                }
            }
        }
        """;

    private const string CounterCs = """
        using System.Text.Json;
        using Volt;

        namespace Starter.Components;

        public sealed record CounterState(int Count);

        [VoltIsland]
        public sealed partial class Counter : VoltComponent<CounterState>
        {
            public override void Render(HtmlWriter w, RenderContext ctx)
            {
                using (w.Div())
                {
                    w.Class("counter");
                    using (w.Span()) w.Text("Count: " + State.Count);
                    using (w.Button())
                    {
                        w.Type("submit");
                        w.Name("__volt_action");
                        w.Value("increment");
                        w.DataVoltOn("click:increment");
                        w.Text("+1");
                    }
                }
            }

            [VoltAction]
            public static CounterState Increment(CounterState state, JsonElement args)
                => state with { Count = state.Count + 1 };
        }
        """;

    private const string Styles = """
        :root { color-scheme: light dark; font-family: system-ui, sans-serif; }
        body { max-width: 40rem; margin: 0 auto; padding: 2rem 1rem 3rem; line-height: 1.6; }
        h1 { letter-spacing: -0.02em; }
        nav { display: flex; gap: 1rem; padding: 0.5rem 0 1rem; }
        .brand { font-weight: 800; text-decoration: none; }
        footer { margin-top: 3rem; opacity: 0.7; font-size: 0.9rem; }
        .counter { display: flex; gap: 1rem; align-items: center; padding: 1rem; border: 1px solid #8884; border-radius: 8px; }
        .field-error { color: #c2410c; }
        .saved { color: #15803d; font-weight: 600; }
        form { display: flex; gap: 0.5rem; }
        input { padding: 0.5rem; border-radius: 6px; border: 1px solid #8886; }
        button { padding: 0.5rem 1rem; border-radius: 6px; cursor: pointer; }
        """;
}
