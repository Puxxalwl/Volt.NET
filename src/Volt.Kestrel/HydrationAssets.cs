using System.Buffers;
using System.Reflection;
using System.Security.Cryptography;

namespace Volt;

/// <summary>Serves the embedded hydration script (/_volt/hydrate.js) with immutable caching.</summary>
internal static class HydrationAssets
{
    private static readonly Lazy<(byte[] Bytes, string ETag)> Script = new(LoadScript);

    private static (byte[], string) LoadScript()
    {
        var asm = typeof(Volt.Hydration.HydrationRuntime).Assembly;
        string? name = null;
        foreach (var n in asm.GetManifestResourceNames())
        {
            if (n.EndsWith("hydrate.js", StringComparison.Ordinal))
            {
                name = n;
                break;
            }
        }
        if (name is null) throw new InvalidOperationException("Volt: embedded hydrate.js not found.");

        using var stream = asm.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream((int)stream.Length);
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        var etag = "\"" + Fnv1a64(bytes).ToString("x16") + "\"";
        return (bytes, etag);
    }

    public static void Warmup() => _ = Script.Value;

    public static bool TryServe(Microsoft.AspNetCore.Http.HttpContext ctx)
    {
        var (bytes, etag) = Script.Value;
        if (ctx.Request.Headers.IfNoneMatch.ToString() == etag)
        {
            ctx.Response.StatusCode = 304;
            return true;
        }
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/javascript; charset=utf-8";
        ctx.Response.Headers.ETag = etag;
        ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        ctx.Response.ContentLength = bytes.Length;
        ctx.Response.BodyWriter.Write(bytes);
        return true;
    }

    /// <summary>Sets the cache-busting version from the script content hash.</summary>
    public static string ComputeVersion()
    {
        var (bytes, _) = Script.Value;
        return Fnv1a64(bytes).ToString("x8");
    }

    private static ulong Fnv1a64(ReadOnlySpan<byte> data)
    {
        ulong hash = 14695981039346656037;
        foreach (byte b in data)
        {
            hash ^= b;
            hash *= 1099511628211;
        }
        return hash;
    }
}
