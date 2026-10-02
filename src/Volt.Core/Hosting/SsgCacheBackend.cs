using System.Text;

namespace Volt;

/// <summary>
/// Shared/persistent SSG cache backend. The in-process LRU sits in front (see SsgCache):
/// on a local miss the backend is consulted; every store writes through. Used for
/// multi-node deployments (shared disk) and warm caches across restarts.
/// </summary>
public interface ISsgCacheBackend
{
    /// <summary>Loads a cached page (html + etag + render time + revalidate window).</summary>
    bool TryLoad(string path, out byte[] html, out string etag, out long renderedAtUtcTicks, out int revalidateSeconds);

    /// <summary>Persists a rendered page (atomic write).</summary>
    void Save(string path, byte[] html, string etag, long renderedAtUtcTicks, int revalidateSeconds);
}

/// <summary>
/// File-backed shared cache: one file per cached page, named by the FNV-1a hash of the
/// path. Safe for several processes on one machine (atomic tmp+move writes).
/// </summary>
public sealed class FileSsgCacheBackend : ISsgCacheBackend
{
    private readonly string _directory;
    private readonly object _saveLock = new();

    /// <param name="directory">Cache directory (created if missing).</param>
    public FileSsgCacheBackend(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public string CacheDirectory => _directory;

    public bool TryLoad(string path, out byte[] html, out string etag, out long renderedAtUtcTicks, out int revalidateSeconds)
    {
        html = Array.Empty<byte>();
        etag = "";
        renderedAtUtcTicks = 0;
        revalidateSeconds = 0;

        var file = Path.Combine(_directory, FileName(path));
        byte[] raw;
        try
        {
            if (!File.Exists(file)) return false;
            raw = File.ReadAllBytes(file);
        }
        catch (IOException)
        {
            return false; // transient (concurrent replace): local LRU or a re-render covers it
        }

        if (raw.Length < 20) return false;
        renderedAtUtcTicks = BitConverter.ToInt64(raw, 0);
        revalidateSeconds = BitConverter.ToInt32(raw, 8);
        int etagLength = BitConverter.ToInt32(raw, 12);
        int htmlLength = BitConverter.ToInt32(raw, 16);
        if (etagLength < 0 || htmlLength < 0 || 20 + etagLength + htmlLength > raw.Length) return false;

        etag = Encoding.ASCII.GetString(raw, 20, etagLength);
        html = new byte[htmlLength];
        Buffer.BlockCopy(raw, 20 + etagLength, html, 0, htmlLength);
        return htmlLength > 0;
    }

    public void Save(string path, byte[] html, string etag, long renderedAtUtcTicks, int intRevalidateSeconds)
    {
        var raw = new byte[20 + etag.Length + html.Length];
        BitConverter.TryWriteBytes(raw.AsSpan(0, 8), renderedAtUtcTicks);
        BitConverter.TryWriteBytes(raw.AsSpan(8, 4), intRevalidateSeconds);
        BitConverter.TryWriteBytes(raw.AsSpan(12, 4), etag.Length);
        BitConverter.TryWriteBytes(raw.AsSpan(16, 4), html.Length);
        Encoding.ASCII.GetBytes(etag, 0, etag.Length, raw, 20);
        Buffer.BlockCopy(html, 0, raw, 20 + etag.Length, html.Length);

        var file = Path.Combine(_directory, FileName(path));
        var tmp = file + ".tmp";
        lock (_saveLock)
        {
            File.WriteAllBytes(tmp, raw);
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
        }
    }

    private static string FileName(string path) => Fnv1a64(path).ToString("x16") + ".volt";

    private static ulong Fnv1a64(string text)
    {
        var bytes = new byte[Encoding.UTF8.GetMaxByteCount(text.Length)];
        int n = Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, 0);
        return Fnv1a64(bytes.AsSpan(0, n));
    }

    internal static ulong Fnv1a64(ReadOnlySpan<byte> data)
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
