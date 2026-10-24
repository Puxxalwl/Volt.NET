#!/usr/bin/env python3
"""Validate Volt showcase export without npm or external Python dependencies."""
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit
import sys


class Document(HTMLParser):
    def __init__(self, path):
        super().__init__(convert_charrefs=True)
        self.path = path
        self.ids = set()
        self.links = []
        self.buttons = []
        self.scripts = []
        self.forms = []
        self.feed(path.read_text())

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if "id" in attrs:
            assert attrs["id"] not in self.ids, f"Duplicate id in {self.path}: {attrs['id']}"
            self.ids.add(attrs["id"])
        if tag in ("a", "link", "script"):
            target = attrs.get("href", attrs.get("src"))
            if target:
                self.links.append(target)
        if tag == "button":
            self.buttons.append(attrs)
        if tag == "script":
            self.scripts.append(attrs)
        if tag == "form":
            self.forms.append(attrs)


def main():
    root = Path(sys.argv[1] if len(sys.argv) > 1 else "dist").resolve()
    pages = {path: Document(path) for path in root.rglob("*.html")}
    expected = {"index.html", "features/index.html", "islands/index.html", "about/index.html"}
    assert {str(path.relative_to(root)) for path in pages} == expected
    for path, doc in pages.items():
        source = path.read_text()
        assert "&amp;lt;" not in source, f"Double-escaped code in {path}"
        assert not doc.forms, f"Static page must not offer a server POST: {path}"
        assert not doc.scripts, f"Static showcase requires no runtime script: {path}"
        for href in doc.links:
            url = urlsplit(href)
            if url.scheme or url.netloc:
                continue
            assert not url.path.startswith("/"), f"Project Pages prefix escaped: {href}"
            target = (path.parent / unquote(url.path)).resolve() if url.path else path
            assert target.is_relative_to(root), f"Link leaves export: {href}"
            if target.is_dir():
                target /= "index.html"
            assert target.is_file(), f"Missing file from {path}: {href} -> {target}"
            if url.fragment and target in pages:
                assert unquote(url.fragment) in pages[target].ids, f"Missing anchor: {href}"
    counter = pages[root / "islands/index.html"]
    assert len(counter.buttons) == 1 and "disabled" in counter.buttons[0]
    assert not (root / "signup/index.html").exists(), "SSR signup must not be exported"
    assert (root / "sitemap.xml").is_file() and (root / "robots.txt").is_file()
    print(f"PASS: {len(pages)} pages, local links/anchors, safe static Counter, no SSR form or scripts")


if __name__ == "__main__":
    main()
