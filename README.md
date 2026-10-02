# ⚡ Volt.NET

Fullstack C# framework: **SSG + SSR + island hydration** in one box. File-based routing,
zero-allocation HTML rendering, built-in SEO, a CLI, a **Razor-like .volt template syntax**,
a choice of transports — the Kestrel bridge **or a built-in zero-allocation HTTP/1.1 server**
(+58% throughput, half the memory) — and **Native AOT** single-binary output.

```csharp
// Program.cs — the whole entry point
using Volt;
return VoltApp.Run(args);
```

Pages are plain C# classes in `Pages/`:

```csharp
public sealed class Index : VoltPage
{
    public override RenderMode Mode => RenderMode.SSG;   // or SSR (default)

    public override void Render(HtmlWriter w, RenderContext ctx)
    {
        w.DocType();
        using (w.Html("en"))
        {
            using (w.Head())
            {
                w.MetaCharset();
                w.Title("Hello, Volt");
                w.Link("stylesheet", "/styles.css");
            }
            using (w.Body())
            {
                using (w.Main()) w.H1Text("Hello, Volt");
                w.Island<Counter, CounterState>(new(0), ctx);   // interactive island
            }
        }
    }
}
```

Islands are server-rendered components with typed actions — **no JavaScript required**:

```csharp
public sealed record CounterState(int Count);

[VoltIsland]
public sealed partial class Counter : VoltComponent<CounterState>
{
    public override void Render(HtmlWriter w, RenderContext ctx)
    {
        using (w.Button())
        {
            w.Type("submit");
            w.Name("__volt_action");
            w.Value("increment");
            w.DataVoltOn("click:increment");   // hydrate.js intercepts when JS is on
            w.Text("+1");
        }
    }

    [VoltAction]
    public static CounterState Increment(CounterState state)
        => state with { Count = state.Count + 1 };
}
```

The source generator emits typed JSON (de)serializers, action dispatch and fragment rendering
for every island — **reflection-free, Native AOT safe**. With JS on, `hydrate.js` (11 KB, zero
dependencies) posts actions to `/_volt/action` and morphs the returned fragment. With JS off,
the same button posts the wrapping form to `/_volt/fallback`; the server parks the new state
under a one-time token and redirects back — the island state survives a full page reload.

## Measured on this machine (M1)

| Metric | Value |
|---|---|
| Page render hot path (route match + ~1.5 KB HTML) | **~19 µs**, **0 B allocated** |
| Route match (8 routes) | **~1.4 µs**, **0 B allocated** |
| Native AOT startup (process → first 200) | **~55 ms** |
| Peak RSS (VmHWM, AOT) | **~17.5 MB** |
| Native AOT binary (self-contained) | **8.3 MB** |
| hydrate.js | **11 KB**, no dependencies |
| Fast-path serve (route match + SSG cache hit + body) | **~3.2 µs**, **0 B allocated** |
| Throughput, 8 keep-alive conns (built-in server vs Kestrel) | **26.6k vs 16.9k rps (+58%)** |
| Request latency, sequential (built-in server vs Kestrel) | **0.15 vs 0.18 ms** |
| Peak RSS JIT (built-in server vs Kestrel) | **47 vs 90 MB** |
| AOT with the built-in server: startup / peak RSS | **~25 ms / ~6 MB** |

Allocation gates are enforced by tests: `GC.GetAllocatedBytesForCurrentThread()` deltas are
asserted to be exactly **0** for route matching and page rendering
([ZeroAllocGateTests](tests/Volt.Core.Tests/ZeroAllocGateTests.cs)). Island actions and the
no-JS fallback intentionally allocate small amounts (JSON handling + one-time tokens) — the
hot path for page serving does not.

## Layout

```
src/Volt.Core/          rendering, routing, islands, SEO, export  (no dependencies)
src/Volt.Compiler/      Roslyn source generator (routes + islands + serializers)
src/Volt.Kestrel/       transport: Kestrel bridge (adapter over the shared engine)
src/Volt.Server/        transport: built-in zero-alloc HTTP/1.1 server (keep-alive, pipelining)
src/Volt.Hydration.JS/  hydrate.js runtime (embedded resource)
src/Volt.Cli/           `volt` CLI (new / dev / build / export / serve)
examples/starter/       demo app: SSG + dynamic route + island
apps/docs/              this documentation site, built with Volt itself
tests/                  unit + compiler + E2E + benchmarks
```

## Routing

| File | Route |
|---|---|
| `Pages/Index.cs` | `/` |
| `Pages/about.cs` | `/about` (static segments lowercased) |
| `Pages/Blog/[slug].cs` | `/blog/{slug}` |
| `Pages/Shop/[...rest].cs` | `/shop/{*rest}` (catch-all) |
| `Pages/notfound.cs` | 404 page |
| `Pages/error.cs` | 500 page (`VoltErrorPage`) |

`ctx.Params.Get("slug")` reads route params (URL-decoded). Dynamic SSG pages implement
`StaticPaths()` — each path is prerendered at export time and into the sitemap.

