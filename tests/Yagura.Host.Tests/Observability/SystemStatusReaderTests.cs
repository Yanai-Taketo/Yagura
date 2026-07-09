using Microsoft.Extensions.Time.Testing;
using Yagura.Abstractions.Observability;
using Yagura.Host.Observability;
using Yagura.Ingestion.Diagnostics;
using Yagura.Storage;
using Yagura.Storage.Spool;

namespace Yagura.Host.Tests.Observability;

/// <summary>
/// <see cref="SystemStatusReader.Assess"/>（ui.md §5.1 状態帯の判定。Issue #132）の検証。
/// 「消化完了」による復帰（一時保管への退避の警告は観測窓ではなくスプールの現在ゲージで判定する。
/// <see cref="SystemStatusReader"/> クラス remarks 参照）を中心に、
/// 「取りこぼし発生 → スプール空 → 復帰表示 → 再発 → 再度警告」を <see cref="FakeTimeProvider"/> で
/// 決定的に検証する（実際のスプール I/O を通す——<see cref="ActiveNotification.ActiveNotificationMonitorTests"/>
/// と同じ流儀）。
/// </summary>
public sealed class SystemStatusReaderTests : IDisposable
{
    private readonly string _spoolDirectory = Path.Combine(Path.GetTempPath(), $"yagura-status-reader-tests-{Guid.NewGuid():N}");
    private readonly List<DiskSpool> _openedSpools = [];

    public void Dispose()
    {
        foreach (var spool in _openedSpools)
        {
            spool.Dispose();
        }

        if (Directory.Exists(_spoolDirectory))
        {
            Directory.Delete(_spoolDirectory, recursive: true);
        }
    }

    [Fact]
    public void ReadCurrent_SpoolEmpty_NoHistory_IsOk()
    {
        // 基準（baseline）が無い最初の読み出しでも、スプールの現在ゲージで判定できる
        // （観測窓の増分を待たない。クラス remarks の「副次的な改善」）。
        var spool = OpenEmptySpool();
        var reader = CreateReader(spool);

        var status = reader.ReadCurrent();

        Assert.Equal(YaguraHealthKind.Ok, status.Health.Kind);
        Assert.Empty(status.Health.Reasons);
    }

    [Fact]
    public async Task ReadCurrent_UndrainedSpoolFromPreviousSession_WarnsImmediately_NoBaselineNeeded()
    {
        // 前回セッションからの未消化セグメントの持ち越し（architecture.md §1.2）を模す:
        // プロセス起動直後・最初の読み出しから、すでにスプールへデータが残っている。
        var spool = OpenEmptySpool();
        await AppendOneRecordAsync(spool);
        var reader = CreateReader(spool);

        var status = reader.ReadCurrent();

        Assert.Equal(YaguraHealthKind.Warning, status.Health.Kind);
        Assert.Contains(YaguraHealthReason.SpoolEvacuationObserved, status.Health.Reasons);
    }

    [Fact]
    public async Task EvacuationThenImmediateDrain_RecoversWithoutWaitingForObservationWindow()
    {
        // Issue #132 の核心シナリオ:退避 → すぐ drain → DB 格納完了の場合、観測窓
        // （仮値 5 分）の経過を待たずに直ちに正常表示へ戻る。
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-10T00:00:00Z"));
        var spool = OpenEmptySpool();
        var reader = CreateReader(spool, timeProvider: timeProvider);

        // 退避発生: スプールへ 1 件追記 → 警告あり。
        await AppendOneRecordAsync(spool);
        var duringEvacuation = reader.ReadCurrent();
        Assert.Equal(YaguraHealthKind.Warning, duringEvacuation.Health.Kind);
        Assert.Contains(YaguraHealthReason.SpoolEvacuationObserved, duringEvacuation.Health.Reasons);

        // 同一周期内で drain 完了（SpoolDrainCoordinator.DrainSegmentAsync と同じ手順:
        // 全バッチの DB 書き込み確定後にセグメントを削除する。§3.2.1・§3.2.4）。
        // 観測窓は 1 ミリ秒も進めていない——時間経過に依存しない復帰であることの確認。
        DrainAllSegments(spool);

        var afterDrain = reader.ReadCurrent();
        Assert.Equal(YaguraHealthKind.Ok, afterDrain.Health.Kind);
        Assert.DoesNotContain(YaguraHealthReason.SpoolEvacuationObserved, afterDrain.Health.Reasons);
    }

    [Fact]
    public async Task EvacuationRecoversThenReoccurs_WarnsAgain()
    {
        // 「取りこぼし発生 → スプール空 → 復帰表示 / 復帰後に再発 → 再度警告」の往復検証
        // （タスク依頼の検証シナリオ）。
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-10T00:00:00Z"));
        var spool = OpenEmptySpool();
        var reader = CreateReader(spool, timeProvider: timeProvider);

        // 1 回目の退避 → drain 完了 → 復帰。
        await AppendOneRecordAsync(spool);
        Assert.Contains(YaguraHealthReason.SpoolEvacuationObserved, reader.ReadCurrent().Health.Reasons);
        DrainAllSegments(spool);
        Assert.Equal(YaguraHealthKind.Ok, reader.ReadCurrent().Health.Kind);

        // 時間を進めても（観測窓を跨いでも）正常のまま——時間経過そのものでは警告が復活しない。
        timeProvider.Advance(SystemStatusReader.ObservationWindow + TimeSpan.FromMinutes(1));
        Assert.Equal(YaguraHealthKind.Ok, reader.ReadCurrent().Health.Kind);

        // 2 回目の退避が発生 → 再度警告あり（前回の復帰が「消化完了の抑制フラグ」のような
        // 恒久状態を作っていないことの確認）。
        await AppendOneRecordAsync(spool);
        var reoccurred = reader.ReadCurrent();
        Assert.Equal(YaguraHealthKind.Warning, reoccurred.Health.Kind);
        Assert.Contains(YaguraHealthReason.SpoolEvacuationObserved, reoccurred.Health.Reasons);

        // 再度 drain すれば再び復帰する。
        DrainAllSegments(spool);
        Assert.Equal(YaguraHealthKind.Ok, reader.ReadCurrent().Health.Kind);
    }

