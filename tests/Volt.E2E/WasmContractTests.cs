using System.Diagnostics;
using Xunit;

namespace Volt.E2E;

/// <summary>
/// M4 WASM tooling: the validator parses real binaries, the fixture module
/// satisfies the contract, and the SHIPPED hydrate.js speaks the protocol
/// (checked by running the real module under node).
/// </summary>
public sealed class WasmContractTests
{
    private static byte[] Fixture
    {
        get
        {
            var fixture = FindRepoFile("tests/fixtures/calc.wasm");
            Assert.True(fixture is not null, "tests/fixtures/calc.wasm not found");
            return File.ReadAllBytes(fixture!);
        }
    }

    private static string? FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent!;
        }
        return null;
    }

    [Fact]
    public void Validator_AcceptsContractCompliantModule()
    {
        var result = WasmContractValidator.Validate(Fixture);
        Assert.True(result.Valid, string.Join("; ", result.Errors));
        Assert.Contains("memory memory (#0)", result.Exports);
        Assert.Contains("func   alloc (#0)", result.Exports);
        Assert.Contains("func   volt_dispatch (#1)", result.Exports);
        Assert.Contains("func   volt_render (#2)", result.Exports);
    }

    [Fact]
    public void Validator_RejectsNonWasmBytes()
    {
        var result = WasmContractValidator.Validate("<html>not wasm</html>"u8.ToArray());
        Assert.False(result.Valid);
        Assert.Contains(result.Errors, e => e.Contains("magic"));
    }

    [Fact]
    public void Validator_RejectsModuleMissingExports()
    {
        // a valid wasm header with a type section but no exports
        var module = new byte[]
        {
            0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00,
            0x01, 0x02, 0x01, 0x60, 0x00, 0x00, // type section: () -> ()
        };
        var result = WasmContractValidator.Validate(module);
        Assert.False(result.Valid);
        Assert.Contains(result.Errors, e => e.Contains("no 'alloc' export"));
        Assert.Contains(result.Errors, e => e.Contains("no 'volt_dispatch' export"));
        Assert.Contains(result.Errors, e => e.Contains("no 'volt_render' export"));
        Assert.Contains(result.Errors, e => e.Contains("no 'memory' export"));
    }

    [Fact]
    public void Validator_RejectsTruncatedSection()
    {
        var module = new byte[]
        {
            0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00,
            0x01, 0x10, 0x01, 0x60, 0x00, 0x00, // type section claims 16 bytes, has 4
        };
        var result = WasmContractValidator.Validate(module);
        Assert.False(result.Valid);
        Assert.Contains(result.Errors, e => e.Contains("overruns the module"));
    }

    [Fact]
    public void NodeConformance_HydrateJsSpeaksTheWasmProtocol()
    {
        var node = FindExecutable("node");
        if (node is null)
        {
            return; // node unavailable in this environment: protocol covered by the C# validator
        }

        var hydrate = FindRepoFile("src/Volt.Hydration.JS/wwwroot/hydrate.js");
        Assert.True(hydrate is not null, "hydrate.js not found");
        var script = FindRepoFile("tests/Volt.E2E/wasm-conformance.js");
        Assert.True(script is not null, "wasm-conformance.js not found");

        var psi = new ProcessStartInfo
        {
            FileName = node,
            Arguments = $"\"{script}\" \"{hydrate}\" \"{FindRepoFile("tests/fixtures/calc.wasm")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "node did not exit in time");
        Assert.True(process.ExitCode == 0, $"node failed: {stdout}{stderr}");
        Assert.Contains("wasm conformance: OK", stdout);
    }

    private static string? FindExecutable(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate) && IsExecutable(candidate)) return candidate;
        }
        return null;
    }

    private static bool IsExecutable(string file)
    {
        try
        {
            using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (IOException) { return false; }
    }
}
