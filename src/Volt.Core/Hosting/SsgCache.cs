using System.Collections.Concurrent;

namespace Volt;

/// <summary>
/// Bounded in-memory output cache for SSG pages with stale-while-revalidate (ISR).
/// Key: canonical request path. Capacity-bounded LRU — memory stays flat.
/// </summary>
internal sealed class SsgCache
{
    private sealed class Entry
    {
        public required byte[] Html;
        public required string ETag;
        public long RenderedAtUtcTicks;
        public int RevalidateSeconds;
        public int Refreshing; // 0/1 single-flight flag for background re-render
    }

    private readonly ConcurrentDictionary<string, Entry> _map = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _lock = new();
    private readonly int _capacity;

    public SsgCache(int capacity) => _capacity = Math.Max(1, capacity);

    public int Capacity => _capacity;

    public bool TryGet(string path, out byte[] html, out string etag, out bool fresh, out int revalidateSeconds)
    {
        html = Array.Empty<byte>();
        etag = "";
        fresh = false;
        revalidateSeconds = 0;
        if (!_map.TryGetValue(path, out var entry)) return false;
        html = entry.Html;
        etag = entry.ETag;
        revalidateSeconds = entry.RevalidateSeconds;
        fresh = entry.RevalidateSeconds <= 0
            || (DateTime.UtcNow.Ticks - entry.RenderedAtUtcTicks) / TimeSpan.TicksPerSecond < entry.RevalidateSeconds;
        return true;
    }

    public void Store(string path, byte[] html, string etag, int revalidateSeconds)
    {
        var entry = new Entry
        {
            Html = html,
            ETag = etag,
            RenderedAtUtcTicks = DateTime.UtcNow.Ticks,
            RevalidateSeconds = revalidateSeconds,
        };
        lock (_lock)
        {
            _map[path] = entry;
            _order.Enqueue(path);
            while (_map.Count > _capacity && _order.TryDequeue(out var oldest))
                _map.TryRemove(oldest, out _);
        }
    }

    /// <summary>Single-flight claim for background re-render; returns true when this caller should refresh.</summary>
    public bool TryBeginRefresh(string path)
    {
        if (!_map.TryGetValue(path, out var entry)) return false;
        if (Interlocked.CompareExchange(ref entry.Refreshing, 1, 0) != 0) return false;
        return true;
    }

    public void EndRefresh(string path, byte[] html, string etag, int revalidateSeconds)
    {
        Store(path, html, etag, revalidateSeconds);
        if (_map.TryGetValue(path, out var entry))
            Interlocked.Exchange(ref entry.Refreshing, 0);
    }

    public static string ComputeETag(ReadOnlySpan<byte> html)
    {
        ulong hash = 14695981039346656037;
        foreach (byte b in html)
        {
            hash ^= b;
            hash *= 1099511628211;
        }
        return "\"" + hash.ToString("x16") + "\"";
    }
}
