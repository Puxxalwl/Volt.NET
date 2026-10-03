@page /whats-new
@namespace Docs.Pages
@mode SSG

<h1>M6: developer experience</h1>
<p>The full list of M6 features with examples: layouts, partials, forms, testing, live reload, minification, metrics, revalidation.</p>

<h2>Layouts</h2>
<p>Place <code>_Layout.volt</code> next to your pages; <code>@@renderbody</code> marks the slot. Nested layouts compose at compile time.</p>

<h2>Typed forms without JavaScript</h2>
<p>Mark a partial class with <code>[VoltForm]</code> and POST to the page route: the generator emits a reflection-free binder and validators.</p>

<h2>Try it</h2>
<p><code>volt new myblog --template blog</code> scaffolds an app with layouts, a subscribe form, tags and minification enabled.</p>
