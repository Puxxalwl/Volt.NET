using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Volt;

/// <summary>Application entry point: `VoltApp.Run()` in Program.cs.</summary>
public static class VoltApp
{
    /// <summary>
    /// Runs the app. Handles the export mode (VOLT_RUN_MODE=export) before starting the server.
    /// </summary>
    public static int Run(string[]? args = null, VoltOptions? options = null)
    {
        options ??= VoltOptions.FromEnvironment();
        VoltRuntime.DevMode = options.DevMode;
        HydrationAssets.Warmup();
        VoltRuntime.HydrateVersion = HydrationAssets.ComputeVersion();

        if (Environment.GetEnvironmentVariable("VOLT_RUN_MODE") == "export")
        {
            VoltExporter.Export(options, options.ExportPath);
            return 0;
        }

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory, // wwwroot next to the binary (run & publish)
        });
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 256 * 1024;
            kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
        });

        var app = builder.Build();
        app.UseStaticFiles();
        app.Run(async ctx => await VoltPipeline.HandleAsync(ctx, options));
        app.Run();
        return 0;
    }

    /// <summary>
    /// Starts an in-process test server (E2E tests). Returns the base URL, e.g. http://127.0.0.1:54321.
    /// </summary>
    public static async Task<(string Url, WebApplication App)> StartTestServerAsync(
        VoltOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new VoltOptions { DevMode = true, BaseUrl = "http://127.0.0.1" };
        VoltRuntime.DevMode = options.DevMode;
        HydrationAssets.Warmup();
        VoltRuntime.HydrateVersion = HydrationAssets.ComputeVersion();

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseStaticFiles();
        app.Run(async ctx => await VoltPipeline.HandleAsync(ctx, options));
        await app.StartAsync(cancellationToken);
        return (app.Urls.FirstOrDefault() ?? "http://127.0.0.1:0", app);
    }
}
