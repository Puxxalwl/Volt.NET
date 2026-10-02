@page /
@mode SSG
@tag home

<section class="hero">
  <h1>Fullstack C#, zero JavaScript bloat</h1>
  <p class="lead">Static-first pages, islands where you need them, typed forms without JavaScript — compiled, minified, AOT-ready.</p>
  <p><a class="cta" href="/features">Explore the features →</a></p>
</section>
<section>
  @Feature("Static by default", "Pages render once at build time (or first hit) and serve from memory — a cached page costs about 3 microseconds.")
  @Feature("Islands when needed", "Drop a C# component into markup; it hydrates on the client and still works without JavaScript.")
  @Feature("Typed forms", "Bind POST data to a C# class, validate with attributes, re-render with errors — no scripts required.")
</section>
