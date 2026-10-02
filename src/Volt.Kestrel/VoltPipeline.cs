using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Volt;

/// <summary>
/// The Volt request pipeline: dispatches routes, renders pages (SSR / SSG with ISR),
/// serves island actions, the no-JS fallback flow, SEO endpoints and the hydration script.
/// </summary>
internal static class VoltPipeline
{
    public static async Task HandleAsync(HttpContext ctx, VoltOptions options)
    {
        try
        {
            await HandleCoreAsync(ctx, options);
        }
        catch (Exception ex)
        {
            try { await RenderErrorAsync(ctx, ex, options); }
            catch { ctx.Response.StatusCode = 500; }
        }
    }

    private static async Task HandleCoreAsync(HttpContext ctx, VoltOptions options)
    {
        var path = ctx.Request.Path.Value ?? "/";
        var method = ctx.Request.Method;

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

        // ---- page routes ------------------------------------------------
        if (method != "GET" && method != "HEAD")
        {
            ctx.Response.StatusCode = 405;
            ctx.Response.Headers.Allow = "GET, HEAD";
            return;
        }

        // fallback token (?__v=...) present → bypass SSG cache (state-overridden render)
        string? token = null;
        var qs = ctx.Request.QueryString;
        if (qs.HasValue && qs.Value.Contains("__v=", StringComparison.Ordinal))
            token = ctx.Request.Query["__v"].ToString();

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

        await ctx.Response.BodyWriter.FlushAsync();
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

    private static VoltRequest BuildRequest(HttpContext ctx, VoltOptions options, string path, ResolvedRoute route)
    {
        return new VoltRequest
        {
            Method = ctx.Request.Method,
            Path = path,
            RawQuery = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value : null,
            BaseUrl = options.BaseUrl,
            CancellationToken = ctx.RequestAborted,
            HeaderLookup = h => ctx.Request.Headers.TryGetValue(h, out var values) && values.Count > 0 ? values[0] : null,
            Params = route.DecodedParams,
        };
    }

    // ------------------------------------------------------------------
    // SSG serving with ETag / 304 / ISR (stale-while-revalidate)
    // ------------------------------------------------------------------

    private static SsgCache? _ssgCache;

    private static async Task<bool> HandleSsgAsync(HttpContext ctx, VoltPage page, VoltRequest request, string path, VoltOptions options)
    {
        _ssgCache ??= new SsgCache(options.SsgCacheCapacity);
        var cache = _ssgCache;

        if (cache.TryGet(path, out var html, out var etag, out var fresh, out var revalidate))
        {
            if (ctx.Request.Headers.IfNoneMatch.ToString() == etag)
            {
                ctx.Response.StatusCode = 304;
                return true;
            }
            SetHtmlHeaders(ctx, etag);
            WriteBody(ctx, html);

            // stale + revalidate → serve stale, refresh in background (single-flight)
            if (!fresh && revalidate > 0 && cache.TryBeginRefresh(path))
            {
                var revalidateSeconds = page.RevalidateSeconds;
                _ = Task.Run(() =>
                {
                    try
                    {
                        var freshPage = page.GetType().IsValueType ? page : (VoltPage)Activator.CreateInstance(page.GetType())!;
                        // AOT-safe: pages come from generated factories; recreate through the route factory instead
                        freshPage = ResolveRoute(path)?.Factory() ?? page;
                        var bytes = RenderToBytes(freshPage, BuildExportRequest(path), path, token: null);
                        cache.EndRefresh(path, bytes, SsgCache.ComputeETag(bytes), revalidateSeconds);
                    }
                    catch
                    {
                        cache.EndRefresh(path, html, etag, revalidateSeconds); // keep old entry
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

        if (ctx.Request.Headers.IfNoneMatch.ToString() == etag)
        {
            ctx.Response.StatusCode = 304;
            return true;
        }
        SetHtmlHeaders(ctx, etag);
        WriteBody(ctx, html);
        return true;
    }

    private static void SetHtmlHeaders(HttpContext ctx, string etag)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.ETag = etag;
    }

    private static void WriteBody(HttpContext ctx, byte[] html)
    {
        ctx.Response.ContentLength = html.Length;
        ctx.Response.BodyWriter.Write(html);
    }

    // ------------------------------------------------------------------
    // Rendering (sync, zero-alloc hot path)
    // ------------------------------------------------------------------

    private static void RenderToResponse(HttpContext ctx, VoltPage page, VoltRequest request, string path, string? token)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        var w = HtmlWriter.Rent(ctx.Response.BodyWriter);
        try
        {
            RenderPage(page, request, path, token, w);
        }
        finally
        {
            w.Return();
        }
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

    private static VoltRequest BuildExportRequest(string path) => new()
    {
        Method = "GET",
        Path = path,
        IsExport = false,
    };

    // ------------------------------------------------------------------
    // 404 / 500
    // ------------------------------------------------------------------

    private static async Task RenderNotFoundAsync(HttpContext ctx, VoltOptions options)
    {
        var factory = VoltRuntime.Routes.NotFound;
        if (factory is null)
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync("404 Not Found");
            return;
        }
        var page = factory();
        ctx.Response.StatusCode = 404;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        var w = HtmlWriter.Rent(ctx.Response.BodyWriter);
        try { RenderPage(page, BuildExportRequest(ctx.Request.Path.Value ?? "/"), ctx.Request.Path.Value ?? "/", null, w); }
        finally { w.Return(); }
        await ctx.Response.BodyWriter.FlushAsync();
    }

    private static async Task RenderErrorAsync(HttpContext ctx, Exception ex, VoltOptions options)
    {
        if (ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = 500;
            return;
        }
        var factory = VoltRuntime.Routes.Error;
        if (factory is null)
        {
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync("500 Internal Server Error");
            return;
        }
        var page = factory();
        if (page is VoltErrorPage errorPage) errorPage.Exception = ex;
        ctx.Response.StatusCode = 500;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        try
        {
            var w = HtmlWriter.Rent(ctx.Response.BodyWriter);
            try { RenderPage(page, BuildExportRequest("/"), "/", null, w); }
            finally { w.Return(); }
            await ctx.Response.BodyWriter.FlushAsync();
        }
        catch
        {
            ctx.Response.StatusCode = 500;
        }
    }

    // ------------------------------------------------------------------
    // /_volt/* endpoints: hydration script, actions, fallback
    // ------------------------------------------------------------------

    private static async Task HandleVoltEndpointAsync(HttpContext ctx, VoltOptions options, string path, string method)
    {
        switch (path)
        {
            case "/_volt/hydrate.js":
                HydrationAssets.TryServe(ctx);
                await ctx.Response.BodyWriter.FlushAsync();
                return;

            case "/_volt/action" when method == "POST":
                await HandleActionAsync(ctx);
                return;

            case "/_volt/fallback" when method == "POST":
                await HandleFallbackAsync(ctx);
                return;

            default:
                ctx.Response.StatusCode = 404;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync("404 Not Found");
                return;
        }
    }

    private static async Task HandleActionAsync(HttpContext ctx)
    {
        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
        }
        catch (JsonException)
        {
            ctx.Response.StatusCode = 400;
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
                ctx.Response.StatusCode = 404;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"error\":\"unknown island\"}");
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
                ctx.Response.StatusCode = 404;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"error\":\"unknown action\"}");
                return;
            }

            using var fragment = entry.Render(newState, sid, token: null);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            var jw = new Utf8JsonWriter(ctx.Response.BodyWriter);
            jw.WriteStartObject();
            jw.WritePropertyName("html");
            jw.WriteStringValue(fragment.Html);
            jw.WriteEndObject();
            await jw.FlushAsync();
            await ctx.Response.BodyWriter.FlushAsync();
        }
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

    private static async Task HandleFallbackAsync(HttpContext ctx)
    {
        // read urlencoded body
        byte[] body;
        using (var ms = new MemoryStream())
        {
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            body = ms.ToArray();
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
            ctx.Response.StatusCode = 400;
            return;
        }

        var entry = VoltRuntime.Islands.Find(island);
        if (entry is null)
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        byte[] stateBytes = System.Text.Encoding.UTF8.GetBytes(state);
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
            ctx.Response.StatusCode = 404;
            return;
        }

        var token = VoltRuntime.FallbackStore.Put(island, sid, newState);

        // redirect back to the referring page (same origin only)
        var referer = ctx.Request.Headers.Referer.ToString();
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

        ctx.Response.StatusCode = 303; // See Other
        ctx.Response.Headers.Location = location;
    }

    // ------------------------------------------------------------------
    // SEO endpoints
    // ------------------------------------------------------------------

    private static byte[]? _sitemap;
    private static byte[]? _robots;

    private static void ServeSitemap(HttpContext ctx, VoltOptions options)
    {
        _sitemap ??= Sitemap.BuildXml(VoltRuntime.Routes, (options.BaseUrl ?? "http://localhost").TrimEnd('/'));
        var etag = SsgCache.ComputeETag(_sitemap);
        if (ctx.Request.Headers.IfNoneMatch.ToString() == etag)
        {
            ctx.Response.StatusCode = 304;
            return;
        }
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/xml; charset=utf-8";
        ctx.Response.Headers.ETag = etag;
        WriteBody(ctx, _sitemap);
    }

    private static void ServeRobots(HttpContext ctx, VoltOptions options)
    {
        _robots ??= Sitemap.BuildRobots(options.BaseUrl ?? "http://localhost");
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        WriteBody(ctx, _robots);
    }
}