## .volt templates (M2)

Pages can also be `.volt` files — Razor-like markup compiled by the source generator into
the same `VoltPage` render code (no runtime interpretation):

```
@page /docs/guide
@mode SSG
@revalidate 60

<!DOCTYPE html>
<html lang="en">
<body>
    <h1>Guide</h1>
    @for (int i = 1; i <= 3; i++) {
        <li>Item @i of 3</li>
    }
    @if (ctx.Request.Path.Length > 5) {
        <p>path: @(ctx.Request.Path)</p>
    }
</body>
</html>
```

Directives: `@page`, `@mode`, `@revalidate`, `@namespace`. In markup: `@expr` / `@(expr)`
interpolate, `@{ … }` runs raw C#, `@@` is a literal `@`. Code blocks switch back to markup
when a line starts with a tag. `notfound.volt` / `error.volt` are special pages.
This site's [templates page](apps/docs/Pages/Templates.volt) is written in .volt.

## Transports (M2)

`VoltApp.Run` (Kestrel) or `VoltServerApp.Run` (built-in server) — the same engine
([VoltEngine](src/Volt.Core/Hosting/VoltEngine.cs)) serves both:

* **Built-in server**: hand-rolled HTTP/1.1 over raw sockets — pooled buffers, offset-based
  parsing, keep-alive and pipelining, `Expect: 100-continue`, ETag/304, static assets from
  memory. The per-request fast path (parse → route match → cached SSG serve) allocates
  **0 bytes**, gated by tests.
* **Kestrel bridge**: for TLS, HTTP/2/3 and the ASP.NET ecosystem.

## Render modes

* **SSG** (`Mode => RenderMode.SSG`): rendered once, cached in a bounded LRU (256 entries),
  served with ETag/`304`. `RevalidateSeconds` enables ISR: stale entries are served
  immediately and re-rendered in the background (single-flight).
* **SSR** (default): rendered per request, streamed straight into the response buffer.
* **Export** (`volt export` or `VOLT_RUN_MODE=export`): writes `dist/` with
  `page/index.html` clean URLs, `sitemap.xml`, `robots.txt` and `wwwroot/` assets.

## SEO built in

* `w.Meta("description", …)`, `w.MetaProperty("og:title", …)` — escaped attribute writes.
* `/sitemap.xml` — static + dynamic SSG routes (via `StaticPaths()`), ETag-cached.
* `/robots.txt` — generated from the configured `BaseUrl`.

## CLI

```
volt new <path>             scaffold an app
volt dev [path] [--port N]  dev server (dotnet watch hot reload)
volt build [path] [--rid R] Native AOT publish (default rid linux-x64, --no-aot to skip)
volt export [path] [--out DIR] [--base-url U]  static export to dist/
volt serve [path] [--port N] run without watch
```

## Getting started (repo)

```bash
dotnet build                          # builds everything
DOTNET_ROLL_FORWARD=Major dotnet run --project examples/starter   # serve the demo
DOTNET_ROLL_FORWARD=Major dotnet test # 101 tests: unit + compiler + E2E (both transports)
dotnet run -c Release --project tests/Volt.Benchmarks              # benchmarks
```

> The repo targets `net10.0` and builds with the .NET SDK 11 preview; running on the
> installed 11-preview runtime needs `DOTNET_ROLL_FORWARD=Major` (SDKs ≥ 11.0 GA won't).

## Design notes

* **HtmlWriter** is a pooled class writing UTF-8 directly into `IBufferWriter<byte>`
  (Kestrel's pipe): no `StringBuilder`, no intermediate strings, no `ToArray`.
  Stack buffers ≤ 1 KB, `SkipLocalsInit`, `SearchValues`-based escaping.
* **Router**: patterns are sorted static → param → catch-all; matching writes
  `RawParam` offsets into a caller stackalloc — params decode lazily, only when read.
* **Islands**: state is UTF-8 JSON; the generator emits `Utf8JsonWriter` serialization
  per state property (int/long/bool/double/float/decimal/string + nullables) and
  `JsonDocument` deserialization into records/POCOs. No reflection anywhere.
* **No-JS fallback**: one-time hex tokens in a bounded LRU store (1024 entries, 10 min TTL)
  — the only server-side state in Volt.
* **CONTRACT.md** is the normative protocol for the island markup, action and fallback
  flows; `hydrate.js` implements it exactly.

## Milestone status

M1: folder routing, islands + hydration, SSG/SSR/ISR, export, SEO, CLI, Native AOT,
tests, benchmarks — **done**.

M2: built-in zero-allocation HTTP/1.1 server (transport-neutral engine + Kestrel adapter,
+58% throughput, AOT ~25 ms / ~6 MB), `.volt` Razor-like templates, in-memory static
assets — **done**. Planned M3: WASM islands, TLS on the built-in server, dist bundling
into the binary, `<volt-island>` in .volt markup.
