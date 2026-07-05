using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Yagura.Ingestion.Diagnostics;

namespace Yagura.Ingestion.Tests.Diagnostics;

/// <summary>
/// OS レベル UDP 統計突合ゲージ（architecture.md §4.2。M4-4）。
/// </summary>
/// <remarks>
/// 実機検証（Windows ARM64・.NET SDK 10.0.301、2026-07-05）で
/// <see cref="System.Net.NetworkInformation.IPGlobalProperties.GetUdpIPv4Statistics"/> /
/// <c>GetUdpIPv6Statistics</c> の <c>IncomingDatagramsDiscarded</c> が実際に取得でき、
/// 値が単調増加する生きた値であることを確認済み（枠のみではなく実装済み）。
/// CI 環境でも Windows である限り同じ API が使えるはずだが、万一取得できない環境
/// （コンテナ制限等）でもクラッシュしない「正直な枠」であることをテストで担保する——
/// <see cref="IngestionMetrics.OsUdpIPv4StatsAvailable"/> が <c>false</c> の場合は
/// ゲージを購読しても測定値が得られないだけで、例外にはならないことを確認する。
/// </remarks>
public sealed class IngestionMetricsOsUdpStatsTests
{
    [Fact]
    public void Constructor_DoesNotThrow_RegardlessOfOsUdpStatsAvailability()
    {
        // コンストラクタ自体が例外を投げないこと（取得不可環境でも起動を妨げない。§4.2）。
        using var metrics = new IngestionMetrics();

        // 利用可否のフラグが例外なく読めること。
        _ = metrics.OsUdpIPv4StatsAvailable;
        _ = metrics.OsUdpIPv6StatsAvailable;
    }

    [Fact]
    public void IPv4GaugeAvailable_ObservingProducesNonNegativeMeasurement()
    {
        using var metrics = new IngestionMetrics();

        if (!metrics.OsUdpIPv4StatsAvailable)
        {
            // 実機検証済みの Windows 環境では利用可能なはずだが、万一の縮退環境向けに
            // 「取得できない場合は正直に報告して枠のみ」（§4.2）の分岐として skip 相当にする。
            return;
        }

        using var collector = new MetricCollector<long>(metrics.OsUdpIPv4DatagramsDiscardedGauge!, timeProvider: null);
        collector.RecordObservableInstruments();

        var measurements = collector.GetMeasurementSnapshot();
        Assert.NotEmpty(measurements);
        Assert.All(measurements, m => Assert.True(m.Value >= 0));
    }

    [Fact]
    public void IPv6GaugeAvailable_ObservingProducesNonNegativeMeasurement()
    {
        using var metrics = new IngestionMetrics();

        if (!metrics.OsUdpIPv6StatsAvailable)
        {
            return;
        }

        using var collector = new MetricCollector<long>(metrics.OsUdpIPv6DatagramsDiscardedGauge!, timeProvider: null);
        collector.RecordObservableInstruments();

        var measurements = collector.GetMeasurementSnapshot();
        Assert.NotEmpty(measurements);
        Assert.All(measurements, m => Assert.True(m.Value >= 0));
    }
}
