using System.Diagnostics;

namespace Volt;

public enum RouteSegmentKind
{
    Static = 3,
    Param = 2,
    CatchAll = 1,
}

/// <summary>
/// A parsed route pattern. Registered once (startup); matched per request with zero allocation.
/// Supports Next.js-style segments ([slug], [...rest]) and template-style segments ({slug}, {*rest}).
/// </summary>
[DebuggerDisplay("{Pattern}")]
public sealed class RoutePattern
{
    internal sealed class Segment
    {
        public required string Text { get; init; }
        public required RouteSegmentKind Kind { get; init; }
        public int ParamNameIndex { get; init; } = -1; // index into ParamNames for Param/CatchAll
    }

    public string Pattern { get; }
    public Func<VoltPage> Factory { get; }
    internal Segment[] Segments { get; }
    internal string[] ParamNames { get; }

    private RoutePattern(string pattern, Segment[] segments, string[] paramNames, Func<VoltPage> factory)
    {
        Pattern = pattern;
        Segments = segments;
        ParamNames = paramNames;
        Factory = factory;
    }

    public static RoutePattern Parse(string pattern, Func<VoltPage> factory)
    {
        if (string.IsNullOrEmpty(pattern)) pattern = "/";
        if (pattern[0] != '/') pattern = "/" + pattern;

        var segments = new List<Segment>();
        var names = new List<string>();
        foreach (var raw in pattern.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.StartsWith("[...") && raw.EndsWith(']') && raw.Length > 5)
                segments.Add(Make(raw, RouteSegmentKind.CatchAll, ref names));
            else if (raw.StartsWith('[') && raw.EndsWith(']') && raw.Length > 2)
                segments.Add(Make(raw, RouteSegmentKind.Param, ref names));
            else if (raw.StartsWith("{*") && raw.EndsWith('}') && raw.Length > 3)
                segments.Add(Make(raw, RouteSegmentKind.CatchAll, ref names));
            else if (raw.StartsWith('{') && raw.EndsWith('}') && raw.Length > 2)
                segments.Add(Make(raw, RouteSegmentKind.Param, ref names));
            else
                segments.Add(new Segment { Text = raw, Kind = RouteSegmentKind.Static });
        }
        return new RoutePattern(pattern, segments.ToArray(), names.ToArray(), factory);
    }

    private static Segment Make(string raw, RouteSegmentKind kind, ref List<string> names)
    {
        string name = kind == RouteSegmentKind.CatchAll
            ? (raw.StartsWith('[') ? raw[4..^1] : raw[2..^1])
            : (raw.StartsWith('[') ? raw[1..^1] : raw[1..^1]);
        int idx = names.Count;
        names.Add(name);
        return new Segment { Text = name, Kind = kind, ParamNameIndex = idx };
    }

    /// <summary>Matches a path (no trailing slash) into caller-provided stack slices.</summary>
    internal bool TryMatch(ReadOnlySpan<char> path, Span<RawParam> slices, out int count)
    {
        count = 0;
        if (path.IsEmpty || path[0] != '/') return false;

        // Root pattern matches only the root path.
        if (Segments.Length == 0)
            return path.Length == 1;

        int pi = 1; // index into path where the current segment starts
        for (int si = 0; si < Segments.Length; si++)
        {
            var seg = Segments[si];
            bool lastSeg = si == Segments.Length - 1;

            if (seg.Kind == RouteSegmentKind.CatchAll)
            {
                if (!lastSeg) return false; // catch-all must be the last segment
                if (count >= slices.Length) return false;
                slices[count++] = new RawParam(seg.ParamNameIndex, pi, path.Length - pi);
                return true;
            }

            // Extract [pi, segEnd)
            int segEnd = pi;
            while (segEnd < path.Length && path[segEnd] != '/') segEnd++;
            if (segEnd == pi) return false; // path ran out of segments

            switch (seg.Kind)
            {
                case RouteSegmentKind.Static:
                    if (!path.Slice(pi, segEnd - pi).SequenceEqual(seg.Text.AsSpan())) return false;
                    break;
                case RouteSegmentKind.Param:
                    if (count >= slices.Length) return false;
                    slices[count++] = new RawParam(seg.ParamNameIndex, pi, segEnd - pi);
                    break;
            }

            pi = segEnd + 1;
        }

        // All pattern segments consumed — the path must be fully consumed as well.
        return pi == path.Length + 1;
    }

    internal int ParamCount => ParamNames.Length;

    /// <summary>Resolves a concrete URL path from a raw static-path value ("a/b" fills params in order).</summary>
    public string MakePath(string staticPathValue)
    {
        var parts = staticPathValue.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int pi = 0;
        var sb = new System.Text.StringBuilder(Pattern.Length + staticPathValue.Length + 8);
        foreach (var seg in Segments)
        {
            sb.Append('/');
            if (seg.Kind == RouteSegmentKind.Static) sb.Append(seg.Text);
            else if (pi < parts.Length) sb.Append(parts[pi++]);
            else sb.Append('-'); // placeholder for missing part
        }
        var result = sb.ToString();
        return result.Length == 0 ? "/" : result;
    }
}
