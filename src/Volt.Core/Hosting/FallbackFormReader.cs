namespace Volt;

/// <summary>
/// Minimal urlencoded form reader for the no-JS fallback POST (no ASP.NET form feature — AOT-safe, allocation-light).
/// </summary>
internal static class FallbackFormReader
{
    /// <summary>Parses "a=1&amp;b=2" into pairs (URL-decoded keys and values).</summary>
    public static List<(string Name, string Value)> Parse(ReadOnlySpan<byte> utf8Body)
    {
        var result = new List<(string, string)>(8);
        var body = System.Text.Encoding.UTF8.GetString(utf8Body);
        foreach (var pairRaw in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pairRaw.IndexOf('=');
            if (eq < 0)
            {
                result.Add((UrlDecoder.Decode(pairRaw), ""));
                continue;
            }
            var name = UrlDecoder.Decode(pairRaw[..eq]);
            var value = UrlDecoder.Decode(pairRaw[(eq + 1)..]);
            result.Add((name, value));
        }
        return result;
    }
}
