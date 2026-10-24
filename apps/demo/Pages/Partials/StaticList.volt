@partial StaticList(string title)

<h3>@title</h3>
<ul>
@for (int i = 1; i <= 3; i++) {
  <li>Release @(i)</li>
}
</ul>
