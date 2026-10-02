using System.Diagnostics;

namespace Volt.Cli;

/// <summary>`volt` command-line interface.</summary>
public static class VoltCli
{
    public static int Run(string[] args)
    {
        if (args.Length == 0) return Usage();

        var command = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();
        try
        {
            return command switch
            {
                "new" => New(rest),
                "dev" => Dev(rest),
                "build" => Build(rest),
                "export" => Export(rest),
                "serve" => Serve(rest),
                "wasm" => Wasm(rest),
                "help" or "--help" or "-h" => Usage(),
                _ => Unknown(command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"volt: {ex.Message}");
            return 1;
        }
    }

    private static int Wasm(string[] args)
    {
        if (args.Length != 2 || args[0] != "validate")
        {
            Console.Error.WriteLine("usage: volt wasm validate <file.wasm>");
            return 1;
        }
        var path = args[1];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"volt: file not found: {path}");
            return 1;
        }

        WasmContractResult result;
        try
        {
            result = WasmContractValidator.Validate(File.ReadAllBytes(path));
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"volt: cannot read {path}: {ex.Message}");
            return 1;
        }

        foreach (var export in result.Exports)
            Console.WriteLine("  " + export);

        if (result.Valid)
        {
            Console.WriteLine($"OK: {Path.GetFileName(path)} satisfies the Volt island module contract");
            return 0;
        }

        foreach (var error in result.Errors)
            Console.Error.WriteLine($"volt: {error}");
        Console.Error.WriteLine($"FAIL: {Path.GetFileName(path)} does not satisfy the Volt island module contract (see CONTRACT.md)");
        return 1;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"volt: unknown command '{command}'");
        return Usage();
    }

    private static int Usage()
    {
        Console.WriteLine(
            """
            volt — fullstack C# framework (SSG + SSR + islands hydration, Native AOT)

            Usage:
              volt new <path>                 scaffold a new app
              volt dev [path] [--port N]      dev server with hot reload (dotnet watch)
              volt build [path] [--rid R]     native AOT publish (default rid: linux-x64)
              volt export [path] [--out DIR]  export SSG pages to dist/ (default)
              volt serve [path] [--port N]    run the app without watch
              volt wasm validate <file.wasm> check an island module against the CONTRACT

            Options:
              --no-aot       build without Native AOT
              --base-url U   site origin used for export/SEO
            """);
        return 0;
    }

    // ------------------------------------------------------------------

    private static int New(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--"))
        {
            Console.Error.WriteLine("volt new: path required (e.g. volt new mysite)");
            return 1;
        }
        var target = Path.GetFullPath(args[0]);
        if (Directory.Exists(target) && Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Any())
        {
            Console.Error.WriteLine($"volt new: '{target}' is not empty");
            return 1;
        }
        Directory.CreateDirectory(target);

        var appName = SanitizeName(Path.GetFileName(target));
        foreach (var (rel, content) in AppTemplate.Files(appName))
        {
            var file = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content.Replace("Starter", appName, StringComparison.Ordinal));
        }
        Console.WriteLine($"volt new: app created at {target}");
        Console.WriteLine("  cd " + args[0]);
        Console.WriteLine("  volt dev");
        return 0;
    }

    private static string SanitizeName(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        var parts = name.Split(new[] { '-', '_', '.', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.Length == 0) continue;
            sb.Append(char.ToUpperInvariant(part[0]));
            if (part.Length > 1) sb.Append(part.Substring(1));
        }
        var result = sb.ToString();
        return result.Length == 0 ? "Starter" : result;
    }

    // ------------------------------------------------------------------

    private static int Dev(string[] args)
    {
        var project = ResolveProject(args);
        var port = FlagValue(args, "--port");
        var env = new Dictionary<string, string>
        {
            ["VOLT_DEV"] = "1",
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
        };
        if (port is not null) env["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        Console.WriteLine($"volt dev: {project} (hot reload via dotnet watch)");
        return RunDotnet(["watch", "run", "--project", project], env);
    }

    private static int Build(string[] args)
    {
        var project = ResolveProject(args);
        var rid = FlagValue(args, "--rid") ?? "linux-x64";
        var noAot = HasFlag(args, "--no-aot");
        var publishArgs = new List<string> { "publish", "-c", "Release", "-r", rid, "--project", project };
        // PublishAot stays per-project: a global /p would flow into netstandard2.0 analyzer projects (NETSDK1207)
        if (noAot) publishArgs.Add("/p:PublishAot=false");
        Console.WriteLine($"volt build: {project} (rid: {rid}, aot: {!noAot})");
        return RunDotnet(publishArgs.ToArray(), null);
    }

    private static int Export(string[] args)
    {
        var project = ResolveProject(args);
        var outDir = FlagValue(args, "--out") ?? "dist";
        var baseUrl = FlagValue(args, "--base-url");
        var env = new Dictionary<string, string>
        {
            ["VOLT_RUN_MODE"] = "export",
            ["VOLT_DIST"] = outDir,
        };
        if (baseUrl is not null) env["VOLT_BASE_URL"] = baseUrl;
        Console.WriteLine($"volt export: {project} -> {outDir}");
        return RunDotnet(["run", "--project", project, "-c", "Release"], env);
    }

    private static int Serve(string[] args)
    {
        var project = ResolveProject(args);
        var port = FlagValue(args, "--port");
        var env = new Dictionary<string, string>();
        if (port is not null) env["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        Console.WriteLine($"volt serve: {project}");
        return RunDotnet(["run", "--project", project], env);
    }

    // ------------------------------------------------------------------

    private static string ResolveProject(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith("--")) continue;
            var path = Path.GetFullPath(arg);
            if (File.Exists(path) && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                return path;
            if (Directory.Exists(path))
            {
                var csproj = Directory.EnumerateFiles(path, "*.csproj").FirstOrDefault();
                if (csproj is not null) return csproj;
                // maybe it's the publish output binary
                return path;
            }
            Console.Error.WriteLine($"volt: path not found: {arg}");
            Environment.Exit(1);
            return "";
        }
        // default: current directory
        var cwd = Directory.GetCurrentDirectory();
        var localCsproj = Directory.EnumerateFiles(cwd, "*.csproj").FirstOrDefault();
        if (localCsproj is not null) return localCsproj;
        Console.Error.WriteLine("volt: no project found in the current directory");
        Environment.Exit(1);
        return "";
    }

    private static string? FlagValue(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == flag) return args[i + 1];
        return null;
    }

    private static bool HasFlag(string[] args, string flag) => args.Contains(flag);

    private static int RunDotnet(IReadOnlyList<string> arguments, Dictionary<string, string>? environment)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        if (environment is not null)
            foreach (var (k, v) in environment)
                psi.Environment[k] = v;

        using var process = Process.Start(psi);
        if (process is null)
        {
            Console.Error.WriteLine("volt: could not start dotnet");
            return 1;
        }
        process.WaitForExit();
        return process.ExitCode;
    }
}
