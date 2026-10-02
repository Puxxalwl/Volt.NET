using System.Buffers;

namespace Volt;

/// <summary>
/// Transport-neutral request/response contract. The transport fills the request side,
/// the engine (VoltEngine) fills the response side, the transport flushes it.
/// Pooled per request by each transport.
/// </summary>
public sealed class VoltHttpContext
{
    // ---- request side (set by the transport) ------------------------
    public string Method = "GET";
    public string Path = "/";              // raw path, no query string
    public string? Query;                  // raw query string without the leading '?'
    public Stream? Body;                   // request body (POST/PUT), null when empty
    public IVoltHeaderSource? Headers;     // span-based header access
    public CancellationToken Aborted;

    // ---- response side (set by the engine) --------------------------
    public int StatusCode = 200;
    public string? ContentType;
    public string? Location;               // 303 redirects (no-JS fallback)
    public string? ETag;
    public string? CacheControl;
    public string? Allow;                  // 405 responses
    public long ContentLength = -1;        // -1: unknown / chunked
    public IBufferWriter<byte>? Output;    // engine writes the body here
    public bool HasBody;

    /// <summary>Response header line + body already produced (error pages must not re-write).</summary>
    public bool ResponseStarted;

    /// <summary>Custom server: slice of the raw request buffer holding the body (avoids a stream).</summary>
    public ReadOnlyMemory<byte>? BodyMemory;

    public void Reset()
    {
        Method = "GET";
        Path = "/";
        Query = null;
        Body = null;
        Headers = null;
        Aborted = default;
        StatusCode = 200;
        ContentType = null;
        Location = null;
        ETag = null;
        CacheControl = null;
        Allow = null;
        ContentLength = -1;
        Output = null;
        HasBody = false;
        ResponseStarted = false;
        BodyMemory = null;
    }
}

/// <summary>
/// Zero-allocation header access: names/values are slices into the transport's request buffer.
/// Lookups compare UTF-8 bytes directly — no string materialization.
/// </summary>
public interface IVoltHeaderSource
{
    bool TryGetHeader(ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value);
}

/// <summary>Header helpers shared by transports.</summary>
public static class VoltHeaders
{
    public static readonly byte[] IfNoneMatchName = "If-None-Match"u8.ToArray();
    public static readonly byte[] RefererName = "Referer"u8.ToArray();
    public static readonly byte[] HostName = "Host"u8.ToArray();
    public static readonly byte[] ConnectionName = "Connection"u8.ToArray();
    public static readonly byte[] ContentTypeName = "Content-Type"u8.ToArray();
    public static readonly byte[] ContentLengthName = "Content-Length"u8.ToArray();

    /// <summary>Case-insensitive ASCII compare of two header names.</summary>
    public static bool NameEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            byte x = a[i], y = b[i];
            if (x is >= (byte)'A' and <= (byte)'Z') x += 32;
            if (y is >= (byte)'A' and <= (byte)'Z') y += 32;
            if (x != y) return false;
        }
        return true;
    }
}
