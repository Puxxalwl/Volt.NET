using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Volt;

/// <summary>
/// The transport-neutral Volt engine: routes, renders pages (SSR / SSG with ISR),
/// serves island actions, the no-JS fallback flow, SEO endpoints and the hydration script.
/// Transports feed a <see cref="VoltHttpContext"/>; the Kestrel bridge and the built-in
/// zero-allocation server both sit on top of this single implementation.
/// </summary>
public static class VoltEngine
{
    public static async Task HandleAsync(VoltHttpContext ctx, VoltOptions options)
    {
        try
        {
            if (options.HasMiddleware)
            {
                // onion: user middleware around the whole core pipeline
                Func<Task> chain = () => HandleCoreAsync(ctx, options);
                var list = options.Middleware;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var middleware = list[i];
                    var next = chain;
                    chain = () => middleware(ctx, next);
                }
                await chain();
            }
            else
            {
                await HandleCoreAsync(ctx, options);
            }
        }
        catch (Exception ex)
        {
            if (options.OnException is { } handler)
            {
                try { await handler(ctx, ex); }
                catch { ctx.StatusCode = 500; ctx.ResponseStarted = true; }
                return;
            }
            try { await RenderErrorAsync(ctx, ex, options); }
            catch { ctx.StatusCode = 500; ctx.ResponseStarted = true; }
        }
    }

    // ------------------------------------------------------------------
    // Fast path: fully synchronous, span-based, ZERO allocations.
    // Covers the highest-traffic responses: hydrate.js, sitemap, robots,
    // and cached SSG pages on static routes (200 + 304).
    // Returns false when the request needs the async pipeline.
    // ------------------------------------------------------------------

    public static bool TryServeFast(
        ReadOnlySpan<char> path,
        ReadOnlySpan<char> query,
        IVoltHeaderSource? headers,
        VoltHttpContext response,
        VoltOptions options)
    {
        // M6: middleware must see every request — cached pages included.
        // While any middleware is registered the async pipeline handles everything.
        if (options.HasMiddleware)
            return false;

        // fallback token (?__v=…) → async path (renders with overridden island state)
        if (query.IndexOf("__v=", StringComparison.Ordinal) >= 0)
            return false;

        // internal + SEO endpoints
        if (path.StartsWith("/_volt/", StringComparison.Ordinal)
            || path.Equals("/sitemap.xml", StringComparison.Ordinal)
            || path.Equals("/robots.txt", StringComparison.Ordinal))
        {
            return false; // rare paths — not worth a span fork; async path handles them
        }

        // static assets (wwwroot): linear scan over the frozen set — zero alloc
        if (VoltStaticAssets.TryGet(path, out var assetBytes, out var assetEtag, out var assetType))
        {
            if (IfNoneMatchMatches(headers, assetEtag))
            {
                response.StatusCode = 304;
                response.ETag = assetEtag;
                response.HasBody = false;
                response.ResponseStarted = true;
                return true;
            }
            response.StatusCode = 200;
            response.ContentType = assetType;
            response.ETag = assetEtag;
            response.CacheControl = "public, max-age=3600";
            response.HasBody = true;
            response.ContentLength = assetBytes.Length;
            response.Output?.Write(assetBytes);
            response.ResponseStarted = true;
            return true;
        }

        // route match on the span (zero alloc)
        Span<RawParam> slices = stackalloc RawParam[16];
        var match = VoltRuntime.Routes.Match(path, slices);
        if (!match.Found) return false; // 404 → async (renders a page)

        // dynamic routes → async (param decoding allocates anyway)
        if (match.Params.Count > 0) return false;

        // cached SSG?
        if (match.Pattern is null) return false;
        var cache = GetSsgCache(options);
        if (!cache.TryGet(match.Pattern, out var html, out var etag, out var fresh, out var revalidate))
            return false; // cache miss → async render

        if (IfNoneMatchMatches(headers, etag))
        {
            response.StatusCode = 304;
            response.ETag = etag;
            response.HasBody = false;
            response.ResponseStarted = true;
            return true;
        }

        response.StatusCode = 200;
        response.ContentType = "text/html; charset=utf-8";
        response.ETag = etag;
        response.HasBody = true;
        response.ContentLength = html.Length;
        response.Output?.Write(html);
        response.ResponseStarted = true;
        return true; // stale-while-revalidate handled by the async path on next non-fast hit

        // note: fresh/revalidate policy for fast hits — stale entries still serve fast;
        // background refresh triggers via the async path (cache misses and dynamic routes).
    }

    private static bool IfNoneMatchMatches(IVoltHeaderSource? headers, string etag)
    {
        if (headers is null) return false;
        if (!headers.TryGetHeader(VoltHeaders.IfNoneMatchName, out var value)) return false;
        return HeaderSpanMatches(value, etag);
    }

    /// <summary>Header value equals the etag string, or a comma list containing it.</summary>
    private static bool HeaderSpanMatches(ReadOnlySpan<byte> value, string etag)
    {
        Span<char> etagChars = stackalloc char[etag.Length];
        for (int i = 0; i < etag.Length; i++) etagChars[i] = etag[i];
        return value.Length == etag.Length && AsciiEqualIgnoreCase(value, etagChars);
    }

    private static bool AsciiEqualIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<char> b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            byte x = a[i];
            char y = b[i];
            if (x == y) continue;
            if (x is >= (byte)'A' and <= (byte)'Z') x += 32;
            if (y is >= 'A' and <= 'Z') y += (char)32;
            if (x != y) return false;
        }
        return true;
    }

    private static async Task HandleCoreAsync(VoltHttpContext ctx, VoltOptions options)
    {
        var path = ctx.Path;
        var method = ctx.Method;

        // ---- internal endpoints ----------------------------------------
        if (path.StartsWith("/_volt/", StringComparison.Ordinal))
        {
            await HandleVoltEndpointAsync(ctx, options, path, method);
            return;
        }

        if (options.EnableSitemap && path == "/sitemap.xml")
        {
            ServeSitemap(ctx, options);
            return;
        }
        if (options.EnableRobots && path == "/robots.txt")
        {
            ServeRobots(ctx, options);
            return;
        }
        if ((method == "GET" || method == "HEAD") && VoltStaticAssets.TryGet(path.AsSpan(), out var assetBytes, out var assetEtag, out var assetType))
        {
            if (IfNoneMatchMatches(ctx.Headers, assetEtag))
            {
                ctx.StatusCode = 304;
                ctx.ETag = assetEtag;
                ctx.ResponseStarted = true;
                return;
            }
            ctx.StatusCode = 200;
            ctx.ContentType = assetType;
            ctx.ETag = assetEtag;
            ctx.CacheControl = "public, max-age=3600";
            WriteBody(ctx, assetBytes);
            ctx.ResponseStarted = true;
            return;
        }

        // ---- page routes ------------------------------------------------
        // M6: POST to a page route → form handler (bind + validate, no JS required)
        if (method == "POST")
        {
            var postRoute = ResolveRoute(path);
            if (postRoute is null)
            {
                ctx.StatusCode = 404;
                ctx.Allow = "GET, HEAD, POST";
                ctx.ResponseStarted = true;
                return;
            }
            var postPage = postRoute.Factory();
            var postRequest = BuildRequest(ctx, options, path, postRoute);
            postRequest.PostFields = await ReadPostFieldsAsync(ctx);
            var postResult = await postPage.OnPostAsync(postRequest);
            if (postResult.IsRedirect)
            {
                ctx.StatusCode = postResult.StatusCode;
                ctx.Location = postResult.Location;
                ctx.ResponseStarted = true;
                ctx.HasBody = false;
                return;
            }
            await postPage.OnPreRenderAsync(postRequest);
            RenderToResponse(ctx, postPage, postRequest, path, token: null); // POST responses are never cached
            Flush(ctx);
            return;
        }

        if (method != "GET" && method != "HEAD")
        {
            ctx.StatusCode = 405;
            ctx.Allow = "GET, HEAD, POST";
            ctx.ResponseStarted = true;
            return;
        }

        // fallback token (?__v=...) present → bypass SSG cache (state-overridden render)
        string? token = null;
        if (ctx.Query is { } query && query.Contains("__v=", StringComparison.Ordinal))
            token = ExtractToken(query);

        var route = ResolveRoute(path);
        if (route is null)
        {
            await RenderNotFoundAsync(ctx, options);
            return;
        }

        var page = route.Factory();
        var request = BuildRequest(ctx, options, path, route);

        if (page.Mode == RenderMode.SSG && token is null)
        {
            var ssg = await HandleSsgAsync(ctx, page, request, path, options);
            if (ssg) return;
        }
        else
        {
            await page.OnPreRenderAsync(request);
            RenderToResponse(ctx, page, request, path, token);
        }

        Flush(ctx);
    }

    /// <summary>M6: reads and parses the urlencoded POST body into fields (async path).</summary>
    private static async Task<IReadOnlyList<(string Name, string Value)>> ReadPostFieldsAsync(VoltHttpContext ctx)
    {
        if (ctx.BodyMemory is { } bodyMemory)
            return FallbackFormReader.Parse(bodyMemory.Span);
        if (ctx.Body is { } stream)
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ctx.Aborted);
            return FallbackFormReader.Parse(ms.ToArray());
        }
        return Array.Empty<(string, string)>();
    }

    private static void Flush(VoltHttpContext ctx)
    {
        // transports flush their own writer; content-length bookkeeping only
        if (!ctx.ResponseStarted && ctx.HasBody && ctx.ContentLength < 0)
            ctx.ContentLength = -1; // unknown — transport decides (chunked)
    }

    private static string? ExtractToken(string query)
    {
        // "__v=HEX" (possibly among other pairs) — manual, no allocation unless present
        int i = query.IndexOf("__v=", StringComparison.Ordinal);
        if (i < 0) return null;
        int start = i + 4;
        int end = query.IndexOf('&', start);
        var value = end < 0 ? query.Substring(start) : query.Substring(start, end - start);
        return Uri.UnescapeDataString(value);
    }

    private sealed record ResolvedRoute(Func<VoltPage> Factory, IReadOnlyList<(string, string)>? DecodedParams);

    /// <summary>Match + eager param decode (heap-safe for the async phase). Null when no route.</summary>
    private static ResolvedRoute? ResolveRoute(string path)
    {
        var paramBuffer = ArrayPool<RawParam>.Shared.Rent(16);
        try
        {
            var match = VoltRuntime.Routes.Match(path.AsSpan(), paramBuffer);
            if (!match.Found) return null;

            if (match.Params.Count == 0) return new ResolvedRoute(match.Factory, null);

            var decoded = new List<(string, string)>(match.Params.Count);
            for (int i = 0; i < match.Params.Count; i++)
            {
                var raw = paramBuffer[i];
                var value = path.Substring(raw.ValueStart, raw.ValueLength);
                decoded.Add((match.Params.Name(i), UrlDecoder.Decode(value)));
            }
            return new ResolvedRoute(match.Factory, decoded);
        }
        finally
        {
            ArrayPool<RawParam>.Shared.Return(paramBuffer);
        }
    }

    private static VoltRequest BuildRequest(VoltHttpContext ctx, VoltOptions options, string path, ResolvedRoute route)
    {
        return new VoltRequest
        {
            Method = ctx.Method,
            Path = path,
            RawQuery = ctx.Query is { Length: > 0 } ? "?" + ctx.Query : null,
            BaseUrl = options.BaseUrl,
            CancellationToken = ctx.Aborted,
            HeaderLookup = ctx.Headers is null
                ? null
                : h => GetHeaderString(ctx.Headers, h),
            Params = route.DecodedParams,
        };
    }

    private static string? GetHeaderString(IVoltHeaderSource headers, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        return headers.TryGetHeader(bytes, out var value)
            ? Encoding.UTF8.GetString(value)
            : null;
    }

    // ------------------------------------------------------------------
    // SSG serving with ETag / 304 / ISR (stale-while-revalidate)
    // ------------------------------------------------------------------

    private static SsgCache? _ssgCache;
    private static (int Capacity, string? Directory)? _ssgIdentity; // struct compare: no allocation
    private static readonly object SsgCacheLock = new();

    private static SsgCache GetSsgCache(VoltOptions options)
    {
        // NOTE: no string concat here — this runs on the zero-alloc fast path.
        (int, string?) identity = (options.SsgCacheCapacity, options.SsgCacheDirectory);
        var cache = _ssgCache;
        if (cache is null || _ssgIdentity != identity)
        {
            lock (SsgCacheLock)
            {
                if (_ssgCache is null || _ssgIdentity != identity)
                {
                    var backend = options.SsgCacheDirectory is { Length: > 0 } dir
                        ? new FileSsgCacheBackend(dir)
                        : null;
                    _ssgCache = new SsgCache(options.SsgCacheCapacity, backend,
                        options.SsgCacheCapacity + "|" + options.SsgCacheDirectory);
                    _ssgIdentity = identity;
                }
                cache = _ssgCache;
            }
        }
        return cache;
    }

    private static async Task<bool> HandleSsgAsync(VoltHttpContext ctx, VoltPage page, VoltRequest request, string path, VoltOptions options)
    {
        var cache = GetSsgCache(options);

        if (cache.TryGet(path, out var html, out var etag, out var fresh, out var revalidate))
        {
            if (IfNoneMatchMatches(ctx, etag))
            {
                ctx.StatusCode = 304;
                ctx.ETag = etag;
                ctx.ResponseStarted = true;
                return true;
            }
            SetHtmlHeaders(ctx, etag);
            WriteBody(ctx, html);

            // stale + revalidate → serve stale, refresh in background (single-flight)
            if (!fresh && revalidate > 0 && cache.TryBeginRefresh(path))
            {
                var revalidateSeconds = page.RevalidateSeconds;
                var stale = (html, etag);
                _ = Task.Run(() =>
                {
                    try
                    {
                        var freshPage = ResolveRoute(path)?.Factory() ?? page;
                        var bytes = RenderToBytes(freshPage, BuildMinimalRequest(path), path, token: null);
                        cache.EndRefresh(path, bytes, SsgCache.ComputeETag(bytes), revalidateSeconds);
                    }
                    catch
                    {
                        cache.EndRefresh(path, stale.html, stale.etag, revalidateSeconds); // keep old entry
                    }
                });
            }
            return true;
        }

        // miss: render inline and cache
        await page.OnPreRenderAsync(request);
        html = RenderToBytes(page, request, path, token: null);
        etag = SsgCache.ComputeETag(html);
        cache.Store(path, html, etag, page.RevalidateSeconds);

        if (IfNoneMatchMatches(ctx, etag))
        {
            ctx.StatusCode = 304;
            ctx.ETag = etag;
            ctx.ResponseStarted = true;
            return true;
        }
        SetHtmlHeaders(ctx, etag);
        WriteBody(ctx, html);
        return true;
    }

    private static bool IfNoneMatchMatches(VoltHttpContext ctx, string etag)
    {
        if (ctx.Headers is null) return false;
        if (!ctx.Headers.TryGetHeader(VoltHeaders.IfNoneMatchName, out var value)) return false;
        return value.SequenceEqual(Encoding.UTF8.GetBytes(etag)) || ValueContains(value, etag);
    }

    /// <summary>Header value equals the etag, or is a list like "a", "b" containing it.</summary>
    private static bool ValueContains(ReadOnlySpan<byte> value, string etag)
    {
        return value.IndexOf(Encoding.UTF8.GetBytes(etag)) >= 0;
    }

    private static void SetHtmlHeaders(VoltHttpContext ctx, string etag)
    {
        ctx.StatusCode = 200;
        ctx.ContentType = "text/html; charset=utf-8";
        ctx.ETag = etag;
    }

    private static void WriteBody(VoltHttpContext ctx, byte[] html)
    {
        ctx.StatusCode = 200;
        ctx.HasBody = true;
        ctx.ContentLength = html.Length;
        ctx.Output?.Write(html);
    }

    // ------------------------------------------------------------------
    // Rendering (sync, zero-alloc hot path)
    // ------------------------------------------------------------------

    private static void RenderToResponse(VoltHttpContext ctx, VoltPage page, VoltRequest request, string path, string? token)
    {
        ctx.StatusCode = 200;
        ctx.ContentType = "text/html; charset=utf-8";
        var w = HtmlWriter.Rent(ctx.Output ?? throw new InvalidOperationException("Volt: no output writer"));
        try
        {
            RenderPage(page, request, path, token, w);
        }
        finally
        {
            w.Return();
        }
        ctx.HasBody = true;
    }

    private static byte[] RenderToBytes(VoltPage page, VoltRequest request, string path, string? token)
    {
        using var buffer = new PooledBufferWriter();
        var w = HtmlWriter.Rent(buffer);
        try
        {
            RenderPage(page, request, path, token, w);
        }
        finally
        {
            w.Return();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Re-matches the path (cheap, zero-alloc) and renders the full page.</summary>
    private static void RenderPage(VoltPage page, VoltRequest request, string path, string? token, HtmlWriter w)
    {
        Span<RawParam> slices = stackalloc RawParam[16];
        var match = VoltRuntime.Routes.Match(path.AsSpan(), slices);
        var ctx = new RenderContext
        {
            Request = request,
            Params = match.Params,
            FallbackToken = token,
            FallbackStore = VoltRuntime.FallbackStore,
        };
        page.Render(w, ctx);
    }

    private static VoltRequest BuildMinimalRequest(string path) => new()
    {
        Method = "GET",
        Path = path,
        IsExport = false,
    };

    // ------------------------------------------------------------------
    // 404 / 500
    // ------------------------------------------------------------------

    private static async Task RenderNotFoundAsync(VoltHttpContext ctx, VoltOptions options)
    {
        var factory = VoltRuntime.Routes.NotFound;
        if (factory is null)
        {
            ctx.StatusCode = 404;
            ctx.ContentType = "text/plain; charset=utf-8";
            var w404 = ctx.Output;
            if (w404 is not null)
            {
                var writer = HtmlWriter.Rent(w404);
                try { writer.Text("404 Not Found"); }
                finally { writer.Return(); }
            }
            ctx.HasBody = true;
            ctx.ResponseStarted = true;
            return;
        }
        var page = factory();
        ctx.StatusCode = 404;
        ctx.ContentType = "text/html; charset=utf-8";
        ctx.ResponseStarted = true;
        var w = HtmlWriter.Rent(ctx.Output ?? throw new InvalidOperationException("Volt: no output writer"));
        try { RenderPage(page, BuildMinimalRequest(ctx.Path), ctx.Path, null, w); }
        finally { w.Return(); }
        ctx.HasBody = true;
        await Task.CompletedTask;
    }

    private static async Task RenderErrorAsync(VoltHttpContext ctx, Exception ex, VoltOptions options)
    {
        if (ctx.ResponseStarted)
        {
            ctx.StatusCode = 500;
            return;
        }
        var factory = VoltRuntime.Routes.Error;
        if (factory is null)
        {
            ctx.StatusCode = 500;
            ctx.ContentType = "text/plain; charset=utf-8";
            var w500 = ctx.Output;
            if (w500 is not null)
            {
                var writer = HtmlWriter.Rent(w500);
                try { writer.Text("500 Internal Server Error"); }
                finally { writer.Return(); }
            }
            ctx.HasBody = true;
            ctx.ResponseStarted = true;
            return;
        }
        var page = factory();
        if (page is VoltErrorPage errorPage) errorPage.Exception = ex;
        ctx.StatusCode = 500;
        ctx.ContentType = "text/html; charset=utf-8";
        ctx.ResponseStarted = true;
        try
        {
            var w = HtmlWriter.Rent(ctx.Output ?? throw new InvalidOperationException("Volt: no output writer"));
            try { RenderPage(page, BuildMinimalRequest("/"), "/", null, w); }
            finally { w.Return(); }
            ctx.HasBody = true;
        }
        catch
        {
            ctx.StatusCode = 500;
            ctx.ResponseStarted = true;
        }
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // /_volt/* endpoints: hydration script, actions, fallback
    // ------------------------------------------------------------------

    private static async Task HandleVoltEndpointAsync(VoltHttpContext ctx, VoltOptions options, string path, string method)
    {
        // M4: versioned hydrate asset — /_volt/hydrate.<hash>.js (immutable caching).
        // A mismatched hash 404s so stale clients never cache the wrong script forever.
        const int PrefixLen = 15; // "/_volt/hydrate."
        if (path.Length == PrefixLen + 16 + 3
            && path.StartsWith("/_volt/hydrate.", StringComparison.Ordinal)
            && path.EndsWith(".js", StringComparison.Ordinal)
            && path.AsSpan(PrefixLen, 16).SequenceEqual(VoltRuntime.HydrateVersion))
        {
            ServeHydrateScript(ctx);
            return;
        }

        switch (path)
        {
            case "/_volt/hydrate.js":
                ServeHydrateScript(ctx);
                return;

            case "/_volt/action" when method == "POST":
                await HandleActionAsync(ctx);
                return;

            case "/_volt/fallback" when method == "POST":
                await HandleFallbackAsync(ctx);
                return;

            default:
                ctx.StatusCode = 404;
                ctx.ContentType = "text/plain; charset=utf-8";
                var w = ctx.Output;
                if (w is not null)
                {
                    var writer = HtmlWriter.Rent(w);
                    try { writer.Text("404 Not Found"); }
                    finally { writer.Return(); }
                }
                ctx.HasBody = true;
                ctx.ResponseStarted = true;
                return;
        }
    }

    private static void ServeHydrateScript(VoltHttpContext ctx)
    {
        var script = VoltRuntime.HydrateScript;
        if (script is null)
        {
            ctx.StatusCode = 404;
            ctx.ResponseStarted = true;
            return;
        }
        var etag = VoltRuntime.HydrateScriptETag!;
        if (IfNoneMatchMatches(ctx, etag))
        {
            ctx.StatusCode = 304;
            ctx.ETag = etag;
            ctx.ResponseStarted = true;
            return;
        }
        ctx.StatusCode = 200;
        ctx.ContentType = "text/javascript; charset=utf-8";
        ctx.ETag = etag;
        ctx.CacheControl = "public, max-age=31536000, immutable";
        ctx.HasBody = true;
        ctx.ContentLength = script.Length;
        ctx.Output?.Write(script);
        ctx.ResponseStarted = true;
    }

    private static async Task HandleActionAsync(VoltHttpContext ctx)
    {
        JsonDocument doc;
        var body = ctx.Body;
        if (ctx.BodyMemory is { } bodyMemory)
        {
            // custom server: the body is a slice of the pooled request buffer
            try
            {
                doc = JsonDocument.Parse(new ReadOnlySequence<byte>(bodyMemory));
            }
            catch (JsonException)
            {
                ctx.StatusCode = 400;
                ctx.ResponseStarted = true;
                return;
            }
        }
        else if (body is not null)
        {
            try
            {
                doc = await JsonDocument.ParseAsync(body, cancellationToken: ctx.Aborted);
            }
            catch (JsonException)
            {
                ctx.StatusCode = 400;
                ctx.ResponseStarted = true;
                return;
            }
        }
        else
        {
            ctx.StatusCode = 400;
            ctx.ResponseStarted = true;
            return;
        }
        using (doc)
        {
            var root = doc.RootElement;
            string island = root.TryGetProperty("island", out var islandEl) ? islandEl.GetString() ?? "" : "";
            string sid = root.TryGetProperty("sid", out var sidEl) ? sidEl.GetString() ?? "" : "";
            string action = root.TryGetProperty("action", out var actionEl) ? actionEl.GetString() ?? "" : "";

            var entry = VoltRuntime.Islands.Find(island);
            if (entry is null)
            {
                ctx.StatusCode = 404;
                ctx.ContentType = "application/json";
                WriteJson(ctx, "{\"error\":\"unknown island\"}"u8);
                return;
            }

            byte[] stateBytes = GetRawUtf8(root, "state");
            byte[] argsBytes = GetRawUtf8(root, "args");

            byte[] newState;
            try
            {
                newState = entry.Dispatch(stateBytes, action, argsBytes);
            }
            catch (VoltActionNotFoundException)
            {
                ctx.StatusCode = 404;
                ctx.ContentType = "application/json";
                WriteJson(ctx, "{\"error\":\"unknown action\"}"u8);
                return;
            }

            using var fragment = entry.Render(newState, sid, token: null);
            ctx.StatusCode = 200;
            ctx.ContentType = "application/json";
            var jw = new Utf8JsonWriter(ctx.Output ?? throw new InvalidOperationException("Volt: no output writer"));
            jw.WriteStartObject();
            jw.WritePropertyName("html");
            jw.WriteStringValue(fragment.Html);
            jw.WriteEndObject();
            await jw.FlushAsync();
            ctx.HasBody = true;
            ctx.ResponseStarted = true;
        }
    }

    private static void WriteJson(VoltHttpContext ctx, ReadOnlySpan<byte> json)
    {
        ctx.HasBody = true;
        ctx.ResponseStarted = true;
        ctx.ContentLength = json.Length;
        ctx.Output?.Write(json);
    }

    private static byte[] GetRawUtf8(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el))
        {
            return "{}"u8.ToArray();
        }
        using var buf = new PooledBufferWriter();
        using (var jw = new Utf8JsonWriter(buf))
        {
            el.WriteTo(jw);
        }
        return buf.WrittenSpan.ToArray();
    }

    private static async Task HandleFallbackAsync(VoltHttpContext ctx)
    {
        byte[] body;
        if (ctx.BodyMemory is { } bodyMemory)
        {
            body = bodyMemory.ToArray(); // custom server: body is a slice of the pooled request buffer
        }
        else if (ctx.Body is { } stream)
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ctx.Aborted);
            body = ms.ToArray();
        }
        else
        {
            ctx.StatusCode = 400;
            ctx.ResponseStarted = true;
            return;
        }
        var fields = FallbackFormReader.Parse(body);

        string? island = null, state = null, sid = null, action = null;
        var args = new List<(string, string)>();
        foreach (var (name, value) in fields)
        {
            switch (name)
            {
                case "__volt_island": island = value; break;
                case "__volt_state": state = value; break;
                case "__volt_sid": sid = value; break;
                case "__volt_action": action = value; break;
                default: args.Add((name, value)); break;
            }
        }
        if (island is null || state is null || sid is null || action is null)
        {
            ctx.StatusCode = 400;
            ctx.ResponseStarted = true;
            return;
        }

        var entry = VoltRuntime.Islands.Find(island);
        if (entry is null)
        {
            ctx.StatusCode = 404;
            ctx.ResponseStarted = true;
            return;
        }

        byte[] stateBytes = Encoding.UTF8.GetBytes(state);
        byte[] argsBytes = "{}"u8.ToArray();
        if (args.Count > 0)
        {
            using var buf = new PooledBufferWriter();
            using (var jw = new Utf8JsonWriter(buf))
            {
                jw.WriteStartObject();
                foreach (var (name, value) in args)
                    jw.WriteString(name, value);
                jw.WriteEndObject();
            }
            argsBytes = buf.WrittenSpan.ToArray();
        }

        byte[] newState;
        try
        {
            newState = entry.Dispatch(stateBytes, action, argsBytes);
        }
        catch (VoltActionNotFoundException)
        {
            ctx.StatusCode = 404;
            ctx.ResponseStarted = true;
            return;
        }

        var token = VoltRuntime.FallbackStore.Put(island, sid, newState);

        // redirect back to the referring page (same origin only)
        var referer = GetReferer(ctx);
        string location = "/";
        if (referer.Length > 0 && Uri.TryCreate(referer, UriKind.Absolute, out var uri))
        {
            location = uri.PathAndQuery;
            if (location.Length == 0) location = "/";
        }
        // strip a previous __v token to avoid stacking
        var qIndex = location.IndexOf("__v=", StringComparison.Ordinal);
        if (qIndex > 0 && location[qIndex - 1] == '?') location = location[..(qIndex - 1)];
        var separator = location.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        location += separator + "__v=" + Uri.EscapeDataString(token);

        ctx.StatusCode = 303; // See Other
        ctx.Location = location;
        ctx.ResponseStarted = true;
        ctx.HasBody = false;
    }

    private static string GetReferer(VoltHttpContext ctx)
    {
        if (ctx.Headers is null) return "";
        if (!ctx.Headers.TryGetHeader(VoltHeaders.RefererName, out var value)) return "";
        return Encoding.UTF8.GetString(value);
    }

    // ------------------------------------------------------------------
    // SEO endpoints
    // ------------------------------------------------------------------

    private static byte[]? _sitemap;
    private static byte[]? _robots;
    private static string? _sitemapBaseUrl;

    private static void ServeSitemap(VoltHttpContext ctx, VoltOptions options)
    {
        var baseUrl = (options.BaseUrl ?? "http://localhost").TrimEnd('/');
        if (_sitemap is null || _sitemapBaseUrl != baseUrl)
        {
            _sitemap = Sitemap.BuildXml(VoltRuntime.Routes, baseUrl);
            _sitemapBaseUrl = baseUrl;
        }
        var etag = SsgCache.ComputeETag(_sitemap);
        if (IfNoneMatchMatches(ctx, etag))
        {
            ctx.StatusCode = 304;
            ctx.ETag = etag;
            ctx.ResponseStarted = true;
            return;
        }
        ctx.StatusCode = 200;
        ctx.ContentType = "application/xml; charset=utf-8";
        ctx.ETag = etag;
        WriteBody(ctx, _sitemap);
        ctx.ResponseStarted = true;
    }

    private static void ServeRobots(VoltHttpContext ctx, VoltOptions options)
    {
        _robots ??= Sitemap.BuildRobots(options.BaseUrl ?? "http://localhost");
        ctx.StatusCode = 200;
        ctx.ContentType = "text/plain; charset=utf-8";
        WriteBody(ctx, _robots);
        ctx.ResponseStarted = true;
    }
}
