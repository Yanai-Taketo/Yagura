using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Yagura.Ingestion.Diagnostics;

namespace Yagura.Ingestion.Tests.Diagnostics;

/// <summary>
/// スプール退避カウンタの <c>reason</c> タグが退避契機（容量 / 時間 / 停止時）を判別できること
/// （M-7 の残作業。Issue #271。architecture.md §5.3・§4.1.1）。
/// </summary>
public sealed class SpoolEvacuationReasonTests
{
    [Theory]
    [InlineData(SpoolEvacuationReason.Q2Overflow, "q2_overflow")]
    [InlineData(SpoolEvacuationReason.WriteTimeout, "write_timeout")]
    [InlineData(SpoolEvacuationReason.Shutdown, "shutdown")]
    public void RecordSpoolEvacuated_TagsReason(SpoolEvacuationReason reason, string expectedTag)
    {
        using var metrics = new IngestionMetrics();
        using var collector = new MetricCollector<long>(metrics.SpoolEvacuatedCounter);

        metrics.RecordSpoolEvacuated(reason);

        var measurement = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal(1, measurement.Value);
        Assert.Equal(expectedTag, measurement.Tags["reason"]);
    }

    [Fact]
    public void RecordSpoolEvacuated_DistinctReasons_AreSeparableByTag()
    {
        using var metrics = new IngestionMetrics();
        using var collector = new MetricCollector<long>(metrics.SpoolEvacuatedCounter);

        metrics.RecordSpoolEvacuated(SpoolEvacuationReason.Q2Overflow);
        metrics.RecordSpoolEvacuated(SpoolEvacuationReason.Q2Overflow);
        metrics.RecordSpoolEvacuated(SpoolEvacuationReason.WriteTimeout);
        metrics.RecordSpoolEvacuated(SpoolEvacuationReason.Shutdown);

        var byReason = collector.GetMeasurementSnapshot()
            .GroupBy(m => (string)m.Tags["reason"]!)
            .ToDictionary(g => g.Key, g => g.Sum(m => m.Value));

        Assert.Equal(2, byReason["q2_overflow"]);
        Assert.Equal(1, byReason["write_timeout"]);
        Assert.Equal(1, byReason["shutdown"]);

        // 累積総数（永続化対象）は契機によらず総和のまま。
        Assert.Equal(4, metrics.SnapshotCumulativeCounters().SpoolEvacuated);
    }
}
