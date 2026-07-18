using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Yagura.Ingestion.Diagnostics;

namespace Yagura.Ingestion.Tests.Diagnostics;

/// <summary>
/// <see cref="IngestionMetrics.RecordTlsHandshakeFailure"/> の <c>source_address</c> タグの
/// カーディナリティ有界化（#316。security.md §6・architecture.md §4.1.1）。送信元アドレスは TLS 認証
/// 成立前に計上される攻撃者制御の次元であり、無制限のタグ基数が集約 exporter のメモリを圧迫し得るため、
/// distinct 値を <see cref="IngestionMetrics.MaxTlsHandshakeFailureSourceCardinality"/> までに抑え、
/// 超過分は <see cref="IngestionMetrics.TlsHandshakeFailureOverflowSource"/> へ畳む。
/// </summary>
public sealed class IngestionMetricsTlsHandshakeCardinalityTests
{
    [Fact]
    public void DistinctSourceTags_AreBoundedByCap_AndTotalCountIsPreserved()
    {
        using var metrics = new IngestionMetrics();
        using var collector = new MetricCollector<long>(metrics.TlsHandshakeFailureCounter, timeProvider: null);

        var cap = IngestionMetrics.MaxTlsHandshakeFailureSourceCardinality;
        const int overflow = 50;

        for (var i = 0; i < cap + overflow; i++)
        {
            metrics.RecordTlsHandshakeFailure($"203.0.113.{i}");
        }

        var snapshot = collector.GetMeasurementSnapshot();

        // 1 件も落とさない（総件数は全て計上される）。
        Assert.Equal(cap + overflow, snapshot.Sum(m => m.Value));

        // distinct なタグ値は「上限 + overflow バケット 1」を超えない。
        var distinctTags = snapshot
            .Select(m => m.Tags["source_address"] as string)
            .Distinct()
            .ToList();
        Assert.True(
            distinctTags.Count <= cap + 1,
            $"distinct source_address タグ数 {distinctTags.Count} が上限 {cap}+1 を超えている");

        // 上限超過分は overflow バケットへ畳まれている。
        Assert.Contains(IngestionMetrics.TlsHandshakeFailureOverflowSource, distinctTags);
    }

    [Fact]
    public void KnownSourceUnderCap_KeepsItsOwnTagAcrossRepeats()
    {
        using var metrics = new IngestionMetrics();
        using var collector = new MetricCollector<long>(metrics.TlsHandshakeFailureCounter, timeProvider: null);

        metrics.RecordTlsHandshakeFailure("198.51.100.7");
        metrics.RecordTlsHandshakeFailure("198.51.100.7");

        var snapshot = collector.GetMeasurementSnapshot();
        Assert.Equal(2, snapshot.Sum(m => m.Value));
        Assert.All(snapshot, m => Assert.Equal("198.51.100.7", m.Tags["source_address"] as string));
    }

    [Fact]
    public void NullSource_IsRecordedAsUnknownTag()
    {
        using var metrics = new IngestionMetrics();
        using var collector = new MetricCollector<long>(metrics.TlsHandshakeFailureCounter, timeProvider: null);

        metrics.RecordTlsHandshakeFailure(null!);

        var measurement = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal("unknown", measurement.Tags["source_address"] as string);
    }
}
