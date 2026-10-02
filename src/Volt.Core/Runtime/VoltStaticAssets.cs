using System.Reflection;
using System.Text;

namespace Volt;

/// <summary>
/// Small static-asset store (wwwroot) loaded into memory at host startup.
/// The built-in server serves these with zero allocations; Kestrel keeps its
/// UseStaticFiles middleware (this store also feeds the dist bundling).
/// Bounded: files up to 1 MB, at most 256 files — sites should use a CDN for more.
/// </summary>
public static class VoltStaticAssets
{
    private const long MaxFileBytes = 1 * 1024 * 1024;
    private const int MaxFiles = 256;

    private static volatile Asset[]? _assets;
    private static string? _root;

    internal sealed record Asset(string Name, byte[] Bytes, string ETag, string ContentType);

    private const string EmbeddedPrefix = "volt.static.";

    /// <summary>
    /// Loads wwwroot from <paramref name="contentRoot"/> (default: AppContext.BaseDirectory);
    /// when the directory is missing or empty, falls back to assets embedded in
    /// <paramref name="assembly"/> (default: the entry assembly) — single-binary deployments.
    /// </summary>
    public static void Load(string? contentRoot = null, Assembly? assembly = null)
    {
        contentRoot ??= AppContext.BaseDirectory;
        var wwwroot = Path.Combine(contentRoot, "wwwroot");

        var list = new List<Asset>(16);
        if (Directory.Exists(wwwroot))
        {
            _root = wwwroot;
            LoadDirectory(wwwroot, "", list);
        }
        if (list.Count == 0)
        {
            var asm = assembly ?? Assembly.GetEntryAssembly();
            if (asm is not null) LoadFromAssembly(asm, list);
        }
        _assets = list.ToArray();
    }

    /// <summary>Pure: names of assets embedded in an assembly ("volt.static." resources).</summary>
    public static List<string> ListEmbedded(Assembly assembly)
    {
        var names = new List<string>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (name.StartsWith(EmbeddedPrefix, StringComparison.Ordinal))
                names.Add("/" + name[EmbeddedPrefix.Length..].Replace('\\', '/'));
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static void LoadFromAssembly(Assembly assembly, List<Asset> list)
    {
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(EmbeddedPrefix, StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var ms = new MemoryStream((int)stream.Length);
            stream.CopyTo(ms);
            var path = "/" + name[EmbeddedPrefix.Length..].Replace('\\', '/');
            list.Add(new Asset(path, ms.ToArray(), ComputeETag(ms.ToArray()), ContentTypeFor(path)));
        }
    }

    private static void LoadDirectory(string dir, string prefix, List<Asset> list)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (list.Count >= MaxFiles) return;
            var info = new FileInfo(file);
            if (info.Length > MaxFileBytes) continue;
            var name = prefix + Path.GetFileName(file);
            var bytes = File.ReadAllBytes(file);
            list.Add(new Asset("/" + name, bytes, ComputeETag(bytes), ContentTypeFor(name)));
        }
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (list.Count >= MaxFiles) return;
            LoadDirectory(sub, prefix + Path.GetFileName(sub) + "/", list);
        }
    }

    /// <summary>Span-based lookup: linear scan over the small frozen set — zero allocation.</summary>
    public static bool TryGet(ReadOnlySpan<char> path, out byte[] bytes, out string etag, out string contentType)
    {
        var assets = _assets;
        if (assets is null)
        {
            // hosts call Load() at startup; library/test consumers without a host see "no assets"
            bytes = null!;
            etag = null!;
            contentType = null!;
            return false;
        }
        foreach (var asset in assets)
        {
            if (path.Length == asset.Name.Length && path.SequenceEqual(asset.Name))
            {
                bytes = asset.Bytes;
                etag = asset.ETag;
                contentType = asset.ContentType;
                return true;
            }
        }
        bytes = null!;
        etag = null!;
        contentType = null!;
        return false;
    }

    public static string? Root => _root;

    private static string ComputeETag(ReadOnlySpan<byte> data)
    {
        ulong hash = 14695981039346656037;
        foreach (byte b in data)
        {
            hash ^= b;
            hash *= 1099511628211;
        }
        return "\"" + hash.ToString("x16") + "\"";
    }

    internal static string ContentTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".mjs" => "text/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".avif" => "image/avif",
        ".gif" => "image/gif",
        ".ico" => "image/x-icon",
        ".txt" => "text/plain; charset=utf-8",
        ".xml" => "application/xml; charset=utf-8",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".map" => "application/json; charset=utf-8",
        ".webmanifest" => "application/manifest+json",
        _ => "application/octet-stream",
    };
}
