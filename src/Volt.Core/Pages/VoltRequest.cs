namespace Volt;

/// <summary>
/// Per-request data for the async phase (OnPreRenderAsync). Plain strings, safe to
/// capture in async state machines. Route parameters are eagerly URL-decoded here
/// only when the route is dynamic.
/// </summary>
public sealed class VoltRequest
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public string? RawQuery { get; init; }
    public string? BaseUrl { get; init; }
    public bool IsExport { get; init; }
    public CancellationToken CancellationToken { get; init; }

    /// <summary>Optional header lookup wired by the transport (null in export mode).</summary>
    public Func<string, string?>? HeaderLookup { get; init; }

    /// <summary>Eagerly decoded route parameters (null for static routes).</summary>
    public IReadOnlyList<(string Name, string Value)>? Params { get; init; }

    public string? Param(string name)
    {
        if (Params is null) return null;
        foreach (var (n, v) in Params)
            if (n == name) return v;
        return null;
    }

    /// <summary>Query parameter by name (parsed on demand; allocates only when used).</summary>
    public string? Query(string name)
    {
        if (RawQuery is null) return null;
        var q = RawQuery.AsSpan();
        while (!q.IsEmpty)
        {
            int amp = q.IndexOf('&');
            var pair = amp < 0 ? q : q[..amp];
            if (!pair.IsEmpty)
            {
                int eq = pair.IndexOf('=');
                var key = eq < 0 ? pair : pair[..eq];
                if (key.SequenceEqual(name.AsSpan()))
                {
                    var rawVal = eq < 0 ? default : pair[(eq + 1)..];
                    return UrlDecoder.Decode(rawVal);
                }
            }
            if (amp < 0) break;
            q = q[(amp + 1)..];
        }
        return null;
    }

    public string? Header(string name) => HeaderLookup?.Invoke(name);

    // ---- M6: typed forms --------------------------------------------------------------

    /// <summary>
    /// POST body fields (urlencoded), parsed by the engine before OnPostAsync.
    /// Null for requests without a body.
    /// </summary>
    public IReadOnlyList<(string Name, string Value)>? PostFields { get; internal set; }

    /// <summary>
    /// Binds the submitted POST fields to a form and validates it
    /// (generated binder — reflection-free, AOT safe). Returns false on
    /// validation errors; <paramref name="errors"/> carries them.
    /// </summary>
    public bool TryForm<T>(out T form, out IReadOnlyList<VoltFormError> errors)
        where T : class, IVoltForm
    {
        var fields = PostFields ?? Array.Empty<(string, string)>();
        var bound = T.VoltBind(fields, out var errorList);
        form = (T)bound!;
        errors = errorList is null ? Array.Empty<VoltFormError>() : errorList;
        return errorList is null;
    }
}
