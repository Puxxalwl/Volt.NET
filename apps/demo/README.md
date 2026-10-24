# Volt.NET technical showcase

English-language framework demos built with `.volt` templates, a shared layout,
typed partials and C# components. No third-party frontend framework.

## Run locally

Install the SDK pinned in the repository's `global.json`, then:

```sh
cd apps/demo
DOTNET_ROLL_FORWARD=Major dotnet run -c Release -- --port 5000
```

- `/` — overview and rendering model.
- `/features/` — SSG output, typed-form walkthrough and routing conventions.
- `/islands/` — real server-backed Counter (also works without JavaScript).
- `/signup/` — SSR form binder/validation example; no data is stored.
- `/about/` — scope, limitations and source references.

Use trailing slashes for demo routes: relative assets and navigation then work
both locally and under the GitHub Pages `/Volt.NET/` project prefix.

## Static export

```sh
cd apps/demo
VOLT_RUN_MODE=export VOLT_EXPORT_DIR="$PWD/../../dist" \
VOLT_BASE_URL=https://puxxalwl.github.io/Volt.NET \
DOTNET_ROLL_FORWARD=Major dotnet run -c Release
python3 ../../scripts/check-demo-export.py ../../dist
```

Only the four SSG pages are exported. The signup SSR handler is intentionally
absent. The static Counter is the real component's initial render, but its
button is disabled and no action form or hydration script is emitted by the
page. The exporter may still copy the framework hydration asset to disk.

## Hosting boundaries

| Feature | Static showcase | Local application |
| --- | --- | --- |
| Templates, layouts, partials, loops | Real generated HTML | Real generated HTML |
| Counter state/action | Initial render only | C# action + fragment response |
| No-JS island fallback | Not available | Server POST + redirect |
| Typed form binding/validation | Code and read-only preview | Real POST to `/signup/` |
| WASM islands | Contract explanation only | Requires a compatible module |

The WASM fixture proves the low-level protocol, not automatic C#-to-WASM
compilation. No JavaScript reimplementation of the Counter is used here.

## Known framework constraints encountered

- `IslandByName` calls a generated fragment delegate that does not forward the
  page's `VoltRequest.IsExport`. The showcase renders the component directly in
  its export-only partial branch, so the disabled state is reliable.
- Template literal entities are escaped again by `HtmlWriter`. Code panels use
  C# string expressions so `<`, `>` and `&` are escaped exactly once.
- Quoted attribute expressions cannot contain arbitrary nested quoted strings.
  The email preview uses a named C# constant instead.
- The shared layout has one document title/description; per-page head slots are
  not implemented here. No framework API was changed for this redesign.
- This showcase has only root and one-segment SSG routes. `Site.Root` is explicitly
  scoped to that tree, rather than presented as a general base-path solution.

## Design and evidence

Graphite surfaces, amber wayfinding, system typography and code/result pairs.
Navigation separates demos, architecture and source resources. No animations or
client-side showcase scripts are required. Focus indicators, a skip link,
reduced-motion handling and narrow-screen layouts are included.

Reference study: Astro's separation of content rendering and islands, htmx's
concrete request examples, Svelte's code-first teaching, Hono's compact entry
point, Vite's resource hierarchy and Bun's reproducible measurement context.
The layout and visual identity are specific to Volt, not copies of those sites.

Performance numbers are intentionally omitted. Link to the repository's
allocation gates and BenchmarkDotNet scenarios instead of implying that local
microbenchmarks predict application throughput.
