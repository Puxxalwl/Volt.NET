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

Directives: `@page`, `@mode`, `@revalidate`, `@namespace`, `@tag`, `@class`, `@layout`,
`@partial`. In markup: `@expr` / `@(expr)` interpolate, `@{ … }` runs raw C#, `@@` is a
literal `@`. Code blocks switch back to markup when a line starts with a tag.
`notfound.volt` / `error.volt` are special pages.

**Layouts (M6)** — `Pages/_Layout.volt` wraps every page in its tree; `@renderbody` marks
the slot. Nested layouts compose at compile time (a named layout nests into the nearest
default `_Layout.volt`), `@layout none` opts out. The splice is done by the generator —
**zero runtime cost**.

**Partials (M6)** — `@partial Feature(string title, string body)` in a `.volt` file makes
it a typed component; call it from any page: `@Feature("Fast", "3µs per cached page")`.
Partials compile into a static class — no runtime lookup, no reflection.

**Code-behind (M6)** — .volt pages emit `partial` classes: `@class Subscribe` pairs
`Pages/subscribe.volt` with your own `partial class Subscribe` (fields, `OnPostAsync`, …).
Islands embed directly: `<volt-island name="Counter" state='{"count":0}' />`
(a `[VoltIsland(Wasm = "…")]` component adds `data-v-wasm` and dispatches client-side —
[the module contract](CONTRACT.md); `volt wasm validate module.wasm` checks a module
against it).
This site's [templates page](apps/docs/Pages/Templates.volt) is written in .volt.

## M6: the DX feature set

Everything that makes a framework pleasant to use day-to-day — and none of it exists
out-of-the-box in Blazor/Razor Pages/Next.js in this combination:

- **Layouts & partials** — compile-time spliced, zero runtime cost (above).
- **Middleware** — `options.Use(async (ctx, next) => …)` onion around the whole
  pipeline: headers, auth, redirects, short-circuits. `options.OnException` for
  custom 500s.
- **Typed forms, no JavaScript** — `[VoltForm]` on a partial class generates a
  reflection-free binder: `OnPostAsync` + `request.TryForm<T>(out form, out errors)`
  with `[VoltRequired]`, `[VoltEmail]`, `[VoltRange]`, `[VoltMaxLength]` validation.
  Errors re-render server-side; a successful flow can `VoltPostResult.Redirect`
  (PRG). Works with JavaScript disabled — islands' no-JS fallback philosophy,
  applied to forms.
- **Volt.Testing** — `VoltTestServer.Create()` runs the engine in-process (no
  sockets, no Kestrel): `GetAsync` / `PostAsync` + `AssertMatchesSnapshot()` HTML
  golden files (`__snapshots__/`, `VOLT_UPDATE_SNAPSHOTS=1` to (re)create).
  Framework-level page testing, like snapshot tests in the JS world.
- **Live reload** — `volt dev` watches Pages/ and wwwroot/; DevMode pages carry a
  ~180-byte poll script that reloads the tab when templates change or the app
  rebuilds. DevMode also bypasses the SSG cache (always-fresh renders).
- **HTML minification** — `<VoltMinify>true</VoltMinify>`: .volt static text is
  collapsed at compile time; rendered output at runtime. Safe rules only
  (pre/script/style/textarea and attribute values are never touched). A byte-level
  streaming minifier, in-place, allocation-free.
- **Metrics & HUD** — `EnableMetrics` → `/_volt/metrics` in Prometheus text format
  (requests, fast-path, SSG hits, status classes, µs histogram) and `/volt/hud` —
  a live auto-refreshing page. Lock-free striped counters, branch-gated when off.
- **On-demand revalidation** — `@tag products` on a page; later
  `VoltRuntime.RevalidateTag("products")`, `POST /_volt/revalidate` (token-guarded)
  or `volt revalidate --tag products --token S` evicts those cached entries; the
  next request re-renders.
