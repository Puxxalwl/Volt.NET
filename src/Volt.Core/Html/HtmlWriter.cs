using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;

namespace Volt;

/// <summary>
/// Zero-allocation streaming HTML writer. All output is UTF-8 written directly into an
/// <see cref="IBufferWriter{T}"/> (a pooled buffer or the HTTP response pipe).
/// Instances are pooled — Rent/Return per render (amortized zero allocation).
///
/// Markup is authored with C# blocks and element scopes:
/// <code>
/// w.DocType();
/// using (w.Html("en"))
/// using (w.Head())
/// {
///     w.MetaCharset();
///     w.Title("Home");
/// }
/// using (w.Body())
/// using (w.Div())
/// {
///     w.Class("card");
///     w.H1("Hello");
/// }
/// </code>
/// Attributes must be written right after opening a scope (before any content).
/// </summary>
public sealed class HtmlWriter
{
    private const int MaxPooled = 64;

    private static readonly ConcurrentQueue<HtmlWriter> Pool = new();

    private static readonly System.Buffers.SearchValues<char> TextEscapeChars
        = System.Buffers.SearchValues.Create("&<>");

    private static readonly System.Buffers.SearchValues<byte> TextEscapeBytes
        = System.Buffers.SearchValues.Create("&<>"u8);

    private static readonly System.Buffers.SearchValues<char> AttrEscapeChars
        = System.Buffers.SearchValues.Create("&<>\"'");

    private static readonly System.Buffers.SearchValues<byte> AttrEscapeBytes
        = System.Buffers.SearchValues.Create("&<>\"'"u8);

    private IBufferWriter<byte>? _out;
    private bool _openTag; // '<tag' written, awaiting attributes and '>'
    private bool _hydrationEmitted;
    private bool _allowHydrationScript = true;

    private HtmlWriter() { }

    /// <summary>Rents a writer bound to an output buffer.</summary>
    /// <param name="includeHydrationScript">False for island fragments (the document already booted).</param>
    public static HtmlWriter Rent(IBufferWriter<byte> output, bool includeHydrationScript = true)
    {
        var w = Pool.TryDequeue(out var pooled) ? pooled : new HtmlWriter();
        w._out = output;
        w._openTag = false;
        w._hydrationEmitted = false;
        w._allowHydrationScript = includeHydrationScript;
        return w;
    }

    /// <summary>Releases the writer to the pool. The output buffer must no longer be used via this writer.</summary>
    public void Return()
    {
        _out = null;
        _openTag = false;
        _hydrationEmitted = false;
        if (Pool.Count <= MaxPooled) Pool.Enqueue(this);
    }

    /// <summary>True after the hydration boot script has been emitted for this document.</summary>
    public bool HydrationEmitted => _hydrationEmitted;

    // ------------------------------------------------------------------
    // Raw writing primitives
    // ------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void W(ReadOnlySpan<byte> utf8)
    {
        var out_ = _out ?? ThrowNotBound();
        if (utf8.IsEmpty) return;
        out_.Write(utf8);
    }

    private static IBufferWriter<byte> ThrowNotBound()
        => throw new InvalidOperationException("Volt: HtmlWriter is not bound to an output buffer.");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CloseOpenTag()
    {
        if (!_openTag) return;
        _openTag = false;
        W(">"u8);
    }

    // ------------------------------------------------------------------
    // Document structure
    // ------------------------------------------------------------------

    public void DocType() => W("<!DOCTYPE html>\n"u8);

    public void NewLine()
    {
        CloseOpenTag();
        W("\n"u8);
    }

    /// <summary>Writes &lt;html lang="..."&gt; and returns a scope closing &lt;/html&gt;.</summary>
    public ElementScope Html(ReadOnlySpan<char> lang = default)
    {
        CloseOpenTag();
        W("<html"u8);
        if (!lang.IsEmpty)
        {
            W(" lang=\""u8);
            WriteEscapedAttrValue(lang);
            W("\""u8);
        }
        W(">"u8);
        return new ElementScope(this, "html"u8, isVoid: false);
    }

