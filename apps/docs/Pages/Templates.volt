@page /templates
@mode SSG

<!DOCTYPE html>
<html lang="en">
<head>
    <title>.volt templates — Volt.NET</title>
    <meta name="description" content="Razor-like .volt template syntax with source generation." />
</head>
<body>
    <h1>.volt templates</h1>
    <p>Pages can be plain C# classes or .volt templates — Razor-like markup compiled by the source generator into the same zero-alloc VoltPage render code.</p>
    <ul>
        <li><code>@@page /route</code> — the route (falls back to the file path)</li>
        <li><code>@@mode SSG</code> or <code>@@mode SSR</code> — the render mode</li>
        <li><code>@@revalidate 60</code> — ISR revalidation window</li>
        <li><code>@@{ ... }</code> — raw C# statements</li>
        <li><code>@@@@</code> — a literal @@ in text</li>
    </ul>
    <h2>Markup + code</h2>
    <pre>@@for (int i = 1; i &lt;= 3; i++) {
    &lt;li&gt;Item @@i of 3&lt;/li&gt;
}</pre>
    <p>Loops and conditionals switch between C# and markup; the generator emits the fluent HtmlWriter calls for you.</p>
    <p>Generated markup is also just markup:</p>
    @for (int i = 1; i <= 3; i++) {
    <li>Sample item @(i) — rendered live by the loop above</li>
    }

    <h2>Islands in markup</h2>
    <pre>&lt;volt-island name="Counter" state='{"count":0}' /&gt;</pre>
    <p>Embeds an interactive island by registry name with wire-format JSON state — the full form, fallback and hydration markup is emitted for you. A component declared with <code>@@[VoltIsland(Wasm = "…")]</code> adds <code>data-v-wasm</code> and dispatches actions client-side (see CONTRACT.md).</p>
    <p>This very page is a .volt file — check <code>apps/docs/Pages/Templates.volt</code>.</p>
</body>
</html>
