using System.Net;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Yagura.Host.Configuration;
using Yagura.Host.Observability.ActiveNotification;
using Yagura.Host.Observability.ActiveNotification.SourceSilence;
using Yagura.Ingestion;

namespace Yagura.Host.Tests.Observability.ActiveNotification.SourceSilence;

/// <summary>
/// <see cref="ActiveNotificationMonitor"/> の途絶評価の配線——受信断保留・回復時再アーム・
/// 警告 Detail への受信経路の状態の併記（ADR-0018 決定 3。委任 6）——のテスト。
/// 判定器単体の状態遷移は <see cref="SourceSilenceDetectorTests"/> が固定しており、
/// ここでは監視ループから見た合成挙動（プローブ観測 → 保留 → 回復 → 再アーム）を検証する。
/// </summary>
public sealed class ActiveNotificationMonitorSourceSilenceTests
{
    private static readonly ListenerAvailabilitySnapshot AllUp = new(Udp: true, Tcp: true, Tls: null);
    private static readonly ListenerAvailabilitySnapshot AllDown = new(Udp: false, Tcp: false, Tls: null);

    private sealed class Harness
    {
        internal required ActiveNotificationMonitor Monitor;
        internal required SourceActivityTracker Tracker;
        internal required FakeTimeProvider Time;
        internal required FakeLogCollector Collector;

        /// <summary>プローブが次回以降の評価で返すスナップショット。<c>null</c> でプローブ未注入相当は作れない（別ヘルパー参照）。</summary>
        internal ListenerAvailabilitySnapshot Availability = AllUp;
    }

    private static Harness Create(bool withProbe = true, params SourceSilenceWatchEntry[] entries)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-19T00:00:00Z"));
        var tracker = new SourceActivityTracker(time);
        var detector = new SourceSilenceDetector(tracker, time);
        tracker.ApplyWatchlist(entries);
        detector.ApplyWatchlist(entries);

        var collector = new FakeLogCollector();
        var harness = new Harness
        {
            Monitor = null!,
            Tracker = tracker,
            Time = time,
            Collector = collector,
        };

        harness.Monitor = new ActiveNotificationMonitor(
            spool: null,
            new Yagura.Ingestion.Diagnostics.IngestionMetrics(),
            new EmptyVolumeInfo(),
            new NullExpressCapacityChecker(),
            time,
            new FakeLogger<ActiveNotificationMonitor>(collector),
            selfTestTracker: null,
            adminHttpsCertificateProbe: null,
            ingestionTlsCertificateProbe: null,
            adminAuthFailureDefense: null,
            sourceSilenceDetector: detector,
            listenerAvailabilityProbe: withProbe ? () => harness.Availability : null);