    public ElementScope Head() => ClosedEl("head"u8);

    public ElementScope Body() => ClosedEl("body"u8);

    // ------------------------------------------------------------------
    // Elements (open-pending style: attributes then content, closed by scope)
    // ------------------------------------------------------------------

    /// <summary>Opens a custom element by name. The scope writes the closing tag.</summary>
    public ElementScope El(ReadOnlySpan<char> name)
    {
        CloseOpenTag();
        W("<"u8);
        WriteUtf8(name);
        _openTag = true;
        return new ElementScope(this, name, isVoid: false);
    }

    /// <summary>Opens a custom void element by name; the scope writes '&gt;'.</summary>
    public ElementScope VoidEl(ReadOnlySpan<char> name)
    {
        CloseOpenTag();
        W("<"u8);
        WriteUtf8(name);
        _openTag = true;
        return new ElementScope(this, name, isVoid: true);
    }

    public ElementScope Div() => OpenEl("div"u8);
    public ElementScope Span() => OpenEl("span"u8);
    public ElementScope P() => OpenEl("p"u8);
    public ElementScope A() => OpenEl("a"u8);
    public ElementScope H1() => OpenEl("h1"u8);
    public ElementScope H2() => OpenEl("h2"u8);
    public ElementScope H3() => OpenEl("h3"u8);
    public ElementScope H4() => OpenEl("h4"u8);
    public ElementScope H5() => OpenEl("h5"u8);
    public ElementScope H6() => OpenEl("h6"u8);
    public ElementScope Ul() => OpenEl("ul"u8);
    public ElementScope Ol() => OpenEl("ol"u8);
    public ElementScope Li() => OpenEl("li"u8);
    public ElementScope Button() => OpenEl("button"u8);
    public ElementScope Form() => OpenEl("form"u8);
    public ElementScope Header() => OpenEl("header"u8);
    public ElementScope Footer() => OpenEl("footer"u8);
    public ElementScope Main() => OpenEl("main"u8);
    public ElementScope Nav() => OpenEl("nav"u8);
    public ElementScope Section() => OpenEl("section"u8);
    public ElementScope Article() => OpenEl("article"u8);
    public ElementScope Aside() => OpenEl("aside"u8);
    public ElementScope Label() => OpenEl("label"u8);
    public ElementScope Strong() => OpenEl("strong"u8);
    public ElementScope Em() => OpenEl("em"u8);
    public ElementScope Small() => OpenEl("small"u8);
    public ElementScope Code() => OpenEl("code"u8);
    public ElementScope Pre() => OpenEl("pre"u8);
    public ElementScope Blockquote() => OpenEl("blockquote"u8);
    public ElementScope Table() => OpenEl("table"u8);
    public ElementScope Thead() => OpenEl("thead"u8);
    public ElementScope Tbody() => OpenEl("tbody"u8);
    public ElementScope Tfoot() => OpenEl("tfoot"u8);
    public ElementScope Tr() => OpenEl("tr"u8);
    public ElementScope Td() => OpenEl("td"u8);
    public ElementScope Th() => OpenEl("th"u8);
    public ElementScope Figure() => OpenEl("figure"u8);
    public ElementScope Figcaption() => OpenEl("figcaption"u8);
    public ElementScope Details() => OpenEl("details"u8);
    public ElementScope Summary() => OpenEl("summary"u8);
    public ElementScope Script() => OpenEl("script"u8);
    public ElementScope Style() => OpenEl("style"u8);

    /// <summary>Opens an input (void element). The scope writes '&gt;'.</summary>
    public ElementScope Input() => OpenEl("input"u8, isVoid: true);

    public void Br()
    {
        CloseOpenTag();
        W("<br>"u8);
    }

    public void Hr()
    {
        CloseOpenTag();
        W("<hr>"u8);
    }

