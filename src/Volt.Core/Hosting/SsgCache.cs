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
        public string[] Tags = Array.Empty<string>(); // M6: revalidation tags (@tag)
    }

    private readonly ConcurrentDictionary<string, Entry> _map = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _lock = new();
    // M6: tag → paths (tag index for on-demand revalidation)
    private readonly ConcurrentDictionary<string, HashSet<string>> _tags = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private readonly ISsgCacheBackend? _backend;
    private readonly string? _identity;

    public SsgCache(int capacity, ISsgCacheBackend? backend = null, string? identity = null)
    {
        _capacity = Math.Max(1, capacity);
        _backend = backend;
        _identity = identity;
    }

    /// <summary>Identity used to pick the shared static instance (capacity + backend identity).</summary>
    public string? Identity => _identity;

    public int Capacity => _capacity;

    public bool TryGet(string path, out byte[] html, out string etag, out bool fresh, out int revalidateSeconds)
    {
        html = Array.Empty<byte>();
        etag = "";
        fresh = false;
        revalidateSeconds = 0;
        if (!_map.TryGetValue(path, out var entry))
        {
            // local miss: consult the shared/persistent backend and promote into the LRU
            if (_backend is null) return false;
            if (!_backend.TryLoad(path, out var storedHtml, out var storedEtag, out var storedTicks, out var storedRevalidate))
                return false;
            entry = new Entry
            {
                Html = storedHtml,
                ETag = storedEtag,
                RenderedAtUtcTicks = storedTicks,
                RevalidateSeconds = storedRevalidate,
            };
            Store(path, storedHtml, storedEtag, storedRevalidate, writeThrough: false, ticks: storedTicks);
            html = entry.Html;
            etag = entry.ETag;
            revalidateSeconds = entry.RevalidateSeconds;
            fresh = entry.RevalidateSeconds <= 0
                || (DateTime.UtcNow.Ticks - entry.RenderedAtUtcTicks) / TimeSpan.TicksPerSecond < entry.RevalidateSeconds;
            return true;
        }
        html = entry.Html;
        etag = entry.ETag;
        revalidateSeconds = entry.RevalidateSeconds;
        fresh = entry.RevalidateSeconds <= 0
            || (DateTime.UtcNow.Ticks - entry.RenderedAtUtcTicks) / TimeSpan.TicksPerSecond < entry.RevalidateSeconds;
        return true;
    }

    public void Store(string path, byte[] html, string etag, int revalidateSeconds, string[]? tags = null)
        => Store(path, html, etag, revalidateSeconds, writeThrough: true, ticks: DateTime.UtcNow.Ticks, tags);

    private void Store(string path, byte[] html, string etag, int revalidateSeconds, bool writeThrough, long ticks, string[]? tags = null)
    {
        var entry = new Entry
        {
            Html = html,
            ETag = etag,
            RenderedAtUtcTicks = ticks,
            RevalidateSeconds = revalidateSeconds,
            Tags = tags ?? Array.Empty<string>(),
        };
        lock (_lock)
        {
            _map[path] = entry;
            _order.Enqueue(path);
            while (_map.Count > _capacity && _order.TryDequeue(out var oldest))
                _map.TryRemove(oldest, out _);

            // M6: maintain the tag index under the same lock (single-writer ordering)
            foreach (var tag in entry.Tags)
            {
                var set = _tags.GetOrAdd(tag, _ => new HashSet<string>(StringComparer.Ordinal));
                set.Add(path);
            }
        }
        if (writeThrough && _backend is not null)
        {
            try { _backend.Save(path, html, etag, ticks, revalidateSeconds); }
            catch (IOException) { /* shared storage hiccup: local LRU still serves */ }
        }
    }

    /// <summary>
    /// M6: on-demand revalidation — evicts every cached entry carrying <paramref name="tag"/>.
    /// The next request re-renders those pages (and the store refills). Returns the evicted count.
    /// </summary>
    public int RevalidateTag(string tag)
    {
        if (!_tags.TryGetValue(tag, out var paths)) return 0;
        int evicted = 0;
        lock (_lock)
        {
            foreach (var path in paths)
                if (_map.TryRemove(path, out _))
                    evicted++;
            paths.Clear();
        }
        return evicted;
    }

    /// <summary>Single-flight claim for background re-render; returns true when this caller should refresh.</summary>
    public bool TryBeginRefresh(string path)
    {
        if (!_map.TryGetValue(path, out var entry)) return false;
        if (Interlocked.CompareExchange(ref entry.Refreshing, 1, 0) != 0) return false;
        return true;
    }

    public void EndRefresh(string path, byte[] html, string etag, int revalidateSeconds, string[]? tags = null)
    {
        Store(path, html, etag, revalidateSeconds, tags);
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
