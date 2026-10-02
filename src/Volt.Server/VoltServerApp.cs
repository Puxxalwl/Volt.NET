using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace Volt;

/// <summary>Host entry for the built-in zero-allocation server (M2 transport).</summary>
public static class VoltServerApp
{
    /// <summary>Runs the app on the Volt.Server transport (equivalent of VoltApp.Run).</summary>
    public static int Run(string[] args, VoltOptions? options = null)
    {
        options ??= new VoltOptions();
        VoltRuntime.DevMode = options.DevMode;
        LoadHydrateScript(typeof(Volt.Hydration.HydrationRuntime).Assembly);
        VoltStaticAssets.Load();

        if (Environment.GetEnvironmentVariable("VOLT_RUN_MODE") == "export")
        {
            var outDir = Environment.GetEnvironmentVariable("VOLT_EXPORT_DIR") ?? options.ExportPath;
            return VoltExporter.Export(options, outDir);
        }

        var port = ResolvePort(args, options);
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        using var server = new Volt.Server.VoltServer(options);
        server.Start(endpoint);
        var actual = (IPEndPoint)server.LocalEndPoint!;
        options.BaseUrl ??= $"http://{actual.Address}:{actual.Port}";
        Console.WriteLine($"Volt server listening on http://{actual.Address}:{actual.Port}");

        cts.Token.WaitHandle.WaitOne();
        return 0;
    }

    /// <summary>Starts an in-process test server on a random port. For E2E tests.</summary>
    public static async Task<(string Url, VoltServerHandle Server)> StartTestServerAsync(VoltOptions? options = null)
    {
        options ??= new VoltOptions { DevMode = true, BaseUrl = "http://127.0.0.1" };
        VoltRuntime.DevMode = options.DevMode;
        LoadHydrateScript(typeof(Volt.Hydration.HydrationRuntime).Assembly);
        VoltStaticAssets.Load();

        var server = new Volt.Server.VoltServer(options);
        server.Start(new IPEndPoint(IPAddress.Loopback, 0));
        await Task.Yield();
        var endpoint = (IPEndPoint)server.LocalEndPoint!;
        options.BaseUrl = $"http://{endpoint.Address}:{endpoint.Port}";
        return ($"http://{endpoint.Address}:{endpoint.Port}", new VoltServerHandle(server));
    }

    internal static void LoadHydrateScript(Assembly hydrationAssembly)
    {
        if (VoltRuntime.HydrateScript is not null) return;
        string? name = null;
        foreach (var n in hydrationAssembly.GetManifestResourceNames())
        {
            if (n.EndsWith("hydrate.js", StringComparison.Ordinal))
            {
                name = n;
                break;
            }
        }
        if (name is null) throw new InvalidOperationException("Volt: embedded hydrate.js not found.");
        using var stream = hydrationAssembly.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream((int)stream.Length);
        stream.CopyTo(ms);
        VoltRuntime.SetHydrateScript(ms.ToArray());
    }

    private static int ResolvePort(string[] args, VoltOptions options)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--port" or "-p" && int.TryParse(args[i + 1], out var p)) return p;
        }
        if (int.TryParse(Environment.GetEnvironmentVariable("VOLT_PORT"), out var envPort)) return envPort;
        return 5000;
    }
}

/// <summary>Handle over a running test server.</summary>
public sealed class VoltServerHandle : IDisposable
{
    private readonly Volt.Server.VoltServer _server;
    public VoltServerHandle(Volt.Server.VoltServer server) => _server = server;
    public void Dispose() => _server.Dispose();
}
