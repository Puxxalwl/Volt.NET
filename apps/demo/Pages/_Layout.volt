<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Volt.NET — C# web framework / Technical showcase</title>
  <meta name="description" content="Explore Volt.NET through real code: compiled .volt templates, static export, server-rendered islands and typed forms." />
  <link rel="stylesheet" href="@(Volt.Demo.Site.Root(ctx))styles.css" />
  <link rel="icon" href="@(Volt.Demo.Site.Root(ctx))favicon.svg" type="image/svg+xml" />
</head>
<body>
  <a class="skip-link" href="#main">Skip to content</a>
  <header class="site-header">
    <nav class="shell nav" aria-label="Main navigation">
      <a class="brand" href="@(Volt.Demo.Site.Root(ctx))"><span class="bolt" aria-hidden="true">ϟ</span> Volt.NET</a>
      <div class="nav-links"><a href="@(Volt.Demo.Site.Root(ctx))#playground">Demos</a><a href="@(Volt.Demo.Site.Root(ctx))#architecture">How it works</a><a href="@(Volt.Demo.Site.Root(ctx))#resources">Resources</a></div>
      <a class="github-link" href="https://github.com/Puxxalwl/Volt.NET">GitHub <span aria-hidden="true">↗</span></a>
    </nav>
  </header>
  <main class="shell" id="main">
    @renderbody
  </main>
  <footer class="site-footer shell"><a class="brand" href="@(Volt.Demo.Site.Root(ctx))"><span class="bolt" aria-hidden="true">ϟ</span> Volt.NET</a><p>Compiled with Volt. Served as HTML.</p><a href="https://github.com/Puxxalwl/Volt.NET/blob/main/LICENSE">Open source / MIT ↗</a></footer>
</body>
</html>
