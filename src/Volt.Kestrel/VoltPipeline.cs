using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Volt;

/// <summary>
/// Kestrel adapter: bridges ASP.NET Core HttpContext ⇄ the transport-neutral VoltHttpContext
/// and delegates all work to VoltEngine. Pooled per request.
/// </summary>
internal static class VoltPipeline
{
    private static readonly ConcurrentQueue<VoltHttpContext> Pool = new();

    private static VoltHttpContext Rent()
    {
        if (Pool.TryDequeue(out var ctx)) return ctx;
        return new VoltHttpContext();
    }

    private static void Return(VoltHttpContext ctx)
    {
        ctx.Reset();
        if (Pool.Count < 256) Pool.Enqueue(ctx);
    }

    public static async Task HandleAsync(HttpContext ctx, VoltOptions options)
    {
        var vctx = Rent();
        try
        {
            FillRequest(vctx, ctx);
            vctx.Output = ctx.Response.BodyWriter;
            await VoltEngine.HandleAsync(vctx, options);
            await WriteResponseAsync(ctx, vctx);
        }
        finally
        {
            Return(vctx);
        }
    }

    private static void FillRequest(VoltHttpContext vctx, HttpContext ctx)
    {
        vctx.Method = ctx.Request.Method;
        vctx.Path = ctx.Request.Path.Value ?? "/";
        vctx.Query = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value!.TrimStart('?') : null;
        vctx.Body = ctx.Request.ContentLength is > 0 ? ctx.Request.Body : null;
        vctx.Headers = new KestrelHeaderSource(ctx.Request.Headers);
        vctx.Aborted = ctx.RequestAborted;
    }

    private static async Task WriteResponseAsync(HttpContext ctx, VoltHttpContext vctx)
    {
        if (vctx.StatusCode >= 300 && vctx.StatusCode < 400 && vctx.Location is not null)
        {
            ctx.Response.StatusCode = vctx.StatusCode;
            ctx.Response.Headers.Location = vctx.Location;
            return;
        }

        ctx.Response.StatusCode = vctx.StatusCode;
        if (vctx.ContentType is not null) ctx.Response.ContentType = vctx.ContentType;
        if (vctx.ETag is not null) ctx.Response.Headers.ETag = vctx.ETag;
        if (vctx.CacheControl is not null) ctx.Response.Headers.CacheControl = vctx.CacheControl;
        if (vctx.Allow is not null) ctx.Response.Headers.Allow = vctx.Allow;
        if (vctx.ContentLength >= 0) ctx.Response.ContentLength = vctx.ContentLength;
        if (vctx.ExtraHeaders is { Count: > 0 })
            foreach (var (name, value) in vctx.ExtraHeaders)
                ctx.Response.Headers[name] = value;
        if (vctx.HasBody)
            await ctx.Response.BodyWriter.FlushAsync(ctx.RequestAborted);
    }

    /// <summary>Header access for the engine: converts the IHeaderDictionary on demand.</summary>
    private sealed class KestrelHeaderSource : IVoltHeaderSource
    {
        private readonly IHeaderDictionary _headers;
        public KestrelHeaderSource(IHeaderDictionary headers) => _headers = headers;

        public bool TryGetHeader(ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value)
        {
            // ASCII name → string lookup in the ASP.NET dictionary
            Span<char> nameBuf = stackalloc char[name.Length];
            for (int i = 0; i < name.Length; i++) nameBuf[i] = (char)name[i];
            var key = new string(nameBuf);
            var values = _headers[key];
            if (values.Count == 0)
            {
                value = default;
                return false;
            }
            var first = values[0];
            if (first is null)
            {
                value = default;
                return false;
            }
            value = Encoding.UTF8.GetBytes(first.ToString());
            return true;
        }
    }
}