    /// <summary>&lt;h1&gt;text&lt;/h1&gt; convenience.</summary>
    public void H1Text(ReadOnlySpan<char> text) => ElText("h1"u8, text);

    public void H2Text(ReadOnlySpan<char> text) => ElText("h2"u8, text);

    public void H3Text(ReadOnlySpan<char> text) => ElText("h3"u8, text);

    /// <summary>&lt;p&gt;text&lt;/p&gt; convenience.</summary>
    public void PText(ReadOnlySpan<char> text) => ElText("p"u8, text);

    private void ElText(ReadOnlySpan<byte> name, ReadOnlySpan<char> text)
    {
        CloseOpenTag();
        W("<"u8);
        W(name);
        W(">"u8);
        WriteEscapedText(text);
        W("</"u8);
        W(name);
        W(">"u8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ElementScope OpenEl(ReadOnlySpan<byte> name, bool isVoid = false)
    {
        CloseOpenTag();
        W("<"u8);
        W(name);
        _openTag = true;
        return new ElementScope(this, name, isVoid);
    }

    private ElementScope ClosedEl(ReadOnlySpan<byte> name)
    {
        CloseOpenTag();
        W("<"u8);
        W(name);
        W(">"u8);
        return new ElementScope(this, name, isVoid: false);
    }

    // ------------------------------------------------------------------
    // Complete elements with fixed attributes (head helpers)
    // ------------------------------------------------------------------

    public void MetaCharset() => W("<meta charset=\"utf-8\">"u8);

    public void Meta(ReadOnlySpan<char> name, ReadOnlySpan<char> content)
    {
        CloseOpenTag();
        W("<meta name=\""u8);
        WriteEscapedAttrValue(name);
        W("\" content=\""u8);
        WriteEscapedAttrValue(content);
        W("\">"u8);
    }

    public void MetaProperty(ReadOnlySpan<char> property, ReadOnlySpan<char> content)
    {
        CloseOpenTag();
        W("<meta property=\""u8);
        WriteEscapedAttrValue(property);
        W("\" content=\""u8);
        WriteEscapedAttrValue(content);
        W("\">"u8);
    }

    public void Link(ReadOnlySpan<char> rel, ReadOnlySpan<char> href)
    {
        CloseOpenTag();
        W("<link rel=\""u8);
        WriteEscapedAttrValue(rel);
        W("\" href=\""u8);
        WriteEscapedAttrValue(href);
        W("\">"u8);
    }

    /// <summary>&lt;title&gt; element with escaped text content.</summary>
    public void Title(ReadOnlySpan<char> text)
    {
        CloseOpenTag();
        W("<title>"u8);
        WriteEscapedText(text);
        W("</title>"u8);
    }

    /// <summary>&lt;script src=... defer&gt;&lt;/script&gt;.</summary>
    public void ScriptSrc(ReadOnlySpan<char> src, bool defer = true)
    {
        CloseOpenTag();
        W("<script src=\""u8);
        WriteEscapedAttrValue(src);
        W("\""u8);
        if (defer) W(" defer"u8);
        W("></script>"u8);
    }

    /// <summary>&lt;style&gt; with raw (trusted) CSS content. Never pass user input here.</summary>
    public void StyleRaw(ReadOnlySpan<char> css)
    {
        CloseOpenTag();
        W("<style>"u8);
        WriteUtf8(css);
        W("</style>"u8);
    }

    internal void HiddenInput(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        CloseOpenTag();
        W("<input type=\"hidden\" name=\""u8);
        W(name);
        W("\" value=\""u8);
        WriteEscapedAttrUtf8(value);
        W("\">"u8);
    }

    internal void HiddenInput(ReadOnlySpan<char> name, ReadOnlySpan<byte> value)
    {
        CloseOpenTag();
        W("<input type=\"hidden\" name=\""u8);
        WriteUtf8(name);
        W("\" value=\""u8);
        WriteEscapedAttrUtf8(value);
        W("\">"u8);
    }

    internal void HiddenInput(ReadOnlySpan<char> name, ReadOnlySpan<char> value)
    {
        CloseOpenTag();
        W("<input type=\"hidden\" name=\""u8);
        WriteUtf8(name);
        W("\" value=\""u8);
        WriteEscapedAttrValue(value);
        W("\">"u8);
    }

    /// <summary>Bare (valueless) attribute, e.g. data-v-form.</summary>
    public void BooleanAttr(ReadOnlySpan<byte> name)
    {
        ThrowIfNoOpenTag();
        W(" "u8);
        W(name);
    }

    public void DataVoltForm() => BooleanAttr("data-v-form"u8);

    /// <summary>Emits the hydration boot script once per document (skipped in fragments).</summary>
    public void EnsureHydrationScript()
    {
        if (!_allowHydrationScript || _hydrationEmitted) return;
        _hydrationEmitted = true;
        ScriptSrc(VoltRuntime.HydrateScriptSrc);
    }

    // ------------------------------------------------------------------
    // Content
    // ------------------------------------------------------------------

    /// <summary>Escaped text content (&amp; &lt; &gt; are entity-encoded).</summary>
    public void Text(ReadOnlySpan<char> text)
    {
        CloseOpenTag();
        WriteEscapedText(text);
    }

    /// <summary>Raw (trusted) HTML fragment — no escaping. Never pass user input.</summary>
    public void Raw(ReadOnlySpan<char> html)
    {
        CloseOpenTag();
        WriteUtf8(html);
    }

    /// <summary>Raw (trusted) pre-encoded UTF-8 HTML — no escaping, no conversion.</summary>
    public void RawUtf8(ReadOnlySpan<byte> utf8)
    {
        CloseOpenTag();
        W(utf8);
    }

    /// <summary>Escaped text content from pre-encoded UTF-8 bytes.</summary>
    public void TextUtf8(ReadOnlySpan<byte> utf8)
    {
        CloseOpenTag();
        WriteEscapedTextUtf8(utf8);
    }

    // ------------------------------------------------------------------
    // Attributes (must be called while the opening tag is pending)
    // ------------------------------------------------------------------

    public void Attr(ReadOnlySpan<byte> name, ReadOnlySpan<char> value)
    {
        ThrowIfNoOpenTag();
        W(" "u8);
        W(name);
        W("=\""u8);
        WriteEscapedAttrValue(value);
        W("\""u8);
    }

    public void Attr(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        ThrowIfNoOpenTag();
        W(" "u8);
        W(name);
        W("=\""u8);
        WriteEscapedAttrUtf8(value);
        W("\""u8);
    }

    public void Attr(ReadOnlySpan<char> name, ReadOnlySpan<char> value)
    {
        ThrowIfNoOpenTag();
        W(" "u8);
        WriteUtf8(name);
        W("=\""u8);
        WriteEscapedAttrValue(value);
        W("\""u8);
    }

    public void Class(ReadOnlySpan<char> value) => Attr("class"u8, value);
    public void Id(ReadOnlySpan<char> value) => Attr("id"u8, value);
    public void Href(ReadOnlySpan<char> value) => Attr("href"u8, value);
    public void Src(ReadOnlySpan<char> value) => Attr("src"u8, value);
    public void Type(ReadOnlySpan<char> value) => Attr("type"u8, value);
    public void Name(ReadOnlySpan<char> value) => Attr("name"u8, value);
    public void Value(ReadOnlySpan<char> value) => Attr("value"u8, value);
    public void Placeholder(ReadOnlySpan<char> value) => Attr("placeholder"u8, value);
    public void Action(ReadOnlySpan<char> value) => Attr("action"u8, value);
    public void Method(ReadOnlySpan<char> value) => Attr("method"u8, value);
    public void Rel(ReadOnlySpan<char> value) => Attr("rel"u8, value);
    public void Target(ReadOnlySpan<char> value) => Attr("target"u8, value);
    public void TitleAttr(ReadOnlySpan<char> value) => Attr("title"u8, value);
    public void Width(ReadOnlySpan<char> value) => Attr("width"u8, value);
    public void Height(ReadOnlySpan<char> value) => Attr("height"u8, value);
    public void Alt(ReadOnlySpan<char> value) => Attr("alt"u8, value);

    /// <summary>data-v-on="event:action" — hydration event binding (see CONTRACT.md).</summary>
    public void DataVoltOn(ReadOnlySpan<char> value) => Attr("data-v-on"u8, value);

    /// <summary>data-v-key — stable morphing key for the client router.</summary>
    public void DataVoltKey(ReadOnlySpan<char> value) => Attr("data-v-key"u8, value);

    public void DataVoltExternal() => Attr("data-v-external"u8, "true"u8);

    // ------------------------------------------------------------------
    // Escaping and UTF-8 conversion
    // ------------------------------------------------------------------

    private void WriteEscapedText(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty) return;
        if (s.IndexOfAny(TextEscapeChars) < 0)
        {
            WriteUtf8(s);
            return;
        }
        Span<char> buf = stackalloc char[256];
        int bi = 0;
        for (int i = 0; i < s.Length; i++)
        {
            ReadOnlySpan<char> entity = s[i] switch
            {
                '<' => "&lt;",
                '>' => "&gt;",
                '&' => "&amp;",
                _ => default,
            };
            if (entity.Length == 0)
            {
                if (bi >= buf.Length - 6) { WriteUtf8(buf[..bi]); bi = 0; }
                buf[bi++] = s[i];
            }
            else
            {
                if (bi + entity.Length > buf.Length) { WriteUtf8(buf[..bi]); bi = 0; }
                entity.CopyTo(buf[bi..]);
                bi += entity.Length;
            }
        }
        if (bi > 0) WriteUtf8(buf[..bi]);
    }

    private void WriteEscapedTextUtf8(ReadOnlySpan<byte> s)
    {
        if (s.IsEmpty) return;
        if (s.IndexOfAny(TextEscapeBytes) < 0)
        {
            W(s);
            return;
        }
        Span<byte> buf = stackalloc byte[256];
        int bi = 0;
        for (int i = 0; i < s.Length; i++)
        {
            ReadOnlySpan<byte> entity = s[i] switch
            {
                (byte)'<' => "&lt;"u8,
                (byte)'>' => "&gt;"u8,
                (byte)'&' => "&amp;"u8,
                _ => default,
            };
            if (entity.Length == 0)
            {
                if (bi >= buf.Length - 6) { W(buf[..bi]); bi = 0; }
                buf[bi++] = s[i];
            }
            else
            {
                if (bi + entity.Length > buf.Length) { W(buf[..bi]); bi = 0; }
                entity.CopyTo(buf[bi..]);
                bi += entity.Length;
            }
        }
        if (bi > 0) W(buf[..bi]);
    }

    private void WriteEscapedAttrValue(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty) return;
        if (s.IndexOfAny(AttrEscapeChars) < 0)
        {
            WriteUtf8(s);
            return;
        }
        Span<char> buf = stackalloc char[256];
        int bi = 0;
        for (int i = 0; i < s.Length; i++)
        {
            ReadOnlySpan<char> entity = s[i] switch
            {
                '<' => "&lt;",
                '>' => "&gt;",
                '&' => "&amp;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ => default,
            };
            if (entity.Length == 0)
            {
                if (bi >= buf.Length - 6) { WriteUtf8(buf[..bi]); bi = 0; }
                buf[bi++] = s[i];
            }
            else
            {
                if (bi + entity.Length > buf.Length) { WriteUtf8(buf[..bi]); bi = 0; }
                entity.CopyTo(buf[bi..]);
                bi += entity.Length;
            }
        }
        if (bi > 0) WriteUtf8(buf[..bi]);
    }

