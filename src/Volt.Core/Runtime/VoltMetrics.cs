using System.Diagnostics;
using System.Text;

namespace Volt;

/// <summary>
/// M6: request metrics — lock-free, allocation-free. Striped padded counters (one
/// cache line each) + fixed-bucket microsecond histograms. Enabled via
/// <c>VoltOptions.EnableMetrics</c>; when off the record calls are branch-gated
/// no-ops so the fast path stays untouched.
/// Exposed as Prometheus text format at <c>/_volt/metrics</c> and as a live
/// auto-refreshing HTML page at <c>/_volt/hud</c>.
/// </summary>
public static class VoltMetrics
{
    private const int LineSize = 128; // keep counters on separate cache lines

    // totals
    private static readonly long[] _requests = new long[LineSize];
    private static readonly long[] _fastPath = new long[LineSize];
    private static readonly long[] _ssgHits = new long[LineSize];
    private static readonly long[] _bytes = new long[LineSize];

    // status classes: 2xx/3xx/4xx/5xx (index code/100 - 2)
    private static readonly long[][] _status =
    [
        new long[LineSize], new long[LineSize], new long[LineSize], new long[LineSize],
    ];

    // duration histogram buckets in µs (upper bounds, inclusive): 2 5 10 25 50 100 250 500 1000 2500 5000 10000 + overflow
    private static readonly long[] BucketsUs = [2, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000];
    private static readonly long[] _duration = new long[LineSize * (12 + 1)]; // 12 buckets + overflow
    private static readonly long[] _durationSum = new long[LineSize]; // total µs (long enough)

    internal static volatile bool Enabled;

    /// <summary>Records one served request (any status).</summary>
    public static void RecordRequest() { if (Enabled) Interlocked.Increment(ref _requests[0]); }

    /// <summary>Records one zero-alloc fast-path hit.</summary>
    public static void RecordFastPath() { if (Enabled) Interlocked.Increment(ref _fastPath[0]); }

    /// <summary>Records one SSG cache hit (misses go through the render path).</summary>
    public static void RecordSsgHit() { if (Enabled) Interlocked.Increment(ref _ssgHits[0]); }

    /// <summary>Records body bytes written.</summary>
    public static void RecordBytes(long count) { if (Enabled && count > 0) Interlocked.Add(ref _bytes[0], count); }

    /// <summary>Records a response status class (2xx..5xx; other codes are ignored).</summary>
    public static void RecordStatus(int statusCode)
    {
        if (!Enabled) return;
        int slot = statusCode / 100 - 2;
        if ((uint)slot < 4) Interlocked.Increment(ref _status[slot][0]);
    }

    /// <summary>Records request duration from a Stopwatch timestamp into the fixed histogram.</summary>
    public static void RecordDuration(long startTimestamp)
    {
        if (!Enabled) return;
        long us = (Stopwatch.GetTimestamp() - startTimestamp) * 1_000_000 / Stopwatch.Frequency;
        Interlocked.Add(ref _durationSum[0], us);
        int bucket = BucketIndex(us);
        Interlocked.Increment(ref _duration[bucket * LineSize]);
    }

    private static int BucketIndex(long us)
    {
        for (int i = 0; i < BucketsUs.Length; i++)
            if (us <= BucketsUs[i]) return i;
        return BucketsUs.Length; // overflow bucket
    }

    /// <summary>Renders the metrics in Prometheus text exposition format (ASCII).</summary>
    public static string Render()
    {
        var sb = new StringBuilder(512);
        sb.Append("volt_requests_total ").Append(Volatile.Read(ref _requests[0])).Append('\n');
        sb.Append("volt_fast_path_total ").Append(Volatile.Read(ref _fastPath[0])).Append('\n');
        sb.Append("volt_ssg_hits_total ").Append(Volatile.Read(ref _ssgHits[0])).Append('\n');
        sb.Append("volt_bytes_written_total ").Append(Volatile.Read(ref _bytes[0])).Append('\n');
        sb.Append("volt_responses_2xx_total ").Append(Volatile.Read(ref _status[0][0])).Append('\n');
        sb.Append("volt_responses_3xx_total ").Append(Volatile.Read(ref _status[1][0])).Append('\n');
        sb.Append("volt_responses_4xx_total ").Append(Volatile.Read(ref _status[2][0])).Append('\n');
        sb.Append("volt_responses_5xx_total ").Append(Volatile.Read(ref _status[3][0])).Append('\n');

        long total = Volatile.Read(ref _requests[0]);
        long sum = Volatile.Read(ref _durationSum[0]);
        sb.Append("volt_request_us_sum ").Append(sum).Append('\n');
        sb.Append("volt_request_us_count ").Append(total).Append('\n');
        long cumulative = 0;
        for (int i = 0; i < BucketsUs.Length; i++)
        {
            cumulative += Volatile.Read(ref _duration[i * LineSize]);
            sb.Append("volt_request_us_bucket{le=\"").Append(BucketsUs[i]).Append("\"} ").Append(cumulative).Append('\n');
        }
        cumulative += Volatile.Read(ref _duration[BucketsUs.Length * LineSize]);
        sb.Append("volt_request_us_bucket{le=\"+Inf\"} ").Append(cumulative).Append('\n');
        if (total > 0)
        {
            sb.Append("# avg ").Append(sum / total).Append("µs\n");
        }
        return sb.ToString();
    }
}
