using Microsoft.Extensions.Logging;
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
    // 監視対象ボリュームの空き容量（EventId 1006）
    // ------------------------------------------------------------------

    [Fact]
    public async Task EvaluateOnce_FreeSpaceBelowThreshold_Warns()
    {
        var collector = new FakeLogCollector();
        var volumeInfo = new FakeMonitoredVolumeInfo(new MonitoredVolumeReading(
            VolumeRoot: @"C:\",
            TotalSizeBytes: 100L * 1024 * 1024 * 1024,
            AvailableFreeSpaceBytes: ActiveNotificationConstants.MonitoredVolumeFreeSpaceMinBytes - 1));
        var monitor = CreateMonitor(spool: null, collector, out _, volumeInfo: volumeInfo);

        await monitor.EvaluateOnceAsync();

        var record = Assert.Single(collector.GetSnapshot(), r => r.Id.Id == 1006);
        Assert.Contains("[volume-free-space-low]", record.Message);
        Assert.Contains(@"C:\", record.Message);
    }

    [Fact]
    public async Task EvaluateOnce_FreeSpaceAboveThreshold_DoesNotWarn()
    {
        var collector = new FakeLogCollector();
        var volumeInfo = new FakeMonitoredVolumeInfo(new MonitoredVolumeReading(
            VolumeRoot: @"C:\",
            TotalSizeBytes: 100L * 1024 * 1024 * 1024,
            AvailableFreeSpaceBytes: ActiveNotificationConstants.MonitoredVolumeFreeSpaceMinBytes + 1));
        var monitor = CreateMonitor(spool: null, collector, out _, volumeInfo: volumeInfo);

        await monitor.EvaluateOnceAsync();

        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id == 1006);
    }

    [Fact]
    public async Task EvaluateOnce_VolumeInfoUnavailable_DoesNotWarn()
    {
        var collector = new FakeLogCollector();
        var monitor = CreateMonitor(spool: null, collector, out _, volumeInfo: new FakeMonitoredVolumeInfo());

        await monitor.EvaluateOnceAsync();

        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id == 1006);
    }

    [Fact]
    public async Task EvaluateOnce_TwoVolumesBothBelowThreshold_WarnsPerVolume_WithIndependentSuppression()
    {
        // データルートとスプールが別ドライブに向いた構成（PR #188 レビュー指摘のシナリオ）を模す。
        var collector = new FakeLogCollector();
        var volumeInfo = new FakeMonitoredVolumeInfo(
            new MonitoredVolumeReading(@"C:\", 100L * 1024 * 1024 * 1024, ActiveNotificationConstants.MonitoredVolumeFreeSpaceMinBytes - 1),
            new MonitoredVolumeReading(@"D:\", 200L * 1024 * 1024 * 1024, ActiveNotificationConstants.MonitoredVolumeFreeSpaceMinBytes - 1));
        var monitor = CreateMonitor(spool: null, collector, out var timeProvider, volumeInfo: volumeInfo);

        await monitor.EvaluateOnceAsync();

        // ボリュームごとに 1 件ずつ警告される（抑制窓が相互に干渉しない）。
        var records = collector.GetSnapshot().Where(r => r.Id.Id == 1006).ToList();
        Assert.Equal(2, records.Count);
        Assert.Contains(records, r => r.Message.Contains(@"C:\"));
        Assert.Contains(records, r => r.Message.Contains(@"D:\"));

        // 抑制窓内の再評価では両ボリュームとも再発火しない。
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await monitor.EvaluateOnceAsync();
        Assert.Equal(2, collector.GetSnapshot().Count(r => r.Id.Id == 1006));

        // 抑制窓を超えたら両ボリュームとも再発火する。
        timeProvider.Advance(ActiveNotificationConstants.SuppressionWindow);
        await monitor.EvaluateOnceAsync();
        Assert.Equal(4, collector.GetSnapshot().Count(r => r.Id.Id == 1006));
    }

    [Fact]
    public void MonitoredVolumeInfo_TwoPathsOnSameVolume_DeduplicatesToSingleReading()
    {
        // 既定構成（スプールはデータルート配下 = 同一ボリューム）で警告が二重発火しないことの
        // 実体側の検証: 同一ドライブ上の 2 パスは 1 件の読み取りに畳まれる。
        var pathA = Path.GetTempPath();
        var pathB = Path.Combine(Path.GetTempPath(), "spool-subdir");

        var volumeInfo = new MonitoredVolumeInfo(pathA, pathB);
        var readings = volumeInfo.ReadMonitoredVolumes();

        var reading = Assert.Single(readings);
        Assert.Equal(Path.GetPathRoot(Path.GetFullPath(pathA)), reading.VolumeRoot);
        Assert.True(reading.TotalSizeBytes > 0);
    }

    // ------------------------------------------------------------------
    // 周期ループの例外保護（評価中の例外でループが死なないこと。PR #188 レビュー指摘）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Start_EvaluationThrows_LoopSurvivesAndLogsError_ThenRecoversOnNextCycles()
    {
        var collector = new FakeLogCollector();

        // 最初の 2 周期は評価が例外を投げ、3 周期目から正常な読み取り（閾値未満の空き容量）を
        // 返す変異フェイク——ループが例外で死んでいれば 1006 警告は永遠に出ない。
        var throwsRemaining = 2;
        var volumeInfo = new MutableMonitoredVolumeInfo(() =>
        {
            if (throwsRemaining > 0)
            {
                throwsRemaining--;
                throw new InvalidOperationException("simulated evaluation failure");
            }

            return
            [
                new MonitoredVolumeReading(@"C:\", 100L * 1024 * 1024 * 1024, ActiveNotificationConstants.MonitoredVolumeFreeSpaceMinBytes - 1),
            ];
        });
        var monitor = CreateMonitor(spool: null, collector, out var timeProvider, volumeInfo: volumeInfo);

        monitor.Start();
        try
        {
            // 条件ポーリングで時計を進める（Start_AdvancingFakeClock_... と同じ流儀）。
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!collector.GetSnapshot().Any(r => r.Id.Id == 1006) && DateTime.UtcNow < deadline)
            {
                timeProvider.Advance(ActiveNotificationConstants.PollInterval);
                await Task.Delay(20);
            }

            // 例外を跨いでループが生き残り、正常化後の周期で警告が出た。
            Assert.Contains(collector.GetSnapshot(), r => r.Id.Id == 1006);

            // 例外発生時にはエラーログ（EventId 1008・機械照合トークン付き）が残っている。
            Assert.Contains(
                collector.GetSnapshot(),
                r => r.Level == LogLevel.Error && r.Id.Id == 1008 &&
                     r.Message.Contains("[active-notification-evaluation-failed]"));
        }
        finally
        {
            await monitor.StopAsync();
        }
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
    // スプールの定期自己検証（EventId 1009。architecture.md §3.2.5・Issue #152）
    // ------------------------------------------------------------------

    [Fact]
    public async Task EvaluateOnce_FirstCall_InjectsSelfTestRecord_AndDrainAcknowledgesBeforeTimeout_DoesNotWarn()
    {
        var spool = await OpenSpoolSizedForRatioAsync(currentOfTen: 0);
        var collector = new FakeLogCollector();
        var tracker = new SpoolSelfTestTracker();
        var monitor = CreateMonitor(spool, collector, out var timeProvider, selfTestTracker: tracker);

        await monitor.EvaluateOnceAsync();

        // 1 回目の評価で投入される（初回は基準値の採種を待たず即座に投入する設計）。
        Assert.True(spool.CurrentUsageBytes > 0);
        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id == 1009);

        // drain が実際に読んで照合したことを模す: 投入したマーカーで通知する。
        var segments = spool.TrySealActiveSegmentAndListDrainable();
        var segment = Assert.Single(segments);
        var records = spool.ReadSegmentRecords(segment, out _, out _);
        var selfTestRecord = Assert.Single(records, r => r.Kind == SpoolRecordKind.SelfTest);
        tracker.OnSelfTestRecordDrained(selfTestRecord.SelfTestMarker!);

        // タイムアウト未満で再評価しても警告は出ない。
        timeProvider.Advance(ActiveNotificationConstants.SelfTestTimeout - TimeSpan.FromSeconds(1));
        await monitor.EvaluateOnceAsync();

        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id == 1009);
    }

    [Fact]
    public async Task EvaluateOnce_SelfTestNeverDrained_TimesOut_WarnsWithEventId1009()
    {
        var spool = await OpenSpoolSizedForRatioAsync(currentOfTen: 0);
        var collector = new FakeLogCollector();
        var tracker = new SpoolSelfTestTracker();
        var monitor = CreateMonitor(spool, collector, out var timeProvider, selfTestTracker: tracker);

        await monitor.EvaluateOnceAsync();
        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id == 1009);

        // drain が一切読まないまま（照合されないまま）タイムアウト時間が経過する。
        timeProvider.Advance(ActiveNotificationConstants.SelfTestTimeout);
        await monitor.EvaluateOnceAsync();

        var record = Assert.Single(collector.GetSnapshot(), r => r.Id.Id == 1009);
        Assert.Contains("[spool-self-test-timeout]", record.Message);
        Assert.Equal(LogLevel.Error, record.Level);
    }

    [Fact]
    public async Task EvaluateOnce_SelfTestTimeout_RepeatedWithinSuppressionWindow_WarnsOnce_ThenAgainAfterWindow()
    {
        var spool = await OpenSpoolSizedForRatioAsync(currentOfTen: 0);
        var collector = new FakeLogCollector();
        var tracker = new SpoolSelfTestTracker();
        var monitor = CreateMonitor(spool, collector, out var timeProvider, selfTestTracker: tracker);

        await monitor.EvaluateOnceAsync();

        timeProvider.Advance(ActiveNotificationConstants.SelfTestTimeout);
        await monitor.EvaluateOnceAsync();
        Assert.Single(collector.GetSnapshot(), r => r.Id.Id == 1009);

        // 抑制窓内の再評価では再発火しない。
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await monitor.EvaluateOnceAsync();
        Assert.Single(collector.GetSnapshot(), r => r.Id.Id == 1009);

        // 抑制窓を超えたら再発火する。
        timeProvider.Advance(ActiveNotificationConstants.SuppressionWindow);
        await monitor.EvaluateOnceAsync();
        Assert.Equal(2, collector.GetSnapshot().Count(r => r.Id.Id == 1009));
    }

    [Fact]
    public async Task EvaluateOnce_SelfTestIntervalNotYetElapsed_DoesNotInjectAgain()
    {
        var spool = await OpenSpoolSizedForRatioAsync(currentOfTen: 0);
        var collector = new FakeLogCollector();
        var tracker = new SpoolSelfTestTracker();
        var monitor = CreateMonitor(spool, collector, out var timeProvider, selfTestTracker: tracker);

        await monitor.EvaluateOnceAsync();
        var usageAfterFirstInjection = spool.CurrentUsageBytes;
        Assert.True(usageAfterFirstInjection > 0);

        // 周期（仮値 1 日）未満の経過では再投入しない——drain していないため使用量は変化しない。
        timeProvider.Advance(ActiveNotificationConstants.SelfTestInterval - TimeSpan.FromMinutes(1));
        await monitor.EvaluateOnceAsync();

        Assert.Equal(usageAfterFirstInjection, spool.CurrentUsageBytes);
    }

    [Fact]
    public async Task EvaluateOnce_SpoolWriteFails_WarnsImmediatelyOnce_AndNeverFiresTimeoutNotificationAfterwards()
    {
        var collector = new FakeLogCollector();
        var tracker = new SpoolSelfTestTracker();

        // 上限 0 バイトのスプール——自己検証レコードの投入自体が QuotaExceeded で必ず失敗する。
        var directory = Path.Combine(_spoolDirectory, $"zero-quota-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var spool = DiskSpool.TryOpen(new DiskSpoolOptions { Directory = directory, QuotaBytes = 0 }, out _);
        Assert.NotNull(spool);
        _openedSpools.Add(spool);

        var monitor = CreateMonitor(spool, collector, out var timeProvider, selfTestTracker: tracker);

        await monitor.EvaluateOnceAsync();

        // タイムアウト（仮値 10 分）を待たず、投入失敗の時点で即座に警告する。
        var record = Assert.Single(collector.GetSnapshot(), r => r.Id.Id == 1009);
        Assert.Contains("[spool-self-test-write-failed]", record.Message);

        // 書込失敗したマーカーは未照合登録から取り消されるため、以降の周期評価で
        // タイムアウト通知（別トリガキー）が後追いで発火しない（PR #200 レビュー指摘への対応——
        // 修正前は書込失敗の約 10 分後からタイムアウト通知が抑制窓ごとに次回投入まで反復していた。
        // タイムアウト + 抑制窓数回分を 1 分周期で進めて確認する）。
        for (var i = 0; i < 60; i++)
        {
            timeProvider.Advance(ActiveNotificationConstants.PollInterval);
            await monitor.EvaluateOnceAsync();
        }

        var records = collector.GetSnapshot().Where(r => r.Id.Id == 1009).ToList();
        var single = Assert.Single(records);
        Assert.Contains("[spool-self-test-write-failed]", single.Message);
        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Message.Contains("[spool-self-test-timeout]"));
    }

    [Fact]
    public async Task EvaluateOnce_SpoolNull_DoesNotInjectOrWarnAboutSelfTest()
    {
        var collector = new FakeLogCollector();
        var monitor = CreateMonitor(spool: null, collector, out var timeProvider);

        await monitor.EvaluateOnceAsync();
        timeProvider.Advance(ActiveNotificationConstants.SelfTestInterval + ActiveNotificationConstants.SelfTestTimeout);
        await monitor.EvaluateOnceAsync();

        // スプールなし（opt-out・縮退運転のいずれか）では自己検証自体を行わない——
        // 既に別の通知（1001）でカバー済みの状態を重ねて警告しない。
        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id == 1009);
    }

    [Fact]
    public async Task EvaluateOnce_SpoolPresentButTrackerNotProvided_DoesNotInjectOrWarn()
    {
        // Program.cs の配線では spool と selfTestTracker は同時に null/非 null になる想定だが、
        // 本クラス自体は selfTestTracker のみ null という構成でも安全側（無評価）に倒れることを
        // 確認する。
        var spool = await OpenSpoolSizedForRatioAsync(currentOfTen: 0);
        var collector = new FakeLogCollector();
        var monitor = CreateMonitor(spool, collector, out var timeProvider, selfTestTracker: null);

        await monitor.EvaluateOnceAsync();
        timeProvider.Advance(ActiveNotificationConstants.SelfTestInterval + ActiveNotificationConstants.SelfTestTimeout);
        await monitor.EvaluateOnceAsync();

        Assert.Equal(0, spool.CurrentUsageBytes);
        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id == 1009);
    }

    // ------------------------------------------------------------------
    // Start/StopAsync のライフサイクル（周期ループが実際に評価を呼ぶこと）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Start_AdvancingFakeClock_TriggersPeriodicEvaluation()
    {
        var collector = new FakeLogCollector();
        var volumeInfo = new FakeMonitoredVolumeInfo(new MonitoredVolumeReading(
            VolumeRoot: @"C:\",
            TotalSizeBytes: 100L * 1024 * 1024 * 1024,
            AvailableFreeSpaceBytes: ActiveNotificationConstants.MonitoredVolumeFreeSpaceMinBytes - 1));
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
        IMonitoredVolumeInfo? volumeInfo = null,
        IExpressCapacityChecker? expressChecker = null,
        SpoolSelfTestTracker? selfTestTracker = null)
    {
        timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-09T00:00:00Z"));
        var ownedMetrics = metrics ?? new IngestionMetrics();

        return new ActiveNotificationMonitor(
            spool,
            ownedMetrics,
            volumeInfo ?? new FakeMonitoredVolumeInfo(),
            expressChecker ?? new FakeExpressCapacityChecker(reading: null),
            timeProvider,
            new FakeLogger<ActiveNotificationMonitor>(collector),
            selfTestTracker);
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

    /// <summary>固定の読み取り結果を返すフェイク（引数なし = 取得不能 = 空リスト）。</summary>
    private sealed class FakeMonitoredVolumeInfo(params MonitoredVolumeReading[] readings) : IMonitoredVolumeInfo
    {
        public IReadOnlyList<MonitoredVolumeReading> ReadMonitoredVolumes() => readings;
    }

    /// <summary>呼び出しごとに挙動を差し替えられるフェイク（例外送出 → 正常化の遷移を模す）。</summary>
    private sealed class MutableMonitoredVolumeInfo(Func<IReadOnlyList<MonitoredVolumeReading>> behavior) : IMonitoredVolumeInfo
    {
        public IReadOnlyList<MonitoredVolumeReading> ReadMonitoredVolumes() => behavior();
    }

    private sealed class FakeExpressCapacityChecker(ExpressCapacityReading? reading) : IExpressCapacityChecker
    {
        public Task<ExpressCapacityReading?> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(reading);
    }
}
