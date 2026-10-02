using System.Text;

namespace Volt;

/// <summary>SSG export: renders every SSG route to static files (see `volt export`).</summary>
public static class VoltExporter
{
    /// <summary>Exports all SSG pages, sitemap.xml and robots.txt into <paramref name="outDir"/>.</summary>
    /// <returns>Number of pages written.</returns>
    public static int Export(VoltOptions options, string outDir, TextWriter? log = null)
    {
        log ??= Console.Out;
        int count = 0;

        // relative paths resolve against the app binary (not the CWD of `dotnet run`)
        if (!Path.IsPathRooted(outDir))
            outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, outDir));

        foreach (var route in VoltRuntime.Routes.Routes)
        {
            var page = route.Factory();
            if (page.Mode != RenderMode.SSG) continue;

            if (route.ParamCount == 0)
            {
                count += RenderRoute(route, page, route.Pattern, outDir);
            }
            else
            {
                foreach (var value in page.StaticPaths())
                {
                    var path = route.MakePath(value);
                    count += RenderRoute(route, route.Factory(), path, outDir);
                }
            }
        }

        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir, "sitemap.xml"), Sitemap.BuildXml(VoltRuntime.Routes, (options.BaseUrl ?? "http://localhost").TrimEnd('/')));
        File.WriteAllBytes(Path.Combine(outDir, "robots.txt"), Sitemap.BuildRobots(options.BaseUrl ?? "http://localhost"));

        // copy wwwroot if present (relative to the current directory)
        if (Directory.Exists("wwwroot"))
        {
            foreach (var file in Directory.EnumerateFiles("wwwroot", "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath("wwwroot", file);
                var dest = Path.Combine(outDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
            log.WriteLine($"volt export: wwwroot copied");
        }

        log.WriteLine($"volt export: {count} pages -> {Path.GetFullPath(outDir)}");
        return count;
    }

    private static int RenderRoute(RoutePattern route, VoltPage page, string path, string outDir)
    {
        var request = new VoltRequest
        {
            Method = "GET",
            Path = path,
            IsExport = true,
        };
        page.OnPreRenderAsync(request).GetAwaiter().GetResult();

        using var buffer = new PooledBufferWriter();
        var w = HtmlWriter.Rent(buffer);

        // route params for dynamic paths (slices of the request path string)
        Span<RawParam> slices = stackalloc RawParam[16];
        var match = VoltRuntime.Routes.Match(path.AsSpan(), slices);
        var ctx = new RenderContext { Request = request, Params = match.Params };

        page.Render(w, ctx);
        w.Return();

        var file = path == "/"
            ? Path.Combine(outDir, "index.html")
            : Path.Combine(outDir, path.Trim('/').Replace('/', Path.DirectorySeparatorChar), "index.html");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, buffer.WrittenSpan.ToArray());
        return 1;
    }
}
