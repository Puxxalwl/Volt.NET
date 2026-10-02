using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using Volt;

namespace Volt.Testing;

/// <summary>
/// In-process Volt server for tests: drives <see cref="VoltEngine"/> directly over a
/// memory transport — no sockets, no Kestrel, no volt CLI. Pages/islands/forms are the
/// real ones (compiled by the Volt source generator into the test assembly).
/// </summary>
/// <example>
/// <code>
/// [Fact]
/// public async Task HomeRenders()
/// {
///     using var server = VoltTestServer.Create();
///     var response = await server.GetAsync("/");
///     response.AssertMatchesSnapshot();
/// }
/// </code>
/// </example>
public sealed class VoltTestServer : IDisposable
{
    private readonly VoltOptions _options;

    private VoltTestServer(VoltOptions options) => _options = options;

    /// <summary>
    /// Creates a test server. Options defaults: DevMode on (richer error pages),
    /// BaseUrl http://127.0.0.1 (deterministic sitemap/robots snapshots).
    /// </summary>
    public static VoltTestServer Create(Action<VoltOptions>? configure = null)
    {
        var options = new VoltOptions { DevMode = true, BaseUrl = "http://127.0.0.1" };
        configure?.Invoke(options);
        return new VoltTestServer(options);
    }

    /// <summary>GET (or HEAD via <paramref name="method"/>) a path, query included ("?a=b").</summary>
    public Task<VoltTestResponse> GetAsync(string pathAndQuery) => SendAsync("GET", pathAndQuery);

    /// <summary>POST a urlencoded form to a page route (typed form flow, no JS).</summary>
    public Task<VoltTestResponse> PostAsync(string path, params (string Name, string Value)[] fields)
    {
        if (fields.Length == 0) return SendAsync("POST", path);
        var sb = new StringBuilder(64);
        foreach (var (name, value) in fields)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
        }
        return SendAsync("POST", path, Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>Raw request: method, path (with optional query), optional urlencoded body bytes.</summary>
    public async Task<VoltTestResponse> SendAsync(string method, string pathAndQuery, byte[]? formBody = null)
    {
        string path = pathAndQuery, query = "";
        int q = pathAndQuery.IndexOf('?');
        if (q >= 0)
        {
            path = pathAndQuery[..q];
            query = pathAndQuery[(q + 1)..];
        }

        using var output = new PooledBufferWriter();
        var ctx = new VoltHttpContext
        {
            Method = method,
            Path = path,
            Query = query.Length > 0 ? query : null,
            Output = output,
            BodyMemory = formBody,
        };
        await VoltEngine.HandleAsync(ctx, _options);

        return new VoltTestResponse(
            ctx.StatusCode,
            ctx.ContentType ?? "",
            ctx.Location,
            ctx.ETag,
            Encoding.UTF8.GetString(output.WrittenSpan));
    }

    public void Dispose() { }
}

/// <summary>A response captured from the in-process server.</summary>
public sealed class VoltTestResponse
{
    private readonly int _statusCode;
    private readonly string _contentType;
    private readonly string? _location;
    private readonly string? _etag;
    private readonly string _body;

    internal VoltTestResponse(int statusCode, string contentType, string? location, string? etag, string body)
    {
        _statusCode = statusCode;
        _contentType = contentType;
        _location = location;
        _etag = etag;
        _body = body;
    }

    public int StatusCode => _statusCode;
    public string ContentType => _contentType;
    public string? Location => _location;
    public string? ETag => _etag;
    public string Body => _body;

    /// <summary>
    /// Golden-file snapshot: compares status + content-type + body against
    /// <c>__snapshots__/{test}.{key}.html</c> next to the calling test source file.
    /// Recreate/overwrite golden files with <c>VOLT_UPDATE_SNAPSHOTS=1</c>.
    /// </summary>
    public void AssertMatchesSnapshot(
        string? key = null,
        [CallerMemberName] string test = "",
        [CallerFilePath] string sourceFile = "")
    {
        var name = key is null ? $"{test}.html" : $"{test}.{key}.html";
        var directory = Path.Combine(Path.GetDirectoryName(sourceFile)!, "__snapshots__");
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(directory);

        var actual = $"// status={_statusCode} type={_contentType}\n{_body}";
        actual = actual.ReplaceLineEndings("\n");

        if (Environment.GetEnvironmentVariable("VOLT_UPDATE_SNAPSHOTS") == "1")
        {
            File.WriteAllText(path, actual);
            return;
        }

        if (!File.Exists(path))
        {
            Xunit.Assert.Fail(
                $"snapshot {name} does not exist — run once with VOLT_UPDATE_SNAPSHOTS=1 to create it:\n  {path}");
            return;
        }

        var expected = File.ReadAllText(path).ReplaceLineEndings("\n");
        if (expected != actual)
        {
            int at = FirstDifference(expected, actual);
            int contextStart = Math.Max(0, at - 60);
            Xunit.Assert.Fail(
                $"snapshot {name} mismatch at offset {at}:\n" +
                $"  expected: …{Ellipsize(expected[contextStart..(Math.Min(expected.Length, at + 60))])}…\n" +
                $"  actual:   …{Ellipsize(actual[contextStart..(Math.Min(actual.Length, at + 60))])}…\n" +
                $"  (update with VOLT_UPDATE_SNAPSHOTS=1)");
        }
    }

    private static int FirstDifference(string expected, string actual)
    {
        int n = Math.Min(expected.Length, actual.Length);
        for (int i = 0; i < n; i++)
            if (expected[i] != actual[i]) return i;
        return n;
    }

    private static string Ellipsize(string s)
    {
        s = s.Replace('\n', '⏎').Replace('\r', '⏎');
        return s.Length > 120 ? s[..120] + "…" : s;
    }
}
