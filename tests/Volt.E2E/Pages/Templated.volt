@page /templated
@mode SSG

<!DOCTYPE html>
<html lang="en">
<head>
    <title>Templated — Volt</title>
</head>
<body>
    <h1 class="headline">From a .volt template</h1>
    <ul class="items">
    @for (int i = 1; i <= 3; i++) {
        <li>Item @(i) of 3</li>
    }
    </ul>
    @if (ctx.Request.Path.Length > 0) {
        <p id="path-echo">path: @(ctx.Request.Path)</p>
    }
    <p>@@literal</p>
</body>
</html>
