# Volt Island & Routing Protocol v1

This document is the **normative contract** between the Volt server runtime (C#) and the
client hydration runtime (`/_volt/hydrate.js`). Both sides must implement exactly this.

## 1. Island markup (server → browser)

Every interactive island is emitted wrapped in a no-JS-fallback form:

```html
<form method="post" action="/_volt/fallback" data-v-form>
  <input type="hidden" name="__volt_island" value="Counter">
  <input type="hidden" name="__volt_state" value='{"count":0}'>
  <input type="hidden" name="__volt_sid" value="i0">
  <volt-island data-v="Counter" data-sid="i0" data-props='{"count":0}'>
    <!-- server-rendered component HTML -->
    <button type="submit" name="__volt_action" value="increment"
            data-v-on="click:increment">+1</button>
  </volt-island>
</form>
```

Notes:
- `data-props` and `__volt_state` contain the **same state JSON**, HTML-attribute-escaped
  (`&` `<` `>` `"` `'` → entities) by the server.
- `data-v` is the island **type name**; `data-sid` is a stable per-render island id
  (`i0`, `i1`, … in render order). SIDs are deterministic for a given page render.
- The hydration boot script is emitted **once per document**, right after the first island:

```html
<script src="/_volt/hydrate.<16-hex-fnv-hash>.js" defer></script>
        (M4: versioned URL, served with `Cache-Control: public, max-age=31536000, immutable`;
        the legacy `/_volt/hydrate.js` URL keeps working with ETag/304; a hashed
        URL with a mismatched version returns 404)
```

- Interactive elements carry `data-v-on="<event>:<actionName>"`.
  Supported events: `click`, `submit` (on `data-v-form` forms), `change`, `input`
  (debounced 150 ms).

## 2. Action request (browser → server, with JS)

`POST /_volt/action`, `Content-Type: application/json`, `Accept: application/json`:

```json
{
  "island": "Counter",
  "sid": "i0",
  "action": "increment",
  "state": { "count": 0 },
  "args": { "qty": "2" }
}
```

- `state` is the raw JSON state (taken from the island's current `data-props`).
- `args` is optional (default `{}`). It is an object of **string** values serialized from
  the form's named inputs (excluding `__volt_*` fields) inside the closest
  `form[data-v-form]` ancestor of the triggering element, if any.
- Response `200` `application/json`: `{"html":"<form …>…</form>"}` — the html is the
  complete replacement (outer element) for the current `form[data-v-form]`.
  The client replaces the form node with the parsed fragment (preserving focus if the
  focused element had an `id`, see §4).
- Non-2xx response: client logs `console.error` and does nothing else.

## 3. No-JS fallback (form POST)

Without JS the form posts normally (urlencoded) to `POST /_volt/fallback` with fields:
`__volt_island`, `__volt_state`, `__volt_sid`, `__volt_action` (from the submit button)
plus any other named inputs as args. Server answers `303 See Other` → back to the
referrer page with `?__v=<token>` which restores the updated island state. Nothing is
required from hydrate.js for this path.

## 4. Client behavior of hydrate.js

- **Boot** (`DOMContentLoaded` or immediately if already loaded): find all
  `volt-island` elements; parse `data-props`; keep a `Map` sid → element.
- **Event delegation**: single capture-phase listeners at `document` level for the
  supported events. Find closest `[data-v-on]` for the event type; if found, prevent
  default and dispatch the action (§2) for the closest `form[data-v-form]` ancestor.
- **Morph on action response**: replace the `form[data-v-form]` element with the
  response html. After replacing, if the previously focused element had an `id`, refocus
  the element with that id in the new tree.
- **Client-side router**: intercept clicks on `<a>` when all of: same-origin, href
  starts with `/`, no `target`, no `download`, `rel` does not contain `external`,
  plain left click (no modifiers), not `mailto:`/`tel:`/`#`-only. Links with
  `data-v-external` or `rel="external"` are never intercepted.
  Fetch the URL with header `X-Volt-Navigate: 1`, parse the response as HTML
  (DOMParser), replace `document.body` children via keyed morph (elements with
  `data-v-key` attributes are matched by key and updated in place; others replaced),
  update `document.title`, `history.pushState`. On `popstate`: fetch and morph, restore
  scroll position recorded at pushState time. Abort in-flight navigation when a new one
  starts (AbortController).
- **Prefetch**: on `pointerenter`/`focusin` (link hover/focus) and via
  IntersectionObserver (rootMargin `100px`) prefetch internal links:
  `fetch(url, { headers: { 'X-Volt-Prefetch': '1' } })`, store `Response` text in a
  Map capped at 20 entries with 30 s TTL (FIFO eviction). Navigations consume the cache.
- **No logging in production paths** except `console.error` on failures.
- **Constraints**: single ES module file, ES2020+, zero dependencies, no frameworks,
  ≤ 25 KB unminified, served as-is (no build step), strict mode.

## WASM islands (M3, experimental)

A `[VoltIsland(Wasm = "/islands/x.wasm")]` component changes the emitted markup and
the client dispatch path:

**Markup**: the `volt-island` element additionally carries
`data-v-wasm="/islands/x.wasm"`. The server registry keeps serving SSR markup,
the no-JS fallback form and server actions — the WASM module is an optimization
for the interactive path, not a replacement.

**Client contract (hydrate.js)**: when a `data-v-wasm` attribute is present on the
island element, `dispatchAction` does NOT POST to `/_volt/action`. Instead it
instantiates the module (`WebAssembly.instantiateStreaming`, cached by URL) and
calls, in module memory (UTF-8, NUL-terminated strings):

```
exports:
  memory:        WebAssembly.Memory
  alloc(n):      i32 offset          — allocate n bytes (n includes the NUL)
  volt_dispatch(islandPtr, islandLen, actionPtr, actionLen,
                statePtr, stateLen, argsPtr, argsLen) -> resultPtr
                   resultPtr: NUL-terminated new-state JSON, 0 = error
  volt_render(statePtr, stateLen) -> htmlPtr
                   htmlPtr: NUL-terminated replacement form markup
                   (the same CONTRACT form fragment /_volt/action returns), 0 = error
```

`hydrate.js` then applies the returned fragment via the standard morph path
(`applyFragment`). The island/action/args strings are exactly the wire values the
server flow sends (`island` name, action name, state object, args object).

**Module authors**: any language that can export the four symbols above works.
For C# components, a future `wasm` build mode of the Volt generator is planned
(the component render/dispatch code compiles to the same contract).

**Tooling (M4)**: `volt wasm validate <module.wasm>` checks a module against this
contract (parses the binary, verifies the exports and signatures) — wire it into CI.
The reference conformance test `tests/Volt.E2E/wasm-conformance.js` runs the protocol
against `tests/fixtures/calc.wasm` under Node.
