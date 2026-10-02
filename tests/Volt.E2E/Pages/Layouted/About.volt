@page /layouted/about
@mode SSR

@{ var widget = new global::Volt.E2E.Components.Widget("About widget", 7); }

<h1>Layouted about</h1>
@Card(widget)
