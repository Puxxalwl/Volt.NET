using System.Text;

namespace Volt;

/// <summary>
/// M6: safe streaming HTML minifier. Single pass over bytes, in-place compaction
/// (output never grows), zero allocations. Rules are intentionally conservative —
/// layout is never affected:
/// <list type="bullet">
///   <item>runs of whitespace collapse to a single space (kills indentation/newlines)</item>
///   <item>attribute values and text are otherwise untouched (no quote/attr removal)</item>
///   <item>contents of pre/textarea/script/style are copied verbatim</item>
/// </list>
/// </summary>
public static class VoltHtmlMinifier
{
    private const byte Space = (byte)' ';

    /// <summary>Minifies <paramref name="src"/> into <paramref name="dst"/>; returns the written length. dst must be at least src.Length.</summary>
    public static int Minify(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int len = src.Length;
        int o = 0;
        int state = 0; // 0 normal · 1 inside tag · 2 inside quoted attribute value
        bool preserving = false; // inside pre/script/style/textarea content

        Span<byte> preserveName = stackalloc byte[8]; // lowercased name of the open preserving element
        int preserveNameLen = 0;
        byte quote = 0;

        for (int i = 0; i < len; i++)
        {
            byte c = src[i];

            if (state == 2) // quoted attribute value — verbatim
            {
                dst[o++] = c;
                if (c == quote) state = 1;
                continue;
            }

            if (state == 1) // inside a tag — verbatim, watch quotes and the tag end
            {
                dst[o++] = c;
                if (c == (byte)'\'' || c == (byte)'"') { quote = c; state = 2; }
                else if (c == (byte)'>') state = 0;
                continue;
            }

            if (preserving) // pre/script/style/textarea content — verbatim, watch its close tag
            {
                dst[o++] = c;
                if (c == (byte)'<' && i + 1 < len && src[i + 1] == (byte)'/'
                    && i + 2 < len && IsNameStart(src[i + 2]))
                {
                    int j = i + 2;
                    int nameStart = j;
                    while (j < len && IsNameChar(src[j])) j++;
                    int n = j - nameStart;
                    if (n == preserveNameLen && SameName(src, nameStart, preserveName, n))
                        preserving = false; // the close tag itself flows through state 1
                }
                continue;
            }

            // ---- normal markup: collapse whitespace runs to a single space ----
            if (IsWs(c))
            {
                while (i + 1 < len && IsWs(src[i + 1])) i++;
                dst[o++] = Space;
                continue;
            }

            if (c == (byte)'<' && i + 1 < len && (IsNameStart(src[i + 1]) || src[i + 1] == (byte)'!' || src[i + 1] == (byte)'/'))
            {
                // entering a tag: read the name to detect a preserving element open
                int j = i + 1;
                bool closing = src[j] == (byte)'/';
                if (closing) j++;
                int nameStart = j;
                while (j < len && IsNameChar(src[j])) j++;
                int n = j - nameStart;

                if (!closing && n is > 0 and <= 8 && IsPreservingName(src, nameStart, n))
                {
                    preserving = true;
                    preserveNameLen = n;
                    for (int k = 0; k < n; k++)
                        preserveName[k] = Lower(src[nameStart + k]);
                }
                state = 1;
                dst[o++] = c;
                continue;
            }

            dst[o++] = c;
        }
        return o;
    }

    private static bool IsWs(byte c) => c is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'\f';

    private static bool IsNameStart(byte c) => c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z';

    private static bool IsNameChar(byte c) => IsNameStart(c) || c is >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)':' or (byte)'_';

    private static byte Lower(byte c) => c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c + 32) : c;

    /// <summary>"pre"(3) · "style"(5) · "script"(6) · "textarea"(8) — ASCII case-insensitive.</summary>
    private static bool IsPreservingName(ReadOnlySpan<byte> src, int start, int len)
    {
        return len switch
        {
            3 => MatchesLower(src, start, "pre"),
            5 => MatchesLower(src, start, "style"),
            6 => MatchesLower(src, start, "script"),
            8 => MatchesLower(src, start, "textarea"),
            _ => false,
        };
    }

    private static bool MatchesLower(ReadOnlySpan<byte> src, int start, string lower)
    {
        for (int k = 0; k < lower.Length; k++)
            if (Lower(src[start + k]) != (byte)lower[k]) return false;
        return true;
    }

    private static bool SameName(ReadOnlySpan<byte> src, int start, ReadOnlySpan<byte> lowerName, int len)
    {
        for (int k = 0; k < len; k++)
            if (Lower(src[start + k]) != lowerName[k]) return false;
        return true;
    }
}
