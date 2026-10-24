@partial Counter(string label)

<div class="card">
  <strong>@label</strong>
  @if (ctx.Request?.IsExport == true) {
    Volt.Demo.Site.RenderCounterPreview(w, ctx);
  } else {
    <volt-island name="Counter" state='{"count":0}' />
  }
</div>
