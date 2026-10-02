using System.Buffers;
using System.Net.Sockets;
using System.Text;

namespace Volt.Server;

/// <summary>
/// A single keep-alive HTTP/1.1 connection. Owns a pooled receive buffer, a pooled
/// response writer and a pooled VoltHttpContext — all reused across requests on the
/// connection. The per-request hot path (parse + route match + cached SSG serve)
/// performs ZERO heap allocations.
/// </summary>
internal sealed class VoltConnection : IDisposable
{
    private const int RecvBufferInitial = 16 * 1024;
    private const int MaxHeadBytes = 64 * 1024;
    private const long MaxBodyBytes = 8 * 1024 * 1024;

    private static readonly byte[] ContinueBytes = "HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray();

    private Socket _socket = null!;
    private Stream _stream = null!;
    private byte[] _recvBuffer = null!;
    private int _recvLength;
    private readonly HeaderPosition[] _positions = new HeaderPosition[96];
    private int _headerCount;
    private readonly PooledBufferWriter _headers = new();
    private readonly PooledBufferWriter _body = new();
    private readonly VoltHttpContext _vctx = new();
    private readonly HeaderSource _headerSource;
    private readonly VoltOptions _options;
    private readonly VoltServer _server;

    internal VoltConnection(VoltServer server, VoltOptions options)
    {
        _server = server;
        _options = options;
        _headerSource = new HeaderSource(this);
    }

    private void Attach(Socket socket, Stream stream)
    {
        _socket = socket;
        _stream = stream;
        _recvBuffer ??= ArrayPool<byte>.Shared.Rent(RecvBufferInitial);
        _recvLength = 0;
        _headerCount = 0;
        _vctx.Reset();
    }

