using System.Globalization;

namespace Yagura.Bench.StorageBench;

/// <summary>
/// 複数試行のレイテンシ計測値から代表統計量を求める（DB-9/DB-10 のクエリレイテンシ・
/// DDL 実行時間の報告に共通で使う）。
/// </summary>
public sealed record LatencyStats(
    int SampleCount,
    TimeSpan Min,
    TimeSpan Max,
    TimeSpan Mean,
    TimeSpan P50,
    TimeSpan P95)
{
    /// <summary>
    /// 試行結果（<see cref="TimeSpan"/> の列）から統計量を計算する。
    /// </summary>
    public static LatencyStats From(IReadOnlyList<TimeSpan> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            throw new ArgumentException("試行が 0 件では統計を計算できない。", nameof(samples));
        }

        var sorted = samples.OrderBy(s => s).ToArray();
        var mean = TimeSpan.FromTicks((long)sorted.Average(s => s.Ticks));

        return new LatencyStats(
            SampleCount: sorted.Length,
            Min: sorted[0],
            Max: sorted[^1],
            Mean: mean,
            P50: Percentile(sorted, 0.50),
            P95: Percentile(sorted, 0.95));
    }

    private static TimeSpan Percentile(IReadOnlyList<TimeSpan> sorted, double percentile)
    {
        if (sorted.Count == 1)
        {
            return sorted[0];
        }

        var rank = percentile * (sorted.Count - 1);
        var lowerIndex = (int)Math.Floor(rank);
        var upperIndex = (int)Math.Ceiling(rank);
        if (lowerIndex == upperIndex)
        {
            return sorted[lowerIndex];
        }

        var fraction = rank - lowerIndex;
        var lowerTicks = sorted[lowerIndex].Ticks;
        var upperTicks = sorted[upperIndex].Ticks;
        return TimeSpan.FromTicks(lowerTicks + (long)((upperTicks - lowerTicks) * fraction));
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"n={SampleCount} min={Min.TotalMilliseconds:F1}ms p50={P50.TotalMilliseconds:F1}ms " +
            $"p95={P95.TotalMilliseconds:F1}ms max={Max.TotalMilliseconds:F1}ms mean={Mean.TotalMilliseconds:F1}ms");
}
