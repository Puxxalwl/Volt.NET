using System.Reflection;

namespace Volt;

/// <summary>Loads the embedded hydration script (from Volt.Hydration.JS) into VoltRuntime.</summary>
internal static class HydrationAssets
{
    private static byte[]? _script;

    /// <summary>Loads hydrate.js from the embedded resource; idempotent.</summary>
    public static byte[] Load()
    {
        if (_script is not null) return _script;
        var asm = typeof(Volt.Hydration.HydrationRuntime).Assembly;
        string? name = null;
        foreach (var n in asm.GetManifestResourceNames())
        {
            if (n.EndsWith("hydrate.js", StringComparison.Ordinal))
            {
                name = n;
                break;
            }
        }
        if (name is null) throw new InvalidOperationException("Volt: embedded hydrate.js not found.");

        using var stream = asm.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream((int)stream.Length);
        stream.CopyTo(ms);
        _script = ms.ToArray();
        return _script;
    }

    /// <summary>Installs the script into VoltRuntime (version + ETag) for engine serving.</summary>
    public static void Install()
    {
        VoltRuntime.SetHydrateScript(Load());
    }
}
