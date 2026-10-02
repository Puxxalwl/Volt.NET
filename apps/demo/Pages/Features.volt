@page /features
@mode SSG
@tag home

<h1>Features</h1>
<section>
  @Feature("Folder routing", "Files under Pages/ become routes — /Pages/blog/post.volt is served at /blog/post. No registration code.")
  @Feature("Layouts & partials", "_Layout.volt wraps every page in its tree; @partial turns markup into a reusable C#-typed component.")
  @Feature("Built-in SEO", "sitemap.xml and robots.txt are generated from the route table at runtime — no extra packages.")
  @Feature("Live reload", "volt dev rerenders templates on save; the browser reloads itself — one fetch per second, no websockets.")
  @Feature("HTML minification", "VoltMinify collapses whitespace at compile time (in the generator) and after render — safe rules only.")
  @Feature("Metrics & HUD", "Prometheus-text counters and microsecond histograms at /_volt/metrics; a live page at /_volt/hud.")
  @Feature("On-demand revalidation", "Tag pages with @tag, evict them by tag over HTTP — the next request re-renders fresh.")
  @Feature("Testing utilities", "VoltTestServer drives the engine in-process (no sockets) with HTML golden-file snapshots.")
</section>
