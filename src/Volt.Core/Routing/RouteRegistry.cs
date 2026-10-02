namespace Volt;

/// <summary>The result of matching a request path; parameters are slices of the path.</summary>
public ref struct RouteMatch
{
    private readonly Func<VoltPage>? _factory;
    public RouteParams Params;

    /// <summary>The matched route's pattern (shared string reference — no allocation).</summary>
    public string? Pattern { get; private set; }

    public bool Found => _factory is not null;
    public Func<VoltPage> Factory => _factory ?? throw new InvalidOperationException("No route matched.");

    internal RouteMatch(Func<VoltPage> factory, RouteParams parameters, string? pattern = null)
    {
        _factory = factory;
        Params = parameters;
        Pattern = pattern;
    }

    public static RouteMatch None => default;
}

/// <summary>
/// Route table filled at startup by generated code (ModuleInitializer) and frozen on first match.
/// Routes are ordered statically-first (static beats param beats catch-all per segment).
/// </summary>
public sealed class RouteRegistry
{
    public static RouteRegistry Root { get; } = new();

    private readonly List<RoutePattern> _routes = new();
    private readonly object _lock = new();
    private RoutePattern[]? _frozen;
    private Func<VoltPage>? _notFound;
    private Func<VoltPage>? _error;

    public int Count => _routes.Count;

    public void Add(string pattern, Func<VoltPage> factory)
    {
        lock (_lock)
        {
            var parsed = RoutePattern.Parse(pattern, factory);
            foreach (var existing in _routes)
                if (existing.Pattern == parsed.Pattern)
                    throw new InvalidOperationException($"Volt: duplicate route '{parsed.Pattern}'.");
            _routes.Add(parsed);
            _frozen = null;
        }
    }

    public void SetNotFound(Func<VoltPage> factory)
    {
        lock (_lock) { _notFound = factory; _frozen = null; }
    }

    public void SetError(Func<VoltPage> factory)
    {
        lock (_lock) { _error = factory; _frozen = null; }
    }

    /// <summary>404 page factory, if a Pages/notfound.cs was declared.</summary>
    public Func<VoltPage>? NotFound => _notFound;

    /// <summary>500 page factory, if a Pages/error.cs was declared.</summary>
    public Func<VoltPage>? Error => _error;

    /// <summary>All registered patterns (sorted by specificity), for sitemap/export.</summary>
    public IReadOnlyList<RoutePattern> Routes
    {
        get { var frozen = _frozen ?? Freeze(); return frozen; }
    }

    /// <summary>
    /// Matches a path, writing route parameters into the caller-provided stack buffer —
    /// zero allocation on the hot path. The returned match (and its params) must not
    /// outlive the caller's frame or the path buffer.
    /// </summary>
    public RouteMatch Match(ReadOnlySpan<char> rawPath, Span<RawParam> paramBuffer)
    {
        // normalize: ignore a single trailing slash (except for the root itself)
        var path = rawPath.Length > 1 && rawPath[^1] == '/' ? rawPath[..^1] : rawPath;
        var routes = _frozen ?? Freeze();

        foreach (var route in routes)
        {
            if (route.TryMatch(path, paramBuffer, out var count))
                return new RouteMatch(route.Factory, new RouteParams(path, paramBuffer[..count], route.ParamNames), route.Pattern);
        }
        return RouteMatch.None;
    }

    private RoutePattern[] Freeze()
    {
        lock (_lock)
        {
            if (_frozen != null) return _frozen;
            var sorted = _routes.ToArray();
            Array.Sort(sorted, Compare);
            _frozen = sorted;
            return sorted;
        }
    }

    /// <summary>
    /// Specificity order: at the first differing segment, static beats param beats catch-all;
    /// more segments beat fewer; then ordinal (deterministic).
    /// </summary>
    internal static int Compare(RoutePattern x, RoutePattern y)
    {
        int n = Math.Min(x.Segments.Length, y.Segments.Length);
        for (int i = 0; i < n; i++)
        {
            int kx = (int)x.Segments[i].Kind;
            int ky = (int)y.Segments[i].Kind;
            if (kx != ky) return ky - kx;
        }
        if (x.Segments.Length != y.Segments.Length)
            return y.Segments.Length - x.Segments.Length;
        return string.CompareOrdinal(x.Pattern, y.Pattern);
    }
}