    private void WriteEscapedAttrUtf8(ReadOnlySpan<byte> s)
    {
        if (s.IsEmpty) return;
        if (s.IndexOfAny(AttrEscapeBytes) < 0)
        {
            W(s);
            return;
        }
        Span<byte> buf = stackalloc byte[256];
        int bi = 0;
        for (int i = 0; i < s.Length; i++)
        {
            ReadOnlySpan<byte> entity = s[i] switch
            {
                (byte)'<' => "&lt;"u8,
                (byte)'>' => "&gt;"u8,
                (byte)'&' => "&amp;"u8,
                (byte)'"' => "&quot;"u8,
                (byte)'\'' => "&#39;"u8,
                _ => default,
            };
            if (entity.Length == 0)
            {
                if (bi >= buf.Length - 6) { W(buf[..bi]); bi = 0; }
                buf[bi++] = s[i];
            }
            else
            {
                if (bi + entity.Length > buf.Length) { W(buf[..bi]); bi = 0; }
                entity.CopyTo(buf[bi..]);
                bi += entity.Length;
            }
        }
        if (bi > 0) W(buf[..bi]);
    }

    private void WriteUtf8(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty) return;
        int max = Encoding.UTF8.GetMaxByteCount(s.Length);
        if (max <= 1024)
        {
            Span<byte> tmp = stackalloc byte[1024];
            int n = Encoding.UTF8.GetBytes(s, tmp);
            W(tmp[..n]);
            return;
        }
        byte[] rent = System.Buffers.ArrayPool<byte>.Shared.Rent(max);
        try
        {
            int n = Encoding.UTF8.GetBytes(s, rent);
            W(rent.AsSpan(0, n));
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rent);
        }
    }

    internal void CloseElement(ReadOnlySpan<byte> name, bool isVoid)
    {
        if (isVoid)
        {
            if (_openTag)
            {
                _openTag = false;
                W(">"u8);
            }
            return;
        }
        if (_openTag)
        {
            _openTag = false;
            W("></"u8);
            W(name);
            W(">"u8);
            return;
        }
        W("</"u8);
        W(name);
        W(">"u8);
    }

    internal void CloseElement(ReadOnlySpan<char> name, bool isVoid)
    {
        if (isVoid)
        {
            if (_openTag)
            {
                _openTag = false;
                W(">"u8);
            }
            return;
        }
        if (_openTag)
        {
            _openTag = false;
            W(">"u8);
            W("</"u8);
            WriteUtf8(name);
            W(">"u8);
            return;
        }
        W("</"u8);
        WriteUtf8(name);
        W(">"u8);
    }

    private void ThrowIfNoOpenTag()
    {
        if (!_openTag)
            throw new InvalidOperationException(
                "Volt: attributes must be written immediately after opening an element, before any content.");
    }
}

/// <summary>
/// Stack-only scope over an open element. <see cref="Dispose"/> writes the closing tag
/// (or '&gt;' for void elements). Must not outlive its <see cref="HtmlWriter"/>.
/// </summary>
public ref struct ElementScope : IDisposable
{
    private readonly HtmlWriter _w;
    private readonly ReadOnlySpan<byte> _name;
    private readonly ReadOnlySpan<char> _charName;
    private readonly bool _void;
    private readonly bool _isChar;

    internal ElementScope(HtmlWriter w, ReadOnlySpan<byte> name, bool isVoid)
    {
        _w = w;
        _name = name;
        _charName = default;
        _void = isVoid;
        _isChar = false;
    }

    internal ElementScope(HtmlWriter w, ReadOnlySpan<char> name, bool isVoid)
    {
        _w = w;
        _name = default;
        _charName = name;
        _void = isVoid;
        _isChar = true;
    }

    public void Dispose()
    {
        if (_isChar) _w.CloseElement(_charName, _void);
        else _w.CloseElement(_name, _void);
    }
}
