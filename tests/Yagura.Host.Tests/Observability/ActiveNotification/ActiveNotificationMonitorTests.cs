using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Yagura.Host.Observability.ActiveNotification;
using Yagura.Ingestion.Diagnostics;
using Yagura.Storage;
using Yagura.Storage.Spool;

namespace Yagura.Host.Tests.Observability.ActiveNotification;

/// <summary>
/// <see cref="ActiveNotificationMonitor"/>（architecture.md §4.6。Issue #149）の周期評価を、
/// <see cref="FakeTimeProvider"/> により時刻を決定的に進めながら検証する。通知先の検証は
/// 既存の <c>RetentionTests</c>・<c>PersistenceWriter</c> と同じ流儀（<see cref="FakeLogger{T}"/> +
/// <see cref="FakeLogCollector"/> をテスト用シンクとして使う）で行う。
/// </summary>
public sealed class ActiveNotificationMonitorTests : IDisposable
{
    private readonly string _spoolDirectory = Path.Combine(Path.GetTempPath(), $"yagura-active-notification-tests-{Guid.NewGuid():N}");
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

    // ------------------------------------------------------------------
    // スプール使用量の上限接近・到達（EventId 1002 / 1003）
    // ------------------------------------------------------------------

    [Fact]
    public async Task EvaluateOnce_SpoolUsageAtNearLimitRatio_WarnsWithNearLimitEventId()
    {
        var spool = await OpenSpoolSizedForRatioAsync(currentOfTen: 8); // ratio = 0.8（閾値ちょうど）
        var collector = new FakeLogCollector();
        var monitor = CreateMonitor(spool, collector, out _);

        await monitor.EvaluateOnceAsync();

        var record = Assert.Single(collector.GetSnapshot(), r => r.Id.Id is 1002 or 1003);
        Assert.Equal(1002, record.Id.Id);
        Assert.Contains("[spool-near-limit]", record.Message);
    }