    public async Task RunAsync(Socket socket, Stream stream)
    {
        Attach(socket, stream);
        try
        {
            socket.NoDelay = true;
            bool keepAlive = true;
            while (keepAlive)
            {
                keepAlive = await ProcessOneRequestAsync();
            }
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
        catch (Exception)
        {
            // last-resort error page (the engine handles page-level errors)
            TrySendImmediate("HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8);
        }
        finally
        {
            CloseSocket();
            _server.Return(this);
        }
    }

    /// <summary>Reads + serves exactly one request. Returns false when the connection should close.</summary>
    private async Task<bool> ProcessOneRequestAsync()
    {
        // ---- read the request head (no parsed state alive across these awaits) ----
        int headEnd = Http1.FindHeadEnd(_recvBuffer.AsSpan(0, _recvLength));
        while (headEnd < 0)
        {
            if (_recvLength >= MaxHeadBytes) { await SendSimpleAsync(431, "Request Header Fields Too Large"); return false; }
            if (_recvLength == _recvBuffer.Length) GrowRecvBuffer(_recvLength + 1);
            int n = await _stream.ReadAsync(_recvBuffer.AsMemory(_recvLength));
            if (n == 0) return false; // peer closed
            _recvLength += n;
            headEnd = Http1.FindHeadEnd(_recvBuffer.AsSpan(0, _recvLength));
        }

        // ---- parse (synchronous; scalar state) ---------------------------------
        ParsedRequest parsed = default;
        if (!Http1Parser.TryParse(_recvBuffer.AsSpan(0, headEnd - 4), ref parsed, _positions))
        {
            await SendSimpleAsync(400, "Bad Request");
            return false;
        }
        _headerCount = parsed.HeaderCount;

        // ---- body ---------------------------------------------------------------
        if (parsed.ContentLength > MaxBodyBytes) { await SendSimpleAsync(413, "Payload Too Large"); return false; }
        if (parsed.ContentLength > 0 && parsed.Expect100)
        {
            await _stream.WriteAsync(ContinueBytes);
        }
        int bodyStart = headEnd;
        if (parsed.ContentLength > 0)
        {
            long totalNeeded = bodyStart + parsed.ContentLength;
            while (_recvLength < totalNeeded)
            {
                if (totalNeeded > _recvBuffer.Length) GrowRecvBuffer((int)totalNeeded);
                int n = await _stream.ReadAsync(_recvBuffer.AsMemory(_recvLength));
                if (n == 0) return false;
                _recvLength += n;
            }
        }
        int bodyEnd = bodyStart + (int)Math.Max(0, parsed.ContentLength);

        // ---- fast path: zero-alloc serving (GET/HEAD only; spans die before any await) ----
        bool served = (parsed.IsGet || parsed.IsHead) && TryFastPath(parsed);

        // ---- full async pipeline -------------------------------------------------
        if (!served)
        {
            var buffer = _recvBuffer.AsSpan();
            _vctx.Reset();
            _vctx.Method = Encoding.UTF8.GetString(buffer.Slice(parsed.MethodStart, parsed.MethodLength));
            _vctx.Path = Encoding.UTF8.GetString(buffer.Slice(parsed.TargetStart, parsed.TargetLength));
            _vctx.Query = parsed.QueryStart < 0
                ? null
                : Encoding.UTF8.GetString(buffer.Slice(parsed.QueryStart, parsed.QueryLength));
            if (parsed.ContentLength > 0)
                _vctx.BodyMemory = _recvBuffer.AsMemory(bodyStart, (int)parsed.ContentLength);
            _vctx.Headers = _headerSource;
            _vctx.Output = _body;

            await VoltEngine.HandleAsync(_vctx, _options);
        }

        await WriteResponseAsync(parsed.KeepAlive, parsed.IsHead);

        // ---- leftover bytes (pipelining): shift to the front ---------------------
        int leftover = _recvLength - bodyEnd;
        if (leftover > 0)
            Array.Copy(_recvBuffer, bodyEnd, _recvBuffer, 0, leftover);
        _recvLength = leftover;

        return parsed.KeepAlive && _vctx.StatusCode < 500;
    }

    /// <summary>Zero-allocation fast path: stack-decoded path + cached SSG / SEO / hydrate serving.</summary>
    private bool TryFastPath(in ParsedRequest parsed)
    {
        var buffer = _recvBuffer.AsSpan();
        var rawPath = buffer.Slice(parsed.TargetStart, parsed.TargetLength);

        Span<byte> decodeBuffer = stackalloc byte[512];
        int pathLength = Http1.DecodeTarget(rawPath, decodeBuffer);
        if (pathLength < 0) return false;

        Span<char> pathBuffer = stackalloc char[512];
        if (pathLength > pathBuffer.Length) return false;
        int charCount = Encoding.UTF8.GetChars(decodeBuffer[..pathLength], pathBuffer);

        int queryLength = 0;
        Span<char> queryBuffer = stackalloc char[512];
        if (parsed.QueryStart >= 0)
        {
            int qd = Http1.DecodeTarget(buffer.Slice(parsed.QueryStart, parsed.QueryLength), decodeBuffer);
            if (qd < 0) return false;
            queryLength = Encoding.UTF8.GetChars(decodeBuffer[..qd], queryBuffer);
        }

        _vctx.Reset();
        _vctx.Output = _body;
        return VoltEngine.TryServeFast(pathBuffer[..charCount], queryBuffer[..queryLength],
            _headerSource, _vctx, _options);
    }

    private void GrowRecvBuffer(int minSize)
    {
        int newSize = _recvBuffer.Length;
        while (newSize < minSize) newSize *= 2;
        var bigger = ArrayPool<byte>.Shared.Rent(newSize);
        Array.Copy(_recvBuffer, bigger, _recvLength);
        ArrayPool<byte>.Shared.Return(_recvBuffer);
        _recvBuffer = bigger;
    }

    // ------------------------------------------------------------------
    // response framing: the engine already wrote the body into _body;
    // framing headers go into _headers and are sent first.
    // ------------------------------------------------------------------

    private async Task WriteResponseAsync(bool keepAlive, bool isHead)
    {
        _headers.Reset();
        long contentLength = _vctx.HasBody ? Math.Max(_vctx.ContentLength, _body.WrittenCount) : 0;

        WriteStatusLine(_headers, _vctx.StatusCode);
        WriteCommonHeaders(_headers, _vctx, keepAlive, contentLength);
        var terminator = _headers.GetSpan(2);
        terminator[0] = (byte)'\r';
        terminator[1] = (byte)'\n';
        _headers.Advance(2);

        await _stream.WriteAsync(_headers.WrittenMemory);

        if (!isHead && _vctx.HasBody)
        {
            await _stream.WriteAsync(_body.WrittenMemory);
        }
        _body.Reset();
    }

    internal static void WriteStatusLine(IBufferWriter<byte> writer, int statusCode)
    {
        var span = writer.GetSpan(64);
        int pos = 0;
        "HTTP/1.1 "u8.CopyTo(span); pos += 9;
        pos = WriteInt(span, pos, statusCode);
        span[pos++] = (byte)' ';
        var reason = ReasonPhrase(statusCode);
        reason.CopyTo(span[pos..]);
        pos += reason.Length;
        span[pos++] = (byte)'\r';
        span[pos++] = (byte)'\n';
        writer.Advance(pos);
    }

    private static void WriteCommonHeaders(IBufferWriter<byte> writer, VoltHttpContext ctx, bool keepAlive, long contentLength)
    {
        if (ctx.ContentType is not null) WriteHeader(writer, "Content-Type"u8, ctx.ContentType);
        if (ctx.ETag is not null) WriteHeader(writer, "ETag"u8, ctx.ETag);
        if (ctx.CacheControl is not null) WriteHeader(writer, "Cache-Control"u8, ctx.CacheControl);
        if (ctx.Allow is not null) WriteHeader(writer, "Allow"u8, ctx.Allow);
        if (ctx.Location is not null) WriteHeader(writer, "Location"u8, ctx.Location);
        WriteContentLength(writer, contentLength);
        WriteHeader(writer, "Connection"u8, keepAlive ? "keep-alive" : "close");
    }

    private static void WriteContentLength(IBufferWriter<byte> writer, long contentLength)
    {
        var span = writer.GetSpan(32);
        "Content-Length: "u8.CopyTo(span);
        int pos = "Content-Length: "u8.Length;
        pos = WriteInt(span, pos, Math.Max(0, contentLength));
        span[pos++] = (byte)'\r';
        span[pos++] = (byte)'\n';
        writer.Advance(pos);
    }

    internal static int WriteInt(Span<byte> span, int pos, long value)
    {
        if (value == 0) { span[pos] = (byte)'0'; return pos + 1; }
        int digits = 1;
        long v = value;
        while (v >= 10) { v /= 10; digits++; }
        pos += digits;
        int p = pos - 1;
        v = value;
        while (v > 0) { span[p--] = (byte)('0' + v % 10); v /= 10; }
        return pos;
    }

    /// <summary>Writes "name: value\r\n" (Volt's response header values are ASCII).</summary>
    private static void WriteHeader(IBufferWriter<byte> writer, ReadOnlySpan<byte> name, ReadOnlySpan<char> value)
    {
        var span = writer.GetSpan(name.Length + value.Length + 4);
        name.CopyTo(span);
        int pos = name.Length;
        span[pos++] = (byte)':';
        span[pos++] = (byte)' ';
        foreach (var c in value)
            span[pos++] = c < 128 ? (byte)c : (byte)'?';
        span[pos++] = (byte)'\r';
        span[pos++] = (byte)'\n';
        writer.Advance(pos);
    }

    private static ReadOnlySpan<byte> ReasonPhrase(int statusCode) => statusCode switch
    {
        200 => "OK"u8,
        204 => "No Content"u8,
        304 => "Not Modified"u8,
        303 => "See Other"u8,
        400 => "Bad Request"u8,
        404 => "Not Found"u8,
        405 => "Method Not Allowed"u8,
        413 => "Payload Too Large"u8,
        431 => "Request Header Fields Too Large"u8,
        500 => "Internal Server Error"u8,
        _ => "OK"u8,
    };

    private async Task SendSimpleAsync(int statusCode, string reason)
    {
        _headers.Reset();
        var span = _headers.GetSpan(160);
        int pos = 0;
        "HTTP/1.1 "u8.CopyTo(span); pos += 9;
        pos = WriteInt(span, pos, statusCode);
        span[pos++] = (byte)' ';
        pos += Encoding.ASCII.GetBytes(reason, span[pos..]);
        "Content-Length: 0\r\nConnection: close\r\n\r\n"u8.CopyTo(span[pos..]);
        pos += "Content-Length: 0\r\nConnection: close\r\n\r\n"u8.Length;
        _headers.Advance(pos);
        try { await _stream.WriteAsync(_headers.WrittenMemory); }
        catch (SocketException) { }
    }

    private void TrySendImmediate(ReadOnlySpan<byte> bytes)
    {
        try
        {
            if (_socket.Connected)
                _stream.Write(bytes.ToArray());
        }
        catch { }
    }

    private void CloseSocket()
    {
        try { _stream.Dispose(); } catch { }
        try { _socket.Dispose(); } catch { }
    }

    public void Dispose()
    {
        if (_recvBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_recvBuffer);
            _recvBuffer = null!;
        }
        _headers.Dispose();
        _body.Dispose();
    }

    /// <summary>
    /// Header access over the connection's parsed positions + receive buffer.
    /// One instance per connection, reused — zero allocation per request.
    /// Valid for the whole request: the receive buffer is not overwritten until
    /// the response has been sent (and pipelined leftovers are shifted only after).
    /// </summary>
    internal sealed class HeaderSource : IVoltHeaderSource
    {
        private readonly VoltConnection _c;
        public HeaderSource(VoltConnection c) => _c = c;

        public bool TryGetHeader(ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value)
            => Http1Parser.TryGetHeader(_c._recvBuffer, _c._positions, _c._headerCount, name, out value);
    }
}
