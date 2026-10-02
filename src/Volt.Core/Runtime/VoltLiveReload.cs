namespace Volt;

/// <summary>
/// M6: development live reload. In DevMode pages get a tiny poll script injected
/// that watches <c>/_volt/ping</c>; when the stamp changes (template/asset edit or
/// an app restart after `volt dev` rebuilt the C#) the browser reloads itself.
/// No websockets, no SSE, no build step — one fetch per second in dev only.
/// </summary>
public static class VoltLiveReload
{
    private static long _stamp = DateTime.UtcNow.Ticks;
    private static FileSystemWatcher? _watcher;

    /// <summary>Current change stamp (public for tests and manual invalidation).</summary>
    public static long Stamp => Volatile.Read(ref _stamp);

    /// <summary>Bumps the stamp — every open browser tab reloads on its next poll.</summary>
    public static void NotifyChanged() => Volatile.Write(ref _stamp, DateTime.UtcNow.Ticks);

    /// <summary>
    /// Starts watching Pages/ and wwwroot/ under <paramref name="root"/> for edits.
    /// C# edits are covered by the restart (dotnet watch): a fresh process starts
    /// with a new stamp, which the poll script also treats as a change.
    /// </summary>
    public static void Start(string root)
    {
        lock (typeof(VoltLiveReload))
        {
            if (_watcher is not null) return;
            try
            {
                _watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                };
                _watcher.Changed += (_, _) => NotifyChanged();
                _watcher.Created += (_, _) => NotifyChanged();
                _watcher.Deleted += (_, _) => NotifyChanged();
                _watcher.Renamed += (_, _) => NotifyChanged();
            }
            catch
            {
                // dev convenience only — never fatal (e.g. root doesn't exist in odd hosts)
                _watcher = null;
            }
        }
    }

    /// <summary>The poll script injected into DevMode HTML pages (no dependencies).</summary>
    public const string PollScript =
        "<script>(function(){var p=0;setInterval(function(){fetch('/_volt/ping').then(function(r){return r.text()}).then(function(t){if(p&&t!==p)location.reload();p=t}).catch(function(){})},1000)})()</script>";
}
