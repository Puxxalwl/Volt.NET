namespace Volt.Cli;

/// <summary>Files written by `volt new` (kept in sync with examples/starter).</summary>
internal static class AppTemplate
{
    public static IEnumerable<(string Rel, string Content)> Files(string appName) =>
    [
        (appName + ".csproj", Csproj),
        ("Program.cs", ProgramCs),
        ("Pages/Index.cs", IndexCs),
        ("Pages/About.cs", AboutCs),
        ("Pages/Blog/[slug].cs", SlugCs),
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
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Volt.Kestrel" Version="1.0.0" />
            <PackageReference Include="Volt.Compiler" Version="1.0.0" PrivateAssets="all"
                              OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
          </ItemGroup>
        </Project>
        """;

    private const string ProgramCs = """
        using Volt;

        return VoltApp.Run(args);
        """;

    private const string IndexCs = """
        using Volt;

        namespace Starter.Pages;

        public sealed class Index : VoltPage
        {
            public override RenderMode Mode => RenderMode.SSG;

            public override void Render(HtmlWriter w, RenderContext ctx)
            {
                w.DocType();
                using (w.Html("en"))
                {
                    using (w.Head())
                    {
                        w.MetaCharset();
                        w.Title("Volt — starter");
                        w.Meta("description", "A Volt app: SSG + SSR + island hydration in C#.");
                        w.Link("stylesheet", "/styles.css");
                    }
                    using (w.Body())
                    {
                        using (w.Header()) w.H1Text("⚡ Volt starter");
                        using (w.Main())
                        {
                            using (w.P()) w.Text("Static page (SSG) with an interactive island:");
                            w.Island<Starter.Components.Counter, Starter.Components.CounterState>(
                                new Starter.Components.CounterState(0), ctx);
                            using (w.P())
                            {
                                w.Text("Dynamic route: ");
                                using (w.A()) { w.Href("/blog/hello-volt"); w.Text("/blog/hello-volt"); }
                            }
                        }
                    }
                }
            }
        }
        """;

    private const string AboutCs = """
        using Volt;

        namespace Starter.Pages;

        public sealed class About : VoltPage
        {
            public override RenderMode Mode => RenderMode.SSG;

            public override void Render(HtmlWriter w, RenderContext ctx)
            {
                w.DocType();
                using (w.Html("en"))
                {
                    using (w.Head())
                    {
                        w.MetaCharset();
                        w.Title("About — Volt");
                        w.Link("stylesheet", "/styles.css");
                    }
                    using (w.Body())
                    {
                        using (w.Main())
                        {
                            w.H1Text("About");
                            using (w.P()) w.Text("Server-side rendered by Volt with zero allocations.");
                            using (w.P())
                            {
                                w.Text("Back to ");
                                using (w.A()) { w.Href("/"); w.Text("home"); }
                            }
                        }
                    }
                }
            }
        }
        """;

    private const string SlugCs = """
        using Volt;

        namespace Starter.Pages;

        public sealed class Post : VoltPage
        {
            // dynamic SSG: StaticPaths prerenders each slug at export time
            public override RenderMode Mode => RenderMode.SSG;

            public override IEnumerable<string> StaticPaths() => ["hello-volt", "why-csharp"];

            public override void Render(HtmlWriter w, RenderContext ctx)
            {
                var slug = ctx.Params.Get("slug") ?? "unknown";
                var title = slug == "hello-volt" ? "Hello, Volt" : "Why C# for the web";
                w.DocType();
                using (w.Html("en"))
                {
                    using (w.Head())
                    {
                        w.MetaCharset();
                        w.Title(title + " — Volt");
                        w.Meta("description", "A dynamic SSG page: " + slug);
                        w.Link("stylesheet", "/styles.css");
                    }
                    using (w.Body())
                    {
                        using (w.Main())
                        {
                            w.H1Text(title);
                            using (w.P()) w.Text("Slug: " + slug);
                            using (w.P())
                            {
                                w.Text("Back to ");
                                using (w.A()) { w.Href("/"); w.Text("home"); }
                            }
                        }
                    }
                }
            }
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
                    using (w.Body())
                    {
                        using (w.Main())
                        {
                            w.H1Text("404");
                            using (w.P())
                            {
                                w.Text("Nothing here. ");
                                using (w.A()) { w.Href("/"); w.Text("Go home"); }
                            }
                        }
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
                    using (w.Body())
                    {
                        using (w.Main())
                        {
                            w.H1Text("Something went wrong");
                            if (VoltRuntime.DevMode && Exception is not null)
                            {
                                using (w.Pre()) w.Text(Exception.ToString());
                            }
                        }
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
                    using (w.Span())
                    {
                        w.Class("count");
                        w.Text("Count: " + State.Count);
                    }
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
        body { max-width: 40rem; margin: 2rem auto; padding: 0 1rem; line-height: 1.6; }
        h1 { font-size: 1.6rem; }
        .counter { display: flex; gap: 1rem; align-items: center; padding: 1rem; border: 1px solid #8884; border-radius: 8px; }
        .count { font-variant-numeric: tabular-nums; font-weight: 600; }
        button { padding: 0.4rem 1rem; border-radius: 6px; cursor: pointer; }
        pre { overflow: auto; }
        """;
}
