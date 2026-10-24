@page /
@mode SSG
@tag home

<section class="hero" id="overview">
  <div class="hero-copy">
    <p class="eyebrow">VOLT.NET / TECHNICAL SHOWCASE</p>
    <h1>Web pages in C#.<br /><span>HTML at the edge.</span></h1>
    <p class="lead">A .NET web framework with compiled templates, static export and server-rendered islands. Start with HTML. Add server interaction where it belongs.</p>
    <div class="actions"><a class="button primary" href="#playground">Explore the demos <span aria-hidden="true">↘</span></a><a class="button" href="https://github.com/Puxxalwl/Volt.NET">View source ↗</a></div>
    <p class="small">This site is built with Volt.NET and exported to static HTML.</p>
  </div>
  <div class="specimen">
    <div class="panel-label"><span>01 / TEMPLATE</span><span>Pages/index.volt</span></div>
    <pre><code>@("@page /\n@mode SSG\n\n<h1>Release notes</h1>\n<ul>\n@for (int i = 1; i <= 3; i++) {\n  <li>Release @(i)</li>\n}\n</ul>")</code></pre>
    <div class="panel-label output-label"><span>02 / RENDERED HTML</span><span>No client runtime needed</span></div>
    <div class="preview"><h2>Release notes</h2><ul class="release-list">
      @for (int i = 1; i <= 3; i++) {
        <li><span>Release @(i)</span><span class="small">Static HTML</span></li>
      }
    </ul></div>
  </div>
</section>
<div class="principles"><span>Compiled .volt templates</span><span>SSG + SSR</span><span>Typed server actions</span><span>Native AOT support</span></div>
<section class="section" id="playground">
  <div class="section-heading"><p class="eyebrow">01 / EXPLORE</p><h2>Read the code.<br />Inspect the result.</h2><p>Small examples of the actual rendering model. Each demo makes its runtime requirements explicit.</p></div>
  <div class="demo-links">
    <a href="./features/#static"><span class="demo-number">01</span><div><h3>Static rendering</h3><p>A template, a generated page and the files produced by export.</p></div><span class="status">Runs on Pages</span><span aria-hidden="true">↗</span></a>
    <a href="./islands/"><span class="demo-number">02</span><div><h3>Islands & actions</h3><p>A real C# Counter. Server-rendered markup, typed state and progressive enhancement.</p></div><span class="status server">Local server</span><span aria-hidden="true">↗</span></a>
    <a href="./features/#forms"><span class="demo-number">03</span><div><h3>Typed forms</h3><p>Generated binding and validation. An ordinary POST, not a JavaScript imitation.</p></div><span class="status server">Local server</span><span aria-hidden="true">↗</span></a>
    <a href="./features/#routing"><span class="demo-number">04</span><div><h3>Routes & templates</h3><p>Files become routes. Layouts and typed partials compose at compile time.</p></div><span class="status">Source walkthrough</span><span aria-hidden="true">↗</span></a>
  </div>
</section>
<section class="section" id="architecture">
  <div class="section-heading"><p class="eyebrow">02 / THE MODEL</p><h2>Compile once.<br />Choose when to render.</h2></div>
  <div class="pipeline"><div><span class="eyebrow">AUTHOR</span><h3>.volt + C#</h3><p>Templates, pages, layouts and components.</p></div><span aria-hidden="true">→</span><div><span class="eyebrow">COMPILE</span><h3>Source generators</h3><p>HtmlWriter calls, route registration and island dispatch.</p></div><span aria-hidden="true">→</span><div><span class="eyebrow">DELIVER</span><h3>HTML</h3><p>Export static files or render requests on a .NET server.</p></div></div>
  <div class="architecture-notes"><article><h3>Static or request-time</h3><p>SSG routes can be exported to disk. SSR pages run on the server. Static hosting does not execute C# request handlers.</p></article><article><h3>Interaction in islands</h3><p>The hydration runtime sends actions to the server and replaces the returned fragment. Without JavaScript, the form fallback still needs that server.</p></article><article><h3>AOT-aware generation</h3><p>Generated routes, binders and island serializers avoid reflection in their dispatch paths. Native AOT is a deployment option, not a browser runtime.</p></article></div>
</section>
<section class="section resources" id="resources"><div><p class="eyebrow">03 / BUILD SOMETHING</p><h2>Take it off this page.</h2><p>Use the starter for a local app. Read the implementation alongside the documentation.</p></div><div class="resource-links"><a href="https://github.com/Puxxalwl/Volt.NET/tree/main/examples/starter">Starter application <span>↗</span></a><a href="https://github.com/Puxxalwl/Volt.NET/tree/main/apps/docs">Documentation source <span>↗</span></a><a href="https://github.com/Puxxalwl/Volt.NET/blob/main/README.md">README & setup <span>↗</span></a><a href="./about/">About this showcase <span>→</span></a></div></section>