    [Fact]
    public void LossObserved_StillUsesObservationWindow_UnaffectedBySpoolState()
    {
        // 取りこぼし（破棄カウンタの増分）は「消化」に相当する正シグナルが無いため、
        // 引き続き観測窓内の増分で判定する（SpoolEvacuationObserved とは異なる設計判断。
        // クラス remarks 参照）。
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-10T00:00:00Z"));
        var spool = OpenEmptySpool();
        using var metrics = new IngestionMetrics();
        var reader = CreateReader(spool, metrics, timeProvider);

        // 初回読み出しで基準を採取(正常側に倒す——基準が無いため増分判定できない)。
        var first = reader.ReadCurrent();
        Assert.Equal(YaguraHealthKind.Ok, first.Health.Kind);

        // 観測窓内に破棄が発生。
        metrics.RecordInternalBufferDropped();
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        var duringLoss = reader.ReadCurrent();
        Assert.Equal(YaguraHealthKind.Error, duringLoss.Health.Kind);
        Assert.Contains(YaguraHealthReason.LossObserved, duringLoss.Health.Reasons);

        // 観測窓（仮値 5 分）を超えて時間が経過すれば、破棄が再発しない限り復帰する
        // （時間経過による自然な鎮静化——スプール退避とは異なる復帰メカニズム）。
        timeProvider.Advance(SystemStatusReader.ObservationWindow + TimeSpan.FromSeconds(1));
        var afterWindow = reader.ReadCurrent();
        Assert.Equal(YaguraHealthKind.Ok, afterWindow.Health.Kind);
        Assert.DoesNotContain(YaguraHealthReason.LossObserved, afterWindow.Health.Reasons);
    }

    [Fact]
    public void ReadCurrent_SpoolDisabled_SpoolEvacuationReasonNotApplicable()
    {
        // スプール無効・縮退運転中（spoolReading = null）は「未消化データの有無」自体が
        // 存在しないため、SpoolEvacuationObserved は対象外のまま（スプールなし縮退の警告は
        // 別の判定理由 SpoolDegraded が担う）。
        var reader = CreateReader(spool: null, spoolDegraded: true);

        var status = reader.ReadCurrent();

        Assert.Equal(YaguraHealthKind.Warning, status.Health.Kind);
        Assert.Contains(YaguraHealthReason.SpoolDegraded, status.Health.Reasons);
        Assert.DoesNotContain(YaguraHealthReason.SpoolEvacuationObserved, status.Health.Reasons);
    }

    // ---- ハーネス ----

    private SystemStatusReader CreateReader(
        DiskSpool? spool,
        IngestionMetrics? metrics = null,
        FakeTimeProvider? timeProvider = null,
        bool spoolDegraded = false) =>
        new(
            metrics ?? new IngestionMetrics(),
            spool,
            spoolQuotaBytes: spool is null ? 0 : long.MaxValue / 2,
            spoolDegraded: spoolDegraded,
            retentionDays: 30,
            listeners: [],
            timeProvider: timeProvider ?? new FakeTimeProvider(DateTimeOffset.Parse("2026-07-10T00:00:00Z")));

    private DiskSpool OpenEmptySpool()
    {
        var directory = Path.Combine(_spoolDirectory, $"spool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var spool = DiskSpool.TryOpen(
            new DiskSpoolOptions { Directory = directory, QuotaBytes = long.MaxValue / 2 },
            out _);
        Assert.NotNull(spool);
        _openedSpools.Add(spool);
        return spool;
    }

    private static async Task AppendOneRecordAsync(DiskSpool spool)
    {
        var result = await spool.TryAppendAsync(SpoolRecord.ForLog(SampleLogRecord()));
        Assert.Equal(SpoolAppendResult.Appended, result);
    }

    /// <summary>
    /// <c>SpoolDrainCoordinator.DrainSegmentAsync</c> と同じ手順（全バッチの DB 書き込み確定後に
    /// セグメントを削除する。§3.2.1・§3.2.4）を模す——drain の実オーケストレーションは
    /// Yagura.Ingestion 側の責務のため、本テストではその契約（消化 = 使用量 0）だけを再現する。
    /// </summary>
    private static void DrainAllSegments(DiskSpool spool)
    {
        foreach (var segmentPath in spool.TrySealActiveSegmentAndListDrainable())
        {
            spool.DeleteSegment(segmentPath);
        }

        Assert.Equal(0, spool.CurrentUsageBytes);
    }

    private static LogRecord SampleLogRecord() => new(
        ReceivedAt: new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero),
        SourceAddress: "10.0.0.1",
        SourcePort: 514,
        Protocol: Protocol.Udp,
        ParseStatus: ParseStatus.Parsed,
        Facility: 1,
        Severity: 5,
        Message: "sample");
}
