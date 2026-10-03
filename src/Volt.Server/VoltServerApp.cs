using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Volt;

/// <summary>Host entry for the built-in zero-allocation server (M2 transport).</summary>
public static class VoltServerApp
{
    /// <summary>Runs the app on the Volt.Server transport (equivalent of VoltApp.Run).</summary>
    public static int Run(string[] args, VoltOptions? options = null)
    {
        options ??= new VoltOptions();
        if (Environment.GetEnvironmentVariable("VOLT_DEV") == "1") options.DevMode = true;
        VoltRuntime.DevMode = options.DevMode;
        LoadHydrateScript(typeof(Volt.Hydration.HydrationRuntime).Assembly);
        VoltStaticAssets.Load();

        if (Environment.GetEnvironmentVariable("VOLT_RUN_MODE") == "export")
        {
            var outDir = Environment.GetEnvironmentVariable("VOLT_EXPORT_DIR") ?? options.ExportPath;
            VoltExporter.Export(options, outDir); // page count goes to the log, not the exit code
            return 0;
        }

        var port = ResolvePort(args, options);
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        var certificate = ResolveCertificate(args);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        using var server = new Volt.Server.VoltServer(options, certificate);
        server.Start(endpoint);
        var actual = (IPEndPoint)server.LocalEndPoint!;
        var scheme = certificate is null ? "http" : "https";
        options.BaseUrl ??= $"{scheme}://{actual.Address}:{actual.Port}";
        Console.WriteLine($"Volt server listening on {scheme}://{actual.Address}:{actual.Port}");

        cts.Token.WaitHandle.WaitOne();
        return 0;
    }

    /// <summary>Starts an in-process test server on a random port. For E2E tests.</summary>
    public static async Task<(string Url, VoltServerHandle Server)> StartTestServerAsync(VoltOptions? options = null, X509Certificate2? tlsCertificate = null)
    {
        options ??= new VoltOptions { DevMode = true, BaseUrl = "http://127.0.0.1" };
        VoltRuntime.DevMode = options.DevMode;
        LoadHydrateScript(typeof(Volt.Hydration.HydrationRuntime).Assembly);
        VoltStaticAssets.Load();

        var server = new Volt.Server.VoltServer(options, tlsCertificate);
        server.Start(new IPEndPoint(IPAddress.Loopback, 0));
        await Task.Yield();
        var endpoint = (IPEndPoint)server.LocalEndPoint!;
        var scheme = tlsCertificate is null ? "http" : "https";
        options.BaseUrl = $"{scheme}://{endpoint.Address}:{endpoint.Port}";
        return ($"{scheme}://{endpoint.Address}:{endpoint.Port}", new VoltServerHandle(server));
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


    /// <summary>Reads --tls-cert/--tls-key (or VOLT_TLS_CERT/VOLT_TLS_KEY) and builds the server certificate.</summary>
    private static X509Certificate2? ResolveCertificate(string[] args)
    {
        string? certPath = ArgValue(args, "--tls-cert") ?? Environment.GetEnvironmentVariable("VOLT_TLS_CERT");
        string? keyPath = ArgValue(args, "--tls-key") ?? Environment.GetEnvironmentVariable("VOLT_TLS_KEY");
        if (certPath is null) return null;
        return X509Certificate2.CreateFromPemFile(certPath, keyPath);
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
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

    /// <summary>Generates a self-signed localhost certificate for tests/dev TLS.</summary>
    public static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = DateTimeOffset.UtcNow.AddDays(7);
        return request.CreateSelfSigned(notBefore, notAfter);
    }
}


/// <summary>Handle over a running test server.</summary>
public sealed class VoltServerHandle : IDisposable
{
    private readonly Volt.Server.VoltServer _server;
    public VoltServerHandle(Volt.Server.VoltServer server) => _server = server;
    public void Dispose() => _server.Dispose();
}
