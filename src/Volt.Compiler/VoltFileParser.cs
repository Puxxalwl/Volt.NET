using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Volt.Compiler;

/// <summary>
/// Parses .volt template files (Razor-like markup + C#) into a page model the
/// generator compiles into a VoltPage subclass. Compile-time only — allocations are fine.
/// </summary>
internal static class VoltFileParser
{
    private static readonly string[] VoidTags =
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"
    };

    private static readonly string[] CodeKeywords = { "if", "for", "foreach", "while", "using", "switch", "do", "else", "try", "catch", "finally" };

    /// <summary>Signature of a declared partial (from the first @partial pass).</summary>
    public readonly record struct PartialSignature(string Name, string ParamType, string ParamName)
    {
        public bool HasParam => ParamType.Length > 0;
    }

    public sealed class VoltFileModel
    {
        public string FilePath = "";
        public string Route = "";
        public string ClassName = "";
        public string Namespace = "Volt.Generated";
        public string Mode = "SSR";
        public int Revalidate = -1;
        /// <summary>M6: @tag values (comma-separated) — on-demand revalidation groups.</summary>
        public List<string> TagList = new();
        public bool IsNotFound;
        public bool IsError;
        public string RenderBody = "";
        public string Diagnostic = "";
        // layout composition (spliced at compile time — no runtime base class)
        public bool IsLayout;                 // _Layout.volt / _AdminLayout.volt
        public string LayoutOverride = "";    // "" (convention) | "none" | "Admin"
        public string BodyBefore = "";        // layout: code before @renderbody
        public string BodyAfter = "";         // layout: code after @renderbody
        // partials
        public bool IsPartial;                // has @partial declaration
        public string PartialName = "";
        public string PartialParamType = "";
        public string PartialParamName = "";
    }

    public static VoltFileModel Parse(string filePath, string text,
        IReadOnlyDictionary<string, PartialSignature> partials, bool minify = false)
    {
        var model = new VoltFileModel { FilePath = filePath, ClassName = SafeName(filePath) };
        model.IsLayout = IsLayoutFile(filePath);

        try
        {
            var body = ParseDirectives(text, model);
            model.IsPartial = model.PartialName.Length > 0;
            if (model.IsLayout)
            {
                if (model.PartialName.Length > 0)
                    throw new FormatException("layout files cannot declare @partial");
                if (model.Route.Length > 0)
                    throw new FormatException("layout files cannot declare @page");
            }
            if (model.IsPartial && model.Route.Length > 0)
                throw new FormatException("partials cannot declare @page");

            // '_'-prefixed files are never routes: they must be layouts or declare @partial
            var stem = System.IO.Path.GetFileNameWithoutExtension(filePath.Replace('\\', '/'));
            if (stem.StartsWith("_", StringComparison.Ordinal) && !model.IsLayout && !model.IsPartial)
                throw new FormatException(
                    "files starting with '_' are non-routable: use _Layout.volt/_NameLayout.volt or declare @partial");

            var parser = new BodyParser(body, model.IsLayout, model.IsPartial, partials, minify);
            var code = parser.Parse();
            if (model.IsLayout)
            {
                if (parser.RenderBodyMark < 0)
                    throw new FormatException("layout must contain @renderbody");
                model.BodyBefore = code.Substring(0, parser.RenderBodyMark);
                model.BodyAfter = code.Substring(parser.RenderBodyMark);
            }
            else
            {
                model.RenderBody = code;
                if (parser.RenderBodyMark >= 0)
                    throw new FormatException("@renderbody is only allowed in layout files");
            }
            if (model.Route.Length == 0 && !model.IsNotFound && !model.IsError && !model.IsLayout && !model.IsPartial)
                model.Route = RouteFromVoltPath(filePath);
        }
        catch (Exception ex)
        {
            model.Diagnostic = $"VOLT020: .volt parse error: {ex.Message}";
        }
        return model;
    }

    /// <summary>_Layout.volt / _AdminLayout.volt — never routes.</summary>
    internal static bool IsLayoutFile(string filePath)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(filePath.Replace('\\', '/'));
        return stem.StartsWith("_", StringComparison.Ordinal)
               && (stem.Length == 7 && stem.Equals("_layout", StringComparison.OrdinalIgnoreCase)
                   || (stem.Length > 7 && stem.EndsWith("layout", StringComparison.OrdinalIgnoreCase)));
    }

    // ------------------------------------------------------------------
    // directives
    // ------------------------------------------------------------------

    private static string ParseDirectives(string text, VoltFileModel model)
    {
        int i = 0;
        while (true)
        {
            int lineEnd = text.IndexOf('\n', i);
            var line = (lineEnd < 0 ? text.Substring(i) : text.Substring(i, lineEnd - i)).Trim();

            if (line.StartsWith("@page ", StringComparison.Ordinal))
            {
                model.Route = line.Substring("@page ".Length).Trim().Trim('"', '\'');
            }
            else if (line.StartsWith("@mode ", StringComparison.Ordinal))
            {
                var mode = line.Substring("@mode ".Length).Trim().ToUpperInvariant();
                model.Mode = mode is "SSG" or "SSR" ? mode : "SSR";
            }
            else if (line.StartsWith("@revalidate ", StringComparison.Ordinal))
            {
                if (int.TryParse(line.Substring("@revalidate ".Length).Trim(), out var seconds) && seconds >= 0)
                    model.Revalidate = seconds;
            }
            else if (line.StartsWith("@tag ", StringComparison.Ordinal))
            {
                foreach (var rawTag in line.Substring("@tag ".Length).Split(','))
                {
                    var tag = rawTag.Trim();
                    if (tag.Length > 0 && !model.TagList.Contains(tag))
                        model.TagList.Add(tag);
                }
            }
            else if (line.StartsWith("@namespace ", StringComparison.Ordinal))
            {
                model.Namespace = line.Substring("@namespace ".Length).Trim();
            }
            else if (line.StartsWith("@layout ", StringComparison.Ordinal))
            {
                model.LayoutOverride = line.Substring("@layout ".Length).Trim().Trim('"', '\'');
                if (model.LayoutOverride.Length == 0)
                    throw new FormatException("@layout requires a layout name or 'none'");
            }
            else if (line.StartsWith("@partial ", StringComparison.Ordinal))
            {
                // @partial Card
                // @partial Card(Product item)
                var decl = line.Substring("@partial ".Length).Trim();
                int open = decl.IndexOf('(');
                if (open < 0 || !decl.EndsWith(")"))
                {
                    model.PartialName = decl.Trim();
                    if (model.PartialName.Length == 0)
                        throw new FormatException("@partial requires a name");
                    model.PartialParamType = "";
                    model.PartialParamName = "";
                }
                else
                {
                    model.PartialName = decl.Substring(0, open).Trim();
                    var param = decl.Substring(open + 1, decl.Length - open - 2).Trim();
                    int space = param.LastIndexOf(' ');
                    if (space < 0)
                        throw new FormatException("@partial parameter must be 'Type name' (e.g. @partial Card(Product item))");
                    model.PartialParamType = param.Substring(0, space).Trim();
                    model.PartialParamName = param.Substring(space + 1).Trim();
                    if (model.PartialName.Length == 0 || model.PartialParamType.Length == 0 || model.PartialParamName.Length == 0)
                        throw new FormatException("@partial declaration is malformed");
                }
            }
            else if (line.Length == 0)
            {
                // blank line between directives and body: tolerated
            }
            else
            {
                if (line.Equals("notfound", StringComparison.OrdinalIgnoreCase) ||
                    (line.StartsWith("@page", StringComparison.Ordinal) && model.Route.Equals("/notfound", StringComparison.OrdinalIgnoreCase)))
                {
                    // handled via file name convention below
                }
                break; // first content line: body starts here
            }

            if (lineEnd < 0) return "";
            i = lineEnd + 1;
        }

        // special pages by file name (Pages/notfound.volt, Pages/error.volt)
        var name = System.IO.Path.GetFileNameWithoutExtension(model.FilePath);
        if (name.Equals("notfound", StringComparison.OrdinalIgnoreCase)) { model.IsNotFound = true; model.Route = ""; }
        else if (name.Equals("error", StringComparison.OrdinalIgnoreCase)) { model.IsError = true; model.Route = ""; }

        return text.Substring(i);
    }

    // ------------------------------------------------------------------
    // body parser: state machine over markup + code
    // ------------------------------------------------------------------

    private sealed class BodyParser
    {
        private readonly string _t;
        private readonly StringBuilder _sb = new(4096);
        private int _i;
        private readonly bool _isLayout;
        private readonly bool _isPartial;
        private readonly IReadOnlyDictionary<string, PartialSignature> _partials;

        /// <summary>For layouts: code length in front of @renderbody; -1 when absent.</summary>
        public int RenderBodyMark = -1;

        public BodyParser(string text, bool isLayout, bool isPartial, IReadOnlyDictionary<string, PartialSignature> partials, bool minify = false)
        {
            _t = text;
            _isLayout = isLayout;
            _isPartial = isPartial;
            _partials = partials;
            _minify = minify;
        }

        /// <summary>true → collapse whitespace runs in static text literals (compile-time minification).</summary>
        private readonly bool _minify;

        /// <summary>Open pre/script/style/textarea elements — their content is copied verbatim.</summary>
        private readonly List<string> _preserve = new();

        /// <summary>Top-level: markup with @-transitions.</summary>
        public string Parse()
        {
            while (_i < _t.Length)
            {
                char c = _t[_i];
                if (c == '<')
                {
                    ParseMarkupNode();
                }
                else if (c == '@')
                {
                    ParseTransition();
                }
                else if (char.IsWhiteSpace(c))
                {
                    SkipInterNodeWhitespace(topLevel: true);
                }
                else
                {
                    ParseTextRun();
                }
            }
            return _sb.ToString();
        }

        /// <summary>
        /// Content of a code block (from '@if (…) {'): C# lines by default; a line
        /// starting with '&lt;' switches to markup until the region's tag depth returns to
        /// zero, then C# lines resume. '}' at brace depth 1 closes the block.
        /// Returns the index just after the closing '}'.
        /// </summary>
        public int ParseCodeBlock(int start)
        {
            _i = start;
            int depth = 1;
            bool inMarkupRegion = false;
            int tagDepth = 0;

            while (_i < _t.Length)
            {
                if (inMarkupRegion)
                {
                    char c = _t[_i];
                    if (c == '<')
                    {
                        tagDepth += ParseMarkupNode();
                        continue;
                    }
                    if (c == '@')
                    {
                        ParseTransition();
                        continue;
                    }
                    if (c == '}')
                    {
                        if (tagDepth == 0)
                        {
                            depth--;
                            if (depth == 0)
                            {
                                _i++;
                                return _i;
                            }
                            _sb.AppendLine("            }");
                            _i++;
                            continue;
                        }
                        // '}' inside an open tag region: treat as text (rare/malformed)
                        ParseTextRun();
                        continue;
                    }
                    if (char.IsWhiteSpace(c))
                    {
                        SkipInterNodeWhitespace(topLevel: false);
                        continue;
                    }
                    ParseTextRun();
                    continue;
                }

                // ---- C# line mode -------------------------------------------------
                int lineEnd = _t.IndexOf('\n', _i);
                if (lineEnd < 0) lineEnd = _t.Length;
                var raw = _t.Substring(_i, lineEnd - _i).TrimEnd('\r');
                var trimmed = raw.Trim();

                if (trimmed.Length == 0)
                {
                    _i = lineEnd < _t.Length ? lineEnd + 1 : _t.Length;
                    continue;
                }

                if (trimmed[0] == '<')
                {
                    inMarkupRegion = true;
                    _i += raw.Length - raw.TrimStart().Length; // keep line indentation position
                    continue;
                }

                // brace bookkeeping
                int localDepth = depth;
                foreach (var ch in trimmed)
                {
                    if (ch == '{') localDepth++;
                    else if (ch == '}') localDepth--;
                }

                if (trimmed[0] == '}' && localDepth == depth - 1)
                {
                    // the line only closed this block
                    depth = localDepth;
                    if (depth == 0)
                    {
                        _i = lineEnd < _t.Length ? lineEnd + 1 : _t.Length;
                        return _i;
                    }
                }

                _sb.AppendLine("            " + trimmed);
                _i = lineEnd < _t.Length ? lineEnd + 1 : _t.Length;
            }
            throw new FormatException("code block is missing its closing '}'");
        }

        // ------------------------------------------------------------------
        // markup nodes
        // ------------------------------------------------------------------

        /// <summary>Parses one markup node at '&lt;'. Returns +1 (opened element), -1 (closed), 0 (void/other).</summary>
        private int ParseMarkupNode()
        {
            // comment
            if (_t.IndexOf("<!--", _i, StringComparison.Ordinal) == _i)
            {
                int end = _t.IndexOf("-->", _i + 4, StringComparison.Ordinal);
                _i = end < 0 ? _t.Length : end + 3;
                return 0;
            }

            // <!doctype> / other declarations
            if (_i + 1 < _t.Length && _t[_i + 1] == '!')
            {
                int end = _t.IndexOf('>', _i);
                var inner = _t.Substring(_i + 2, end - _i - 2).Trim();
                if (inner.StartsWith("DOCTYPE", StringComparison.OrdinalIgnoreCase))
                    _sb.AppendLine("            w.DocType();");
                _i = end + 1;
                return 0;
            }

            // closing tag
            if (_i + 2 < _t.Length && _t[_i + 1] == '/')
            {
                int end = _t.IndexOf('>', _i);
                var name = _t.Substring(_i + 2, end - _i - 2).Trim();
                _i = end + 1;
                if (name == "volt-island")
                    return 0; // self-contained node: the close tag emits nothing
                PreserveClosed(name.ToLowerInvariant());
                _sb.AppendLine($"            }} // /{name}");
                return -1;
            }

            // stray '<'
            int nameEnd = _i + 1;
            while (nameEnd < _t.Length && (char.IsLetterOrDigit(_t[nameEnd]) || _t[nameEnd] is '-' or '_' or ':')) nameEnd++;
            var tagName = _t.Substring(_i + 1, nameEnd - _i - 1);
            if (tagName.Length == 0)
            {
                EmitText("<");
                _i++;
                return 0;
            }

            // <volt-island name="Counter" state='{"count":0}' [sid="s1"] /> — runtime island embedding
            if (tagName == "volt-island")
            {
                return ParseVoltIslandNode(nameEnd);
            }

            // attributes
            var attrs = new List<(string Name, string Value, bool IsExpr)>();
            bool selfClosed = false;
            int i = nameEnd;
            while (i < _t.Length)
            {
                while (i < _t.Length && char.IsWhiteSpace(_t[i])) i++;
                if (i >= _t.Length) break;
                if (_t[i] == '>')
                {
                    i++;
                    break;
                }
                if (_t[i] == '/' && i + 1 < _t.Length && _t[i + 1] == '>')
                {
                    selfClosed = true;
                    i += 2;
                    break;
                }

                int attrNameStart = i;
                while (i < _t.Length && _t[i] != '=' && _t[i] != '>' && !char.IsWhiteSpace(_t[i])) i++;
                var attrName = _t.Substring(attrNameStart, i - attrNameStart);

                string attrValue = "";
                bool isExpr = false;
                int j = i;
                while (j < _t.Length && char.IsWhiteSpace(_t[j])) j++;
                if (j < _t.Length && _t[j] == '=')
                {
                    j++;
                    while (j < _t.Length && char.IsWhiteSpace(_t[j])) j++;
                    if (j < _t.Length && (_t[j] == '"' || _t[j] == '\''))
                    {
                        char quote = _t[j];
                        int valueStart = j + 1;
                        int valueEnd = _t.IndexOf(quote, valueStart);
                        if (valueEnd < 0) throw new FormatException($"unterminated attribute value in <{tagName}>");
                        attrValue = _t.Substring(valueStart, valueEnd - valueStart);
                        i = valueEnd + 1;
                    }
                    else
                    {
                        int valueStart = j;
                        while (j < _t.Length && !char.IsWhiteSpace(_t[j]) && _t[j] != '>') j++;
                        attrValue = _t.Substring(valueStart, j - valueStart);
                        i = j;
                    }
                    isExpr = attrValue.IndexOf('@') >= 0;
                }
                if (attrName.Length > 0)
                    attrs.Add((attrName, attrValue, isExpr));
            }
            _i = i;

            bool isVoid = selfClosed || VoidTags.Contains(tagName.ToLowerInvariant());

            if (isVoid)
            {
                if (attrs.Count == 0)
                {
                    _sb.AppendLine($"            using (w.VoidEl(\"{tagName}\"));");
                }
                else
                {
                    _sb.AppendLine($"            using (w.VoidEl(\"{tagName}\"))");
                    _sb.AppendLine("            {");
                    foreach (var (name, value, isExprValue) in attrs)
                        EmitAttr(name, value, isExprValue);
                    _sb.AppendLine("            }");
                }
                return 0;
            }

            // normal element: the using scope wraps all nested content;
            // the matching </tag> emits the closing brace
            var lowerName = tagName.ToLowerInvariant();
            if (lowerName is "pre" or "script" or "style" or "textarea")
                _preserve.Add(lowerName); // M6: minification never touches this content
            _sb.AppendLine($"            using (w.El(\"{tagName}\"))");
            _sb.AppendLine("            {");
            foreach (var (name, value, isExprValue) in attrs)
                EmitAttr(name, value, isExprValue);
            return 1;
        }


        /// <summary>Parses a &lt;volt-island&gt; node: name + state (JSON) + optional sid.</summary>
        private int ParseVoltIslandNode(int afterName)
        {
            string name = "", state = "", sid = "";
            int i = afterName;
            while (i < _t.Length)
            {
                while (i < _t.Length && char.IsWhiteSpace(_t[i])) i++;
                if (i >= _t.Length) break;
                if (_t[i] == '>') { i++; break; }
                if (_t[i] == '/' && i + 1 < _t.Length && _t[i + 1] == '>') { i += 2; break; }

                int attrNameStart = i;
                while (i < _t.Length && _t[i] != '=' && _t[i] != '>' && !char.IsWhiteSpace(_t[i])) i++;
                var attrName = _t.Substring(attrNameStart, i - attrNameStart);

                string attrValue = "";
                int j = i;
                while (j < _t.Length && char.IsWhiteSpace(_t[j])) j++;
                if (j < _t.Length && _t[j] == '=')
                {
                    j++;
                    while (j < _t.Length && char.IsWhiteSpace(_t[j])) j++;
                    if (j < _t.Length && (_t[j] == '"' || _t[j] == '\''))
                    {
                        char quote = _t[j];
                        int valueStart = j + 1;
                        int valueEnd = _t.IndexOf(quote, valueStart);
                        if (valueEnd < 0) throw new FormatException("unterminated attribute value in <volt-island>");
                        attrValue = _t.Substring(valueStart, valueEnd - valueStart);
                        i = valueEnd + 1;
                    }
                    else
                    {
                        int valueStart = j;
                        while (j < _t.Length && !char.IsWhiteSpace(_t[j]) && _t[j] != '>') j++;
                        attrValue = _t.Substring(valueStart, j - valueStart);
                        i = j;
                    }
                }

                if (attrName == "name") name = attrValue;
                else if (attrName == "state") state = attrValue;
                else if (attrName == "sid") sid = attrValue;
            }
            _i = i;

            if (name.Length == 0)
                throw new FormatException("<volt-island> requires a name attribute");
            if (state.Length == 0)
                throw new FormatException("<volt-island> requires a state attribute (wire-format JSON)");

            if (sid.Length > 0)
                _sb.AppendLine($"            w.IslandByName({Symbol(name)}, {Symbol(state)}, ctx, {Symbol(sid)});");
            else
                _sb.AppendLine($"            w.IslandByName({Symbol(name)}, {Symbol(state)}, ctx);");
            return 0;
        }

        private static string Symbol(string literal) => "\"" + literal.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        /// <summary>'@' transitions: @@, @{code}, @(expr), @keyword {…}, @expr.</summary>
        private void ParseTransition()
        {
            // '@@' → literal @
            if (_i + 1 < _t.Length && _t[_i + 1] == '@')
            {
                EmitText("@");
                _i += 2;
                return;
            }

            // '@{ raw code }'
            if (_i + 1 < _t.Length && _t[_i + 1] == '{')
            {
                int end = FindMatchingBrace(_t, _i + 1);
                var code = _t.Substring(_i + 2, end - _i - 2);
                _sb.AppendLine();
                foreach (var line in SplitLines(code))
                    _sb.AppendLine("            " + line);
                _i = end + 1;
                return;
            }

            // '@(expr)'
            if (_i + 1 < _t.Length && _t[_i + 1] == '(')
            {
                int end = FindMatchingParen(_t, _i + 1);
                var expr = _t.Substring(_i + 2, end - _i - 2);
                _sb.AppendLine($"            w.Text(({expr}).ToString());");
                _i = end + 1;
                return;
            }

            // '@renderbody' — layout body slot marker
            if (_t.AsSpan(_i + 1).StartsWith("renderbody", StringComparison.Ordinal))
            {
                int after = _i + 1 + "renderbody".Length;
                if (after >= _t.Length || !char.IsLetterOrDigit(_t[after]))
                {
                    if (!_isLayout)
                        throw new FormatException("@renderbody is only allowed in layout files");
                    if (RenderBodyMark >= 0)
                        throw new FormatException("only one @renderbody per layout");
                    _i = after;
                    RenderBodyMark = _sb.Length;
                    return;
                }
            }

            // partial call: @Name(expr) where Name is a declared partial
            {
                int p = _i + 1;
                while (p < _t.Length && (char.IsLetterOrDigit(_t[p]) || _t[p] == '_')) p++;
                var pword = _t.Substring(_i + 1, p - _i - 1);
                if (pword.Length > 0 && _partials.ContainsKey(pword))
                {
                    if (_isPartial)
                        throw new FormatException($"partial '{pword}' cannot call another partial (keep partials flat)");
                    while (p < _t.Length && char.IsWhiteSpace(_t[p])) p++;
                    if (p < _t.Length && _t[p] == '(')
                    {
                        int end = FindMatchingParen(_t, p);
                        var args = _t.Substring(p + 1, end - p - 1).Trim();
                        _i = end + 1;
                        _sb.AppendLine($"            global::Volt.Generated.VoltPartials.{pword}(w, ctx{(args.Length > 0 ? ", " + args : "")});");
                        return;
                    }
                    throw new FormatException($"partial call '@{pword}' requires parentheses: @{pword}(model)");
                }
            }

            // keyword statement: @if (…) { … }
            int k = _i + 1;
            while (k < _t.Length && (char.IsLetterOrDigit(_t[k]) || _t[k] == '_')) k++;
            var word = _t.Substring(_i + 1, k - _i - 1);

            if (CodeKeywords.Contains(word))
            {
                while (k < _t.Length && char.IsWhiteSpace(_t[k])) k++;
                string condition = "";
                if (k < _t.Length && _t[k] == '(')
                {
                    int end = FindMatchingParen(_t, k);
                    condition = _t.Substring(k, end - k + 1);
                    k = end + 1;
                    while (k < _t.Length && char.IsWhiteSpace(_t[k])) k++;
                }
                if (k < _t.Length && _t[k] == '{')
                {
                    _sb.AppendLine($"            {word} {condition}");
                    _sb.AppendLine("            {");
                    _i = ParseCodeBlock(k + 1);
                    _sb.AppendLine("            }");
                    return;
                }
                // braceless single statement (rare): treat the rest of the line as C#
                int lineEnd = _t.IndexOf('\n', k);
                if (lineEnd < 0) lineEnd = _t.Length;
                _sb.AppendLine($"            {word} {condition} {_t.Substring(k, lineEnd - k).Trim()};");
                _i = lineEnd;
                return;
            }

            // bare expression: @identifier.path
            int e = _i + 1;
            while (e < _t.Length && (char.IsLetterOrDigit(_t[e]) || _t[e] is '_' or '.' or '[' or ']')) e++;
            if (e == _i + 1)
            {
                EmitText("@");
                _i++;
                return;
            }
            var bare = _t.Substring(_i + 1, e - _i - 1);
            _sb.AppendLine($"            w.Text(({bare}).ToString());");
            _i = e;
        }

        /// <summary>Text run until '&lt;', '@', newline-ish boundary.</summary>
        private void ParseTextRun()
        {
            int end = _i;
            while (end < _t.Length && _t[end] != '<' && _t[end] != '@' && _t[end] != '}') end++;
            if (end == _i) end++; // single char (e.g. '}' at top level)
            var text = _t.Substring(_i, end - _i);
            if (text.Trim().Length > 0)
                EmitText(text);
            _i = end;
        }

        /// <summary>Whitespace between nodes: keep when directly followed by text, else drop.</summary>
        private void SkipInterNodeWhitespace(bool topLevel)
        {
            int end = _i;
            while (end < _t.Length && char.IsWhiteSpace(_t[end])) end++;
            int after = end;
            while (after < _t.Length && char.IsWhiteSpace(_t[after])) after++;
            if (after < _t.Length && _t[after] != '<' && _t[after] != '@' && (topLevel || _t[after] != '}'))
                EmitText(_t.Substring(_i, end - _i));
            _i = end;
        }

        // ------------------------------------------------------------------
        // emit helpers
        // ------------------------------------------------------------------

        private void EmitText(string text)
        {
            if (_minify && _preserve.Count == 0 && HasWhitespaceRun(text))
                text = CollapseWhitespace(text);
            _sb.AppendLine($"            w.Text(\"{EscapeCsString(text)}\");");
        }

        private static bool HasWhitespaceRun(string text)
        {
            for (int i = 0; i + 1 < text.Length; i++)
                if (char.IsWhiteSpace(text[i]) && char.IsWhiteSpace(text[i + 1])) return true;
            return false;
        }

        /// <summary>M6 compile-time minification: runs of whitespace → single space (leading/trailing kept — inline spacing is preserved).</summary>
        private static string CollapseWhitespace(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            bool inRun = false;
            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!inRun) { sb.Append(' '); inRun = true; }
                }
                else
                {
                    sb.Append(c);
                    inRun = false;
                }
            }
            return sb.ToString();
        }

        private void PreserveClosed(string lowerName)
        {
            for (int i = _preserve.Count - 1; i >= 0; i--)
                if (_preserve[i] == lowerName) { _preserve.RemoveAt(i); return; }
        }


        private void EmitAttr(string name, string value, bool isExpr)
        {
            if (name == "class" && !isExpr)
            {
                _sb.AppendLine($"            w.Class(\"{EscapeCsString(value)}\");");
                return;
            }
            if (isExpr)
            {
                _sb.AppendLine($"            w.Attr(\"{name}\", {BuildAttrValue(value)});");
                return;
            }
            _sb.AppendLine($"            w.Attr(\"{name}\", \"{EscapeCsString(value)}\");");
        }

        /// <summary>Splits an attribute value with @expressions into a C# expression.</summary>
        private static string BuildAttrValue(string value)
        {
            var parts = new List<string>();
            int i = 0;
            while (i < value.Length)
            {
                int at = value.IndexOf('@', i);
                if (at < 0)
                {
                    parts.Add("\"" + EscapeCsString(value.Substring(i)) + "\"");
                    break;
                }
                if (at > i)
                    parts.Add("\"" + EscapeCsString(value.Substring(i, at - i)) + "\"");
                if (at + 1 < value.Length && value[at + 1] == '(')
                {
                    int end = FindMatchingParen(value, at + 1);
                    parts.Add("(" + value.Substring(at + 2, end - at - 2) + ").ToString()");
                    i = end + 1;
                }
                else
                {
                    int j = at + 1;
                    while (j < value.Length && (char.IsLetterOrDigit(value[j]) || value[j] is '_' or '.' or '[' or ']')) j++;
                    parts.Add("(" + value.Substring(at + 1, j - at - 1) + ").ToString()");
                    i = j;
                }
            }
            return string.Join(" + ", parts);
        }

        private static string EscapeCsString(string s)
            => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");

        private static int FindMatchingBrace(string s, int open)
        {
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] == '{') depth++;
                else if (s[i] == '}') { depth--; if (depth == 0) return i; }
            }
            throw new FormatException("unbalanced braces in code block");
        }

        private static int FindMatchingParen(string s, int open)
        {
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')') { depth--; if (depth == 0) return i; }
            }
            throw new FormatException("unbalanced parentheses");
        }

        private static List<string> SplitLines(string s)
        {
            var result = new List<string>();
            foreach (var line in s.Replace("\r\n", "\n").Split('\n'))
            {
                var t = line.Trim();
                if (t.Length > 0) result.Add(t);
            }
            return result;
        }
    }

    // ------------------------------------------------------------------
    // naming / routing
    // ------------------------------------------------------------------

    internal static string SafeName(string filePath)
    {
        var normalized = filePath.Replace('\\', '/');
        int pagesAt = normalized.LastIndexOf("/Pages/", StringComparison.OrdinalIgnoreCase);
        var rel = pagesAt >= 0 ? normalized.Substring(pagesAt + "/Pages/".Length) : System.IO.Path.GetFileNameWithoutExtension(normalized);
        var dot = rel.LastIndexOf('.');
        if (dot >= 0) rel = rel.Substring(0, dot); // strip .volt
        var ident = new StringBuilder();
        foreach (var ch in rel)
        {
            if (char.IsLetterOrDigit(ch)) ident.Append(ch);
            else if (ch is '_' or '-' or '/' or '.') ident.Append('_');
        }
        var name = ident.ToString().Trim('_');
        return "Volt_" + (name.Length > 0 ? name : "Page");
    }

    internal static string RouteFromVoltPath(string filePath)
    {
        var normalized = filePath.Replace('\\', '/');
        int pagesAt = normalized.LastIndexOf("/Pages/", StringComparison.OrdinalIgnoreCase);
        var rel = pagesAt >= 0 ? normalized.Substring(pagesAt + "/Pages/".Length) : System.IO.Path.GetFileName(normalized);
        var dot = rel.LastIndexOf('.');
        if (dot >= 0) rel = rel.Substring(0, dot);

        var segments = rel.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return "/";
        var last = segments[segments.Length - 1];
        if (last.Equals("index", StringComparison.OrdinalIgnoreCase))
            return segments.Length == 1 ? "/" : "/" + string.Join("/", TakeAllButLast(segments)).ToLowerInvariant();
        for (int i = 0; i < segments.Length; i++)
            segments[i] = segments[i].ToLowerInvariant();
        return "/" + string.Join("/", segments);
    }

    private static string[] TakeAllButLast(string[] segments)
    {
        var result = new string[segments.Length - 1];
        Array.Copy(segments, result, segments.Length - 1);
        return result;
    }
}
