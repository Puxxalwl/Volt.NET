using System.Text;

namespace Volt;

/// <summary>
/// Route parameters as integer offsets into the request path — zero allocation until decoded.
/// Valid only while the request path buffer is alive and the params stay on the render stack.
/// </summary>
public ref struct RouteParams
{
    private ReadOnlySpan<char> _path;
    private ReadOnlySpan<RawParam> _raw;
    private string[] _names;

    internal RouteParams(ReadOnlySpan<char> path, ReadOnlySpan<RawParam> raw, string[] names)
    {
        _path = path;
        _raw = raw;
        _names = names;
    }

    internal static RouteParams Empty => default;

    public int Count => _raw.Length;

    /// <summary>Parameter name by index (from the route pattern).</summary>
    public string Name(int index) => _names[_raw[index].NameIndex];

    /// <summary>Raw (undecoded) parameter value by index.</summary>
    public ReadOnlySpan<char> RawValue(int index) => _path.Slice(_raw[index].ValueStart, _raw[index].ValueLength);

    /// <summary>Raw (undecoded) parameter value by name.</summary>
    public bool TryGetRaw(ReadOnlySpan<char> name, out ReadOnlySpan<char> value)
    {
        for (int i = 0; i < _raw.Length; i++)
        {
            if (_names[_raw[i].NameIndex].AsSpan().SequenceEqual(name))
            {
                value = _path.Slice(_raw[i].ValueStart, _raw[i].ValueLength);
                return true;
            }
        }
        value = default;
        return false;
    }

    /// <summary>URL-decoded parameter value (allocates a string).</summary>
    public string? Get(ReadOnlySpan<char> name)
    {
        return TryGetRaw(name, out var raw) ? UrlDecoder.Decode(raw) : null;
    }

    public string? Get(string name) => Get(name.AsSpan());
}

/// <summary>A matched parameter: index into the pattern's param-name array + value range in the path.</summary>
public readonly struct RawParam
{
    public readonly int NameIndex;
    public readonly int ValueStart;
    public readonly int ValueLength;

    public RawParam(int nameIndex, int valueStart, int valueLength)
    {
        NameIndex = nameIndex;
        ValueStart = valueStart;
        ValueLength = valueLength;
    }
}

/// <summary>Minimal span-based percent-decoder (percent-escapes are UTF-8 bytes; '+' is space).</summary>
public static class UrlDecoder
{
    public static string Decode(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty) return string.Empty;
        if (s.IndexOf('%') < 0 && s.IndexOf('+') < 0) return s.ToString();

        // worst case: every char is a literal non-ASCII (up to 3 UTF-8 bytes each)
        var maxBytes = s.Length * 3;
        if (maxBytes <= 1024)
        {
            Span<byte> stackBuf = stackalloc byte[1024];
            return Encoding.UTF8.GetString(DecodeBytesInto(s, stackBuf));
        }
        byte[] rented = new byte[maxBytes];
        return Encoding.UTF8.GetString(DecodeBytesInto(s, rented));
    }

    private static ReadOnlySpan<byte> DecodeBytesInto(ReadOnlySpan<char> s, Span<byte> buf)
    {
        int bi = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '%' && i + 2 < s.Length)
            {
                int hi = HexValue(s[i + 1]);
                int lo = HexValue(s[i + 2]);
                if (hi >= 0 && lo >= 0)
                {
                    buf[bi++] = (byte)((hi << 4) | lo);
                    i += 2;
                    continue;
                }
            }
            if (c == '+')
            {
                buf[bi++] = (byte)' ';
                continue;
            }
            // literal char → UTF-8 bytes (no stackalloc in loop — CA2014)
            if (c < 0x80)
            {
                buf[bi++] = (byte)c;
            }
            else if (c < 0x800)
            {
                buf[bi++] = (byte)(0xC0 | (c >> 6));
                buf[bi++] = (byte)(0x80 | (c & 0x3F));
            }
            else
            {
                buf[bi++] = (byte)(0xE0 | (c >> 12));
                buf[bi++] = (byte)(0x80 | ((c >> 6) & 0x3F));
                buf[bi++] = (byte)(0x80 | (c & 0x3F));
            }
        }
        return buf[..bi];
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