    [Fact]
    public async Task EvaluateOnce_SpoolUsageAtQuota_WarnsWithReachedEventId_NotNearLimit()
    {
        var spool = await OpenSpoolSizedForRatioAsync(currentOfTen: 10); // ratio = 1.0（到達）
        var collector = new FakeLogCollector();
        var monitor = CreateMonitor(spool, collector, out _);

        await monitor.EvaluateOnceAsync();

        var record = Assert.Single(collector.GetSnapshot(), r => r.Id.Id is 1002 or 1003);
        Assert.Equal(1003, record.Id.Id);
        Assert.Contains("[spool-quota-reached]", record.Message);
        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id == 1002);
    }

    [Fact]
    public async Task EvaluateOnce_SpoolUsageBelowNearLimitRatio_DoesNotWarn()
    {
        var spool = await OpenSpoolSizedForRatioAsync(currentOfTen: 5); // ratio = 0.5
        var collector = new FakeLogCollector();
        var monitor = CreateMonitor(spool, collector, out _);

        await monitor.EvaluateOnceAsync();

        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id is 1002 or 1003);
    }

    [Fact]
    public async Task EvaluateOnce_RepeatedWithinSuppressionWindow_WarnsOnce_ThenAgainAfterWindow()
    {
        var spool = await OpenSpoolSizedForRatioAsync(currentOfTen: 10);
        var collector = new FakeLogCollector();
        var monitor = CreateMonitor(spool, collector, out var timeProvider);

        await monitor.EvaluateOnceAsync();
        Assert.Single(collector.GetSnapshot(), r => r.Id.Id == 1003);

        // 抑制窓（15 分の仮値）内に再評価しても再発火しない。
        timeProvider.Advance(TimeSpan.FromMinutes(10));
        await monitor.EvaluateOnceAsync();
        Assert.Single(collector.GetSnapshot(), r => r.Id.Id == 1003);

        // 抑制窓を超えたら再発火する。
        timeProvider.Advance(TimeSpan.FromMinutes(10));
        await monitor.EvaluateOnceAsync();
        Assert.Equal(2, collector.GetSnapshot().Count(r => r.Id.Id == 1003));
    }

    [Fact]
    public async Task EvaluateOnce_SpoolNull_DoesNotWarnAboutUsage()
    {
        var collector = new FakeLogCollector();
        var monitor = CreateMonitor(spool: null, collector, out _);

        await monitor.EvaluateOnceAsync();

        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id is 1002 or 1003);
    }

    // ------------------------------------------------------------------
    // スプール退避の継続（EventId 1004）
    // ------------------------------------------------------------------

    [Fact]
    public async Task EvaluateOnce_EvacuationContinuesPastDuration_WarnsWithContinuingEventId()
    {
        var spool = await OpenSpoolSizedForRatioAsync(currentOfTen: 1); // 使用率は低く保つ（1002/1003 と混線しない）
        var collector = new FakeLogCollector();
        using var metrics = new IngestionMetrics();
        var monitor = CreateMonitor(spool, collector, out var timeProvider, metrics);

        // 初回呼び出しは基準値の採種のみ（増分の比較対象がまだ無い）。
        await monitor.EvaluateOnceAsync();
        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id == 1004);

        // 1 分間隔で退避が発生し続ける状況を模す。ストリーク開始は「最初に増分を観測した周期」
        // （1 分後）であり、EvacuationContinuationDuration(仮値 5 分)を満たすには
        // ストリーク開始からさらに 5 分後（＝seed から 6 回目の増分呼び出し）が必要。
        // 5 回目まで（ストリーク開始から 4 分経過）は発火しないはず。
        for (var i = 0; i < 5; i++)
        {
            timeProvider.Advance(ActiveNotificationConstants.PollInterval);
            metrics.RecordSpoolEvacuated();
            await monitor.EvaluateOnceAsync();
        }

        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id == 1004);

        // 6 回目の継続増分でストリーク開始から 5 分を満たし発火する。
        timeProvider.Advance(ActiveNotificationConstants.PollInterval);
        metrics.RecordSpoolEvacuated();
        await monitor.EvaluateOnceAsync();

        var record = Assert.Single(collector.GetSnapshot(), r => r.Id.Id == 1004);
        Assert.Contains("[spool-evacuation-continuing]", record.Message);
    }

    [Fact]
    public async Task EvaluateOnce_EvacuationStops_ResetsStreak_RequiresFullDurationAgain()
    {
        var spool = await OpenSpoolSizedForRatioAsync(currentOfTen: 1);
        var collector = new FakeLogCollector();
        using var metrics = new IngestionMetrics();
        var monitor = CreateMonitor(spool, collector, out var timeProvider, metrics);

        await monitor.EvaluateOnceAsync();

        // 4 分継続（発火閾値の 5 分未満）。
        for (var i = 0; i < 4; i++)
        {
            timeProvider.Advance(ActiveNotificationConstants.PollInterval);
            metrics.RecordSpoolEvacuated();
            await monitor.EvaluateOnceAsync();
        }

        // 1 周期だけ退避が止まる(増分なし) — ストリークがリセットされるはず。
        timeProvider.Advance(ActiveNotificationConstants.PollInterval);
        await monitor.EvaluateOnceAsync();

        // 再開後、5 分未満(4 分)ではまだ発火しない — リセットが効いていることの確認。
        for (var i = 0; i < 4; i++)
        {
            timeProvider.Advance(ActiveNotificationConstants.PollInterval);
            metrics.RecordSpoolEvacuated();
            await monitor.EvaluateOnceAsync();
        }

        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id == 1004);
    }

    // ------------------------------------------------------------------
    // データルートのボリューム空き容量（EventId 1006）
    // ------------------------------------------------------------------

    [Fact]
    public async Task EvaluateOnce_FreeSpaceBelowThreshold_Warns()
    {
        var collector = new FakeLogCollector();
        var volumeInfo = new FakeDataRootVolumeInfo(new DataRootVolumeReading(
            TotalSizeBytes: 100L * 1024 * 1024 * 1024,
            AvailableFreeSpaceBytes: ActiveNotificationConstants.DataRootFreeSpaceMinBytes - 1));
        var monitor = CreateMonitor(spool: null, collector, out _, volumeInfo: volumeInfo);

        await monitor.EvaluateOnceAsync();

        var record = Assert.Single(collector.GetSnapshot(), r => r.Id.Id == 1006);
        Assert.Contains("[data-root-disk-space-low]", record.Message);
    }

    [Fact]
    public async Task EvaluateOnce_FreeSpaceAboveThreshold_DoesNotWarn()
    {
        var collector = new FakeLogCollector();
        var volumeInfo = new FakeDataRootVolumeInfo(new DataRootVolumeReading(
            TotalSizeBytes: 100L * 1024 * 1024 * 1024,
            AvailableFreeSpaceBytes: ActiveNotificationConstants.DataRootFreeSpaceMinBytes + 1));
        var monitor = CreateMonitor(spool: null, collector, out _, volumeInfo: volumeInfo);

        await monitor.EvaluateOnceAsync();

        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id == 1006);
    }

    [Fact]
    public async Task EvaluateOnce_VolumeInfoUnavailable_DoesNotWarn()
    {
        var collector = new FakeLogCollector();
        var monitor = CreateMonitor(spool: null, collector, out _, volumeInfo: new FakeDataRootVolumeInfo(reading: null));

        await monitor.EvaluateOnceAsync();

        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id == 1006);
    }

    // ------------------------------------------------------------------
    // SQL Server Express の DB 容量接近（EventId 1007）
    // ------------------------------------------------------------------

    [Fact]
    public async Task EvaluateOnce_ExpressCapacityNearLimit_Warns()
    {
        var collector = new FakeLogCollector();
        var checker = new FakeExpressCapacityChecker(new ExpressCapacityReading(
            DatabaseSizeBytes: 9L * 1024 * 1024 * 1024,
            MaxDatabaseSizeBytes: 10L * 1024 * 1024 * 1024)); // 使用率 0.9 >= 閾値 0.8
        var monitor = CreateMonitor(spool: null, collector, out _, expressChecker: checker);

        await monitor.EvaluateOnceAsync();

        var record = Assert.Single(collector.GetSnapshot(), r => r.Id.Id == 1007);
        Assert.Contains("[express-capacity-near-limit]", record.Message);
    }

    [Fact]
    public async Task EvaluateOnce_ExpressCheckerReturnsNull_NotApplicable_DoesNotWarn()
    {
        var collector = new FakeLogCollector();
        var checker = new FakeExpressCapacityChecker(reading: null); // SQLite/非 Express provider を模す
        var monitor = CreateMonitor(spool: null, collector, out _, expressChecker: checker);

        await monitor.EvaluateOnceAsync();

        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id == 1007);
    }

    [Fact]
    public async Task EvaluateOnce_ExpressCapacityBelowThreshold_DoesNotWarn()
    {
        var collector = new FakeLogCollector();
        var checker = new FakeExpressCapacityChecker(new ExpressCapacityReading(
            DatabaseSizeBytes: 1L * 1024 * 1024 * 1024,
            MaxDatabaseSizeBytes: 10L * 1024 * 1024 * 1024)); // 使用率 0.1
        var monitor = CreateMonitor(spool: null, collector, out _, expressChecker: checker);

        await monitor.EvaluateOnceAsync();

        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id == 1007);
    }

    // ------------------------------------------------------------------
    // Start/StopAsync のライフサイクル（周期ループが実際に評価を呼ぶこと）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Start_AdvancingFakeClock_TriggersPeriodicEvaluation()
    {
        var collector = new FakeLogCollector();
        var volumeInfo = new FakeDataRootVolumeInfo(new DataRootVolumeReading(
            TotalSizeBytes: 100L * 1024 * 1024 * 1024,
            AvailableFreeSpaceBytes: ActiveNotificationConstants.DataRootFreeSpaceMinBytes - 1));
        var monitor = CreateMonitor(spool: null, collector, out var timeProvider, volumeInfo: volumeInfo);

        monitor.Start();
        try
        {
            // Start() が起こす背景タスクが最初の Task.Delay(..., timeProvider, ...) を登録する
            // までの間に Advance() を呼ぶと、その時点では待機中のタイマーが無く進行が拾われない
            // 競合があり得るため、条件が成立するまで Advance を繰り返す（固定 sleep ではなく
            // 条件ポーリング。RetentionTests の OnCapacityExhausted と同じ流儀）。
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!collector.GetSnapshot().Any(r => r.Id.Id == 1006) && DateTime.UtcNow < deadline)
            {
                timeProvider.Advance(ActiveNotificationConstants.PollInterval);
                await Task.Delay(20);
            }

            Assert.Contains(collector.GetSnapshot(), r => r.Id.Id == 1006);
        }
        finally
        {
            await monitor.StopAsync();
        }
    }

    // ------------------------------------------------------------------
    // ヘルパー
    // ------------------------------------------------------------------

    private ActiveNotificationMonitor CreateMonitor(
        DiskSpool? spool,
        FakeLogCollector collector,
        out FakeTimeProvider timeProvider,
        IngestionMetrics? metrics = null,
        IDataRootVolumeInfo? volumeInfo = null,
        IExpressCapacityChecker? expressChecker = null)
    {
        timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-09T00:00:00Z"));
        var ownedMetrics = metrics ?? new IngestionMetrics();

        return new ActiveNotificationMonitor(
            spool,
            ownedMetrics,
            volumeInfo ?? new FakeDataRootVolumeInfo(reading: null),
            expressChecker ?? new FakeExpressCapacityChecker(reading: null),
            timeProvider,
            new FakeLogger<ActiveNotificationMonitor>(collector));
    }

    /// <summary>
    /// 「10 分の N」で使用率を作れるよう、1 レコードのフレームサイズを実測してから
    /// <c>QuotaBytes = frameSize * 10</c> のスプールを開き、<paramref name="currentOfTen"/> 件を
    /// 追記する（整数倍で厳密に使用率を作るため——浮動小数の丸めに依存しない）。
    /// </summary>
    private async Task<DiskSpool> OpenSpoolSizedForRatioAsync(int currentOfTen)
    {
        // 1) フレームサイズを実測する（十分大きい上限の使い捨てスプールで 1 件追記）。
        var measureDirectory = Path.Combine(_spoolDirectory, "measure");
        Directory.CreateDirectory(measureDirectory);
        var measureSpool = DiskSpool.TryOpen(
            new DiskSpoolOptions { Directory = measureDirectory, QuotaBytes = long.MaxValue / 2 },
            out _);
        Assert.NotNull(measureSpool);
        await measureSpool.TryAppendAsync(SpoolRecord.ForLog(SampleLogRecord()));
        var frameSize = measureSpool.CurrentUsageBytes;
        measureSpool.Dispose();
        Assert.True(frameSize > 0);

        // 2) 本番用スプールを QuotaBytes = frameSize * 10 で開き、同一内容の記録を currentOfTen 件追記する。
        var targetDirectory = Path.Combine(_spoolDirectory, $"target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(targetDirectory);
        var spool = DiskSpool.TryOpen(
            new DiskSpoolOptions { Directory = targetDirectory, QuotaBytes = frameSize * 10 },
            out _);
        Assert.NotNull(spool);
        _openedSpools.Add(spool);

        for (var i = 0; i < currentOfTen; i++)
        {
            var result = await spool.TryAppendAsync(SpoolRecord.ForLog(SampleLogRecord()));
            Assert.Equal(SpoolAppendResult.Appended, result);
        }

        return spool;
    }

    private static LogRecord SampleLogRecord() => new(
        ReceivedAt: new DateTimeOffset(2026, 7, 9, 0, 0, 0, TimeSpan.Zero),
        SourceAddress: "10.0.0.1",
        SourcePort: 514,
        Protocol: Protocol.Udp,
        ParseStatus: ParseStatus.Parsed,
        Facility: 1,
        Severity: 5,
        Message: "sample");

    private sealed class FakeDataRootVolumeInfo(DataRootVolumeReading? reading) : IDataRootVolumeInfo
    {
        public DataRootVolumeReading? TryRead() => reading;
    }

    private sealed class FakeExpressCapacityChecker(ExpressCapacityReading? reading) : IExpressCapacityChecker
    {
        public Task<ExpressCapacityReading?> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(reading);
    }
}