        return harness;
    }

    private static SourceSilenceWatchEntry Entry(string address, int thresholdMinutes = 60) =>
        new(IPAddress.Parse(address), $"装置-{address}", TimeSpan.FromMinutes(thresholdMinutes), false);

    // ------------------------------------------------------------------
    // 受信断保留（決定 3——サーバ都合の受信断を装置側の途絶として通知しない）
    // ------------------------------------------------------------------

    [Fact]
    public async Task EvaluateOnce_WhileAllListenersDown_HoldsSilenceDetection()
    {
        var harness = Create(withProbe: true, Entry("192.0.2.10", thresholdMinutes: 60));
        harness.Availability = AllDown;

        harness.Time.Advance(TimeSpan.FromMinutes(61));
        await harness.Monitor.EvaluateOnceAsync();

        Assert.DoesNotContain(harness.Collector.GetSnapshot(), r => r.Id.Id == 1027);

        // 保留が続く限り、周期を重ねても遷移しない。
        harness.Time.Advance(TimeSpan.FromMinutes(30));
        await harness.Monitor.EvaluateOnceAsync();
        Assert.DoesNotContain(harness.Collector.GetSnapshot(), r => r.Id.Id == 1027);
    }

    [Fact]
    public async Task EvaluateOnce_AfterReceptionRecovery_RearmsAndRedetectsByEntryThreshold()
    {
        var harness = Create(withProbe: true, Entry("192.0.2.10", thresholdMinutes: 60));

        // 全リスナ受信不能の間に閾値超過が起きる。
        harness.Availability = AllDown;
        harness.Time.Advance(TimeSpan.FromMinutes(61));
        await harness.Monitor.EvaluateOnceAsync();
        Assert.DoesNotContain(harness.Collector.GetSnapshot(), r => r.Id.Id == 1027);

        // 回復の観測で再アームされ、即発火しない（起動時の再アームと同一規則）。
        harness.Availability = AllUp;
        await harness.Monitor.EvaluateOnceAsync();
        Assert.DoesNotContain(harness.Collector.GetSnapshot(), r => r.Id.Id == 1027);
        Assert.Contains(harness.Collector.GetSnapshot(), r => r.Message.Contains("再アーム"));

        // 回復から当該エントリの閾値が経過するまで受信がなければ、通常どおり発火する。
        harness.Time.Advance(TimeSpan.FromMinutes(61));
        await harness.Monitor.EvaluateOnceAsync();
        Assert.Single(harness.Collector.GetSnapshot(), r => r.Id.Id == 1027);
    }

    [Fact]
    public async Task EvaluateOnce_RecoveredEntryReceivingAgain_DoesNotFire()
    {
        var harness = Create(withProbe: true, Entry("192.0.2.10", thresholdMinutes: 60));

        harness.Availability = AllDown;
        harness.Time.Advance(TimeSpan.FromMinutes(61));
        await harness.Monitor.EvaluateOnceAsync();

        // 回復後に実受信が戻った装置は、そのまま健在として扱われる（偽陽性を作らない）。
        harness.Availability = AllUp;
        await harness.Monitor.EvaluateOnceAsync();
        harness.Time.Advance(TimeSpan.FromMinutes(30));
        harness.Tracker.RecordActivity(IPAddress.Parse("192.0.2.10"));
        harness.Time.Advance(TimeSpan.FromMinutes(45));
        await harness.Monitor.EvaluateOnceAsync();

        Assert.DoesNotContain(harness.Collector.GetSnapshot(), r => r.Id.Id == 1027);
    }

    // ------------------------------------------------------------------
    // 部分受信断は保留しない（決定 3——Detail 併記で対応する）
    // ------------------------------------------------------------------

    [Fact]
    public async Task EvaluateOnce_PartialListenerDown_DoesNotHold_AndAppendsReceptionPathToDetail()
    {
        var harness = Create(withProbe: true, Entry("192.0.2.10", thresholdMinutes: 60));
        harness.Availability = new ListenerAvailabilitySnapshot(Udp: false, Tcp: true, Tls: null);

        harness.Time.Advance(TimeSpan.FromMinutes(61));
        await harness.Monitor.EvaluateOnceAsync();

        var record = Assert.Single(harness.Collector.GetSnapshot(), r => r.Id.Id == 1027);
        Assert.Contains("UDP=受信不能", record.Message);
        Assert.Contains("TCP=受信中", record.Message);
        Assert.Contains("TLS=未構成", record.Message);
    }

    [Fact]
    public async Task EvaluateOnce_WithoutProbe_DoesNotHold_AndReportsPathAsUnknown()
    {
        // プローブ未注入（テスト・部分結線）の間は従来どおり保留しない——保留しないことで起きる
        // 偽陽性は「観測が欠けない側」に倒れている（第 4 段前半までの許容挙動と同じ）。
        var harness = Create(withProbe: false, Entry("192.0.2.10", thresholdMinutes: 60));

        harness.Time.Advance(TimeSpan.FromMinutes(61));
        await harness.Monitor.EvaluateOnceAsync();

        var record = Assert.Single(harness.Collector.GetSnapshot(), r => r.Id.Id == 1027);
        Assert.Contains("不明", record.Message);
    }

    // ------------------------------------------------------------------
    // ヘルパー
    // ------------------------------------------------------------------

    private sealed class EmptyVolumeInfo : IMonitoredVolumeInfo
    {
        public IReadOnlyList<MonitoredVolumeReading> ReadMonitoredVolumes() => [];
    }

    private sealed class NullExpressCapacityChecker : IExpressCapacityChecker
    {
        public Task<ExpressCapacityReading?> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ExpressCapacityReading?>(null);
    }
}
