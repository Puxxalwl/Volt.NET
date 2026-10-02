using System.Buffers;
using System.Runtime.CompilerServices;

namespace Volt.Server;

/// <summary>
/// Zero-allocation HTTP/1.1 request parser. Slices the request line and headers
/// directly into the connection's receive buffer; all parsed state is plain scalars
/// (offsets + flags) so it can live across awaits. Header positions are stored in a
/// reusable array — lookups compare UTF-8 bytes in place, no strings.
/// </summary>
internal struct ParsedRequest
{
    public int MethodStart, MethodLength;
    public int TargetStart, TargetLength;       // raw path (no query)
    public int QueryStart, QueryLength;         // -1/0 when no query
    public long ContentLength;                  // -1: absent
    public bool KeepAlive;
    public bool Expect100;
    public bool IsHead;
    public bool IsGet;
    public int HeaderCount;
}

internal readonly record struct HeaderPosition(int NameStart, int NameLength, int ValueStart, int ValueLength);

internal static class Http1Parser
{
    /// <summary>
    /// Parses the request head (request line + headers). <paramref name="head"/> is the
    /// buffer content from index 0 up to (not including) the blank line's CRLF.
    /// Fills <paramref name="positions"/> with header offsets for later lookups.
    /// Returns false on malformed input.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> head, ref ParsedRequest parsed, HeaderPosition[] positions)
    {
        parsed = default;

        // request line: METHOD SP TARGET SP VERSION
        int lineEnd = head.IndexOf(Crlf);
        if (lineEnd < 0) return false;
        var requestLine = head[..lineEnd];
        int sp1 = requestLine.IndexOf((byte)' ');
        if (sp1 <= 0) return false;
        int sp2 = requestLine.LastIndexOf((byte)' ');
        if (sp2 <= sp1) return false;

        parsed.MethodStart = 0;
        parsed.MethodLength = sp1;

        var method = requestLine[..sp1];
        parsed.IsHead = method.SequenceEqual("HEAD"u8);
        parsed.IsGet = method.SequenceEqual("GET"u8);

        var target = requestLine[(sp1 + 1)..sp2];
        parsed.TargetStart = sp1 + 1;
        parsed.TargetLength = target.Length;

        var version = requestLine[(sp2 + 1)..];
        if (!version.SequenceEqual("HTTP/1.1"u8) && !version.SequenceEqual("HTTP/1.0"u8))
            return false;

        bool keepAlive = version.Length == 8; // 1.1 defaults to keep-alive
        long contentLength = -1;
        bool expect100 = false;

        // query split (offsets relative to the head span)
        int q = target.IndexOf((byte)'?');
        if (q < 0)
        {
            parsed.QueryStart = -1;
            parsed.QueryLength = 0;
        }
        else
        {
            parsed.QueryStart = parsed.TargetStart + q + 1;
            parsed.QueryLength = target.Length - q - 1;
            parsed.TargetLength = q;
        }

        // headers
        int pos = lineEnd + 2;
        int positionCount = 0;
        while (pos < head.Length)
        {
            int end = head[pos..].IndexOf(Crlf);
            var line = end < 0 ? head[pos..] : head[pos..(pos + end)];
            if (line.Length == 0) return false;
            int colon = line.IndexOf((byte)':');
            if (colon <= 0) return false;

            var name = line[..colon];
            var value = line[(colon + 1)..].TrimStart((byte)' ');

            if (positionCount < positions.Length)
            {
                positions[positionCount] = new HeaderPosition(pos, name.Length, pos + colon + 2, value.Length);
                positionCount++;
            }

            if (EqualsIgnoreCase(name, "Content-Length"u8))
            {
                contentLength = ParseContentLength(value);
                if (contentLength < 0) return false;
            }
            else if (EqualsIgnoreCase(name, "Connection"u8))
            {
                if (ContainsIgnoreCase(value, "keep-alive"u8)) keepAlive = true;
                else if (ContainsIgnoreCase(value, "close"u8)) keepAlive = false;
            }
            else if (EqualsIgnoreCase(name, "Expect"u8))
            {
                if (ContainsIgnoreCase(value, "100-continue"u8)) expect100 = true;
            }

            pos += end < 0 ? line.Length : end + 2;
        }

        parsed.KeepAlive = keepAlive;
        parsed.ContentLength = contentLength;
        parsed.Expect100 = expect100;
        parsed.HeaderCount = positionCount;
        return parsed.MethodLength is > 0 and <= 16;
    }

    /// <summary>Case-insensitive header lookup over stored positions (buffer-relative).</summary>
    public static bool TryGetHeader(ReadOnlySpan<byte> buffer, HeaderPosition[] positions, int count,
        ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value)
    {
        for (int i = 0; i < count; i++)
        {
            var p = positions[i];
            var actual = buffer.Slice(p.NameStart, p.NameLength);
            if (actual.Length == name.Length && EqualsIgnoreCase(actual, name))
            {
                value = buffer.Slice(p.ValueStart, p.ValueLength);
                return true;
            }
        }
        value = default;
        return false;
    }

    private static long ParseContentLength(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty) return -1;
        long result = 0;
        foreach (var b in value)
        {
            if (b is < (byte)'0' or > (byte)'9') return -1;
            result = result * 10 + (b - (byte)'0');
            if (result > 128 * 1024 * 1024) return -1; // 128 MB cap
        }
        return result;
    }

    private static bool ContainsIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length > haystack.Length) return false;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (EqualsIgnoreCase(haystack.Slice(i, needle.Length), needle)) return true;
        }
        return false;
    }

    private static bool EqualsIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            byte x = Lower(a[i]), y = Lower(b[i]);
            if (x != y) return false;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Lower(byte c) => c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c + 32) : c;

    private static ReadOnlySpan<byte> Crlf => "\r\n"u8;
}

/// <summary>Span utilities shared by the server.</summary>
internal static class Http1
{
    /// <summary>Index just after the request head terminator (\r\n\r\n), or -1 when incomplete.</summary>
    public static int FindHeadEnd(ReadOnlySpan<byte> buffer)
    {
        int idx = buffer.IndexOf(HeadTerminator);
        return idx < 0 ? -1 : idx + HeadTerminator.Length;
    }

    private static ReadOnlySpan<byte> HeadTerminator => "\r\n\r\n"u8;

    /// <summary>
    /// Percent-decodes the raw target into a UTF-8 byte span (stack), so callers can
    /// convert to chars. '+' becomes space (query convention). Returns -1 when the
    /// decoded path does not fit the destination.
    /// </summary>
    public static int DecodeTarget(ReadOnlySpan<byte> raw, Span<byte> destination)
    {
        int di = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            byte b = raw[i];
            if (b == (byte)'%' && i + 2 < raw.Length)
            {
                int hi = HexValue(raw[i + 1]);
                int lo = HexValue(raw[i + 2]);
                if (hi >= 0 && lo >= 0)
                {
                    if (di >= destination.Length) return -1;
                    destination[di++] = (byte)((hi << 4) | lo);
                    i += 2;
                    continue;
                }
            }
            if (di >= destination.Length) return -1;
            destination[di++] = b == (byte)'+' ? (byte)' ' : b;
        }
        return di;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int HexValue(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => c - (byte)'a' + 10,
        >= (byte)'A' and <= (byte)'F' => c - (byte)'A' + 10,
        _ => -1,
    };
}
