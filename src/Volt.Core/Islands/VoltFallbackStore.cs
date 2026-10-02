using System.Security.Cryptography;

namespace Volt;

/// <summary>
/// Bounded store for no-JS fallback island state: after a form POST the server parks the
/// updated island state under a random token and redirects back with ?__v=token.
/// LRU-evicted, strictly capacity-bounded — the only server-side state in Volt.
/// </summary>
public sealed class VoltFallbackStore
{
    private readonly int _capacity;
    private readonly TimeSpan _ttl;

    private sealed record Entry(string Island, string Sid, byte[] State, long CreatedAt);

    private readonly Dictionary<string, Entry> _map = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _lock = new();

    /// <param name="capacity">Max parked entries (LRU).</param>
    /// <param name="ttl">Entry lifetime; null = default 10 minutes.</param>
    public VoltFallbackStore(int capacity = 1024, TimeSpan? ttl = null)
    {
        _capacity = Math.Max(1, capacity);
        _ttl = ttl ?? TimeSpan.FromMinutes(10);
    }

    /// <summary>True when the token is a 32-char uppercase hex string (validation helper).</summary>
    public static bool IsHexToken(string? token)
        => token is not null
           && token.Length == 32
           && System.Text.RegularExpressions.Regex.IsMatch(token, "^[0-9A-F]+$");

    /// <summary>Parks updated island state and returns the one-time URL token.</summary>
    public string Put(string island, string sid, byte[] state)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        lock (_lock)
        {
            EvictExpired(Environment.TickCount64);
            _map[token] = new Entry(island, sid, state, Environment.TickCount64);
            _order.Enqueue(token);
            while (_map.Count > _capacity && _order.TryDequeue(out var oldest))
                _map.Remove(oldest);
        }
        return token;
    }

    /// <summary>Returns the parked state if the token is live and matches this island+sid.</summary>
    public byte[]? Get(string? token, string island, string sid)
    {
        if (token is null) return null;
        lock (_lock)
        {
            if (!_map.TryGetValue(token, out var e)) return null;
            if (e.Island != island || e.Sid != sid) return null;
            if (Environment.TickCount64 - e.CreatedAt > _ttl.TotalMilliseconds)
            {
                _map.Remove(token);
                return null;
            }
            _map.Remove(token); // one-time use
            return e.State;
        }
    }

    private void EvictExpired(long now)
    {
        while (_order.TryPeek(out var oldest) && _map.TryGetValue(oldest, out var e))
        {
            if (now - e.CreatedAt <= _ttl.TotalMilliseconds) break;
            _order.Dequeue();
            _map.Remove(oldest);
        }
    }
}