- **Demo site** — [apps/demo](apps/demo) exports to a static site
  (`volt export`) and CI deploys it to GitHub Pages:
  **[puxxalwl.github.io/Volt.NET](https://puxxalwl.github.io/Volt.NET/)**.

## Transports (M2)

`VoltApp.Run` (Kestrel) or `VoltServerApp.Run` (built-in server) — the same engine
([VoltEngine](src/Volt.Core/Hosting/VoltEngine.cs)) serves both:

* **Built-in server**: hand-rolled HTTP/1.1 over raw sockets — pooled buffers, offset-based
  parsing, keep-alive and pipelining, `Expect: 100-continue`, ETag/304, static assets from
  memory. The per-request fast path (parse → route match → cached SSG serve) allocates
  **0 bytes**, gated by tests.
* **TLS (M3)**: `--tls-cert cert.pem --tls-key key.pem` (or `VOLT_TLS_CERT`/`VOLT_TLS_KEY`)
  wraps accepted connections in `SslStream` — same zero-alloc path, scheme switches to https.
* **Kestrel bridge**: for HTTP/2/3 and the ASP.NET ecosystem.

## Volt vs Blazor

Both are C# fullstack frameworks. The difference is the interaction model — and it
shows up in latency, memory and page weight. Volt's own numbers below are measured
on this repo's benchmarks; Blazor figures are architectural facts, not my benchmarks.

| | **Volt.NET** | **Blazor Server** | **Blazor WASM / Auto** |
|---|---|---|---|
| Interaction path | plain HTTP POST → typed static action → HTML fragment; WASM dispatch is optional | SignalR circuit (websocket, one per user, state lives on the server) | runs in the browser |
| Server memory per interactive user | **none** — state travels in a token, cache shared by SSG | circuit + render tree diff state held for the connection lifetime | none (client) |
| Fallback without JS | **works** — real form POST + 303 + token re-render | broken | broken (WASM *is* the JS) |
| First load | **~11 KB** hydrate.js, static HTML immediately | app shell + circuit setup | .NET runtime download (MBs even trimmed), loading UI |
| Crawlability / SEO | SSG pages are plain HTML; meta/sitemap/robots built in | needs prerender for SEO | needs prerender |
| Render model | explicit `HtmlWriter` calls, cached SSG = serve 0-alloc bytes | virtual render tree, per-interaction diff over the circuit | render tree, client-side diff |
| Actions | static `(State, args) → State` methods, unit-testable without a host | component instance methods + event system | component instance methods |
| Native AOT | yes — full stack: ~25 ms cold start, ~6 MB RSS, one binary | server: yes (with limits); client: no (wasm runtime) | no AOT path for the client payload |

Honest counterpoints — Blazor wins where you need: a mature component ecosystem
(MudBlazor, Telerik…), rich client-side state/UI composition, forms validation
built into components, and a large hiring pool. If your app is mostly *content +
islands of interactivity* (catalogs, marketing, blogs, landing pages, tools),
Volt's model costs less memory and bytes per user; if it's a dense CRUD SPA,
Blazor's component model is the safer bet.

Why Volt is lean by construction:

* **Stateless beats stateful.** A Blazor Server circuit pins memory and a websocket
  per user for as long as they're on the page. A Volt island is stateless HTML —
  the state rides a token, actions are one-shot POSTs. The server keeps only the
  SSG cache, shared across all users.
* **Ship HTML, not a runtime.** The interactive payload is 11 KB of vanilla JS;
  WASM islands are opt-in per component, not the default app model.
* **Serve from memory.** The zero-alloc fast path answers a cached SSG page in
  ~3.2 µs with 0 bytes allocated; assets are embedded in the single binary.

## M4: shared cache, immutable assets, wasm tooling

* **Shared/distributed SSG cache** — `VoltOptions.SsgCacheDirectory` (env
  `VOLT_SSG_CACHE_DIR`): rendered pages persist to disk (atomic writes) and are
  visible to every instance pointing at the same directory — warm caches across
  restarts and multi-node shared-disk deployments. The in-process LRU stays in
  front; a render-counter test proves a second instance serves without re-rendering.
* **Immutable hydrate asset** — pages reference `/_volt/hydrate.<fnv16>.js`,
  served with `Cache-Control: public, max-age=31536000, immutable`. A stale hash
  404s, so clients never cache the wrong script forever. The legacy URL works.
* **`volt wasm validate`** — parses wasm binaries and checks the island module
  contract (memory/alloc/volt_dispatch/volt_render signatures). The protocol is
  conformance-tested against a real hand-emitted module under Node
  (`tests/Volt.E2E/wasm-conformance.js`).

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
volt new <path> [--template blog]  scaffold an app (blog: .volt layouts + typed forms + minify)
volt dev [path] [--port N]  dev server (dotnet watch hot reload, browser live-reload)
volt revalidate --url U --tag T --token S  evict cached pages by tag
volt build [path] [--rid R] Native AOT publish (default rid linux-x64, --no-aot to skip)
volt export [path] [--out DIR] [--base-url U]  static export to dist/
volt serve [path] [--port N] run without watch
```

## Install from NuGet

Published automatically by [build.yml](.github/workflows/build.yml) via **NuGet
trusted publishing** (OIDC, no API keys): push a tag `v0.4.0` → all packages
versioned `0.4.0` land on nuget.org.

```bash
# scaffold (CLI as a dotnet global tool)
dotnet tool install --global Volt.Cli
volt new mysite && cd mysite && dotnet run
```

Or wire an existing console app:

```xml
<ItemGroup>
  <PackageReference Include="Volt.Server" Version="*" />
  <!-- the source generator + .volt compiler: reference as an analyzer -->
  <PackageReference Include="Volt.Compiler" Version="*" PrivateAssets="all"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>
```

Packages: `Volt.Server` (built-in zero-alloc HTTP/1.1 server) · `Volt.Kestrel`
(Kestrel bridge) · `Volt.Core` (engine/runtime) · `Volt.Compiler` (source
generator, analyzer package) · `Volt.Hydration.JS` (hydrate.js payload) ·
`Volt.Cli` (`volt` tool). All MIT.

## Getting started (repo)

```bash
dotnet build                          # builds everything
DOTNET_ROLL_FORWARD=Major dotnet run --project examples/starter   # serve the demo
DOTNET_ROLL_FORWARD=Major dotnet test # 123 tests: unit + compiler + E2E (both transports)
# CI (.github/workflows/build.yml): build + test + pack + consumer smoke on every push;
# pushing a v* tag publishes all packages to nuget.org via trusted publishing
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
assets — **done**.

M3: `<volt-island>` in .volt markup, WASM island protocol (client-side dispatch,
module contract in CONTRACT.md), TLS on the built-in server (SslStream + PEM certs),
static assets embedded in the binary (single-file/AOT works without wwwroot on disk) —
**done**.

M4: shared/distributed SSG cache (file backend + cross-instance serving proven by
tests), immutable content-hashed hydrate asset, `volt wasm validate` + Node
conformance of the WASM protocol — **done**.

M5: NuGet packages (7) + trusted publishing from CI tags — **done**.

M6: developer experience — layouts/partials, middleware, typed forms without
JavaScript, Volt.Testing with golden files, live reload, compile-time +
runtime HTML minification, Prometheus metrics + HUD, on-demand revalidation
tags, `volt new --template blog`, demo site on GitHub Pages — **done**.
Planned M7: auth/sessions, i18n, streaming SSR, runtime template compilation.
