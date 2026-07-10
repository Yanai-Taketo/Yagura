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
    // 自己検証タイムアウトのバックログ起因判別（EventId 1010。Issue #202。
    // PR #200 レビューのフォローアップ）
    // ------------------------------------------------------------------

    [Fact]
    public async Task EvaluateOnce_SelfTestTimeout_WithRecentDrainProgress_WarnsWithBacklogEventId1010_NotFailure1009()
    {
        // 高負荷滞留シナリオ: 投入時点で未消化バックログが深く、drain は生きていて実際に
        // セグメントを消化しているが、FIFO のため自己検証マーカーの合流が期待時間に間に合わない。
        var spool = await OpenSpoolWithLargeQuotaAsync();
        var backlogSegmentA = await AppendAndSealSegmentAsync(spool);
        _ = await AppendAndSealSegmentAsync(spool); // 未削除のまま残す 2 件目の未消化セグメント。

        var collector = new FakeLogCollector();
        var tracker = new SpoolSelfTestTracker();
        var monitor = CreateMonitor(spool, collector, out var timeProvider, selfTestTracker: tracker);

        // t=0: マーカーを投入する（バックログの末尾に置かれ、drain が追いつくまで合流しない）。
        await monitor.EvaluateOnceAsync();
        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id is 1009 or 1010);

        // t=5分: drain が先頭セグメント（マーカーより古いバックログ）を 1 件消化・削除しつつ、
        // 同じ観測窓内に実トラフィックの退避（追記）が消化量を上回って続く状況を模す——
        // 使用量は純増する（§3.2.2 の「持続的な速度不足」の定義そのもの。PR #211 レビュー指摘への
        // 対応——使用量の純増減サンプリングではこのシナリオで進捗が観測できず 1009 に誤分類される。
        // マーカー自体はまだ照合しない。FIFO でまだ手前に未消化分が残っている想定）。
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        var usageBeforeWindow = spool.CurrentUsageBytes;
        spool.DeleteSegment(backlogSegmentA);
        _ = await AppendAndSealSegmentAsync(spool);
        _ = await AppendAndSealSegmentAsync(spool);
        Assert.True(spool.CurrentUsageBytes > usageBeforeWindow); // 追記速度 > 消化速度の成立確認。
        await monitor.EvaluateOnceAsync();
        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id is 1009 or 1010);

        // t=10分（期待時間ちょうど）: マーカーはまだ未照合だが、直近（5 分前）に drain の進捗
        // （消化済みセグメント削除の累積カウンタの増分）を観測しているため、使用量が純増していても
        // 経路障害ではなくバックログの滞留と判定される。
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await monitor.EvaluateOnceAsync();

        var record = Assert.Single(collector.GetSnapshot(), r => r.Id.Id is 1009 or 1010);
        Assert.Equal(1010, record.Id.Id);
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Contains("[spool-self-test-timeout-backlog]", record.Message);
    }

    [Fact]
    public async Task EvaluateOnce_SelfTestTimeout_BacklogPresentButNeverDrains_WarnsWithFailureEventId1009()
    {
        // 真の経路障害シナリオ: バックログが存在し、追記（実トラフィックの退避）も続いている点は
        // 高負荷滞留と同じだが、drain が一切進まない（セグメントが 1 件も消化・削除されない）。
        // 「バックログの存在」や「追記の継続」だけでは判定を和らげず、「消化の進捗」の有無で
        // 判別することの確認。
        var spool = await OpenSpoolWithLargeQuotaAsync();
        _ = await AppendAndSealSegmentAsync(spool);
        _ = await AppendAndSealSegmentAsync(spool);

        var collector = new FakeLogCollector();
        var tracker = new SpoolSelfTestTracker();
        var monitor = CreateMonitor(spool, collector, out var timeProvider, selfTestTracker: tracker);

        await monitor.EvaluateOnceAsync();

        // drain 側で一切セグメントを消化しないまま、追記だけが続いて期待時間が経過する
        // （追記は進捗と誤認されないことの確認を兼ねる）。
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        _ = await AppendAndSealSegmentAsync(spool);
        await monitor.EvaluateOnceAsync();

        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await monitor.EvaluateOnceAsync();

        var record = Assert.Single(collector.GetSnapshot(), r => r.Id.Id is 1009 or 1010);
        Assert.Equal(1009, record.Id.Id);
        Assert.Equal(LogLevel.Error, record.Level);
        Assert.Contains("[spool-self-test-timeout]", record.Message);
    }

    [Fact]
    public async Task EvaluateOnce_SelfTestTimeout_DrainProgressStalls_EscalatesFromBacklogToFailure()
    {
        // 進捗停止シナリオ: 最初は drain が進んでいて「バックログの滞留」（1010）と判定されるが、
        // その後 drain の進捗が完全に途絶えると、進捗が「直近」と呼べなくなった時点で経路障害の
        // 疑い（1009）へ切り替わる——判定を先送りし続けて実障害の検知が沈黙しないことの確認。
        var spool = await OpenSpoolWithLargeQuotaAsync();
        var backlogSegmentA = await AppendAndSealSegmentAsync(spool);

        var collector = new FakeLogCollector();
        var tracker = new SpoolSelfTestTracker();
        var monitor = CreateMonitor(spool, collector, out var timeProvider, selfTestTracker: tracker);

        // t=0: マーカー投入。
        await monitor.EvaluateOnceAsync();

        // t=5分: 1 度だけ drain の進捗を観測させる。
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        spool.DeleteSegment(backlogSegmentA);
        await monitor.EvaluateOnceAsync();

        // t=10分: 直近（5 分前）の進捗によりバックログ判定（1010）。
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await monitor.EvaluateOnceAsync();
        Assert.Contains(collector.GetSnapshot(), r => r.Id.Id == 1010);
        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id == 1009);

        // それ以降、drain の進捗が一切再発しないまま十分な時間が経過する
        // （進捗観測から期待時間 = 10 分を超えて「直近」と呼べなくなる）。
        timeProvider.Advance(TimeSpan.FromMinutes(10));
        await monitor.EvaluateOnceAsync();

        Assert.Contains(collector.GetSnapshot(), r => r.Id.Id == 1009);
    }

    [Fact]
    public async Task EvaluateOnce_SelfTestEscalatedTo1009_SingleProgressDoesNotRevertTo1010_UntilNextMarkerResets()
    {
        // 振動（flapping）防止シナリオ（PR #211 レビュー指摘への対応）: 一度 1009（経路障害の
        // 疑い）へエスカレートした後は、単発の進捗が観測されても当該マーカーの追跡が終わるまで
        // 1010 へ戻さない（ラッチ）。次のマーカー投入でラッチが解除され、新しい検証は白紙から
        // 1010 判定に戻れることも確認する。
        var spool = await OpenSpoolWithLargeQuotaAsync();
        var backlogSegmentA = await AppendAndSealSegmentAsync(spool);
        var backlogSegmentB = await AppendAndSealSegmentAsync(spool);

        var collector = new FakeLogCollector();
        var tracker = new SpoolSelfTestTracker();
        var monitor = CreateMonitor(spool, collector, out var timeProvider, selfTestTracker: tracker);

        // t=0: マーカー A を投入。
        await monitor.EvaluateOnceAsync();

        // t=10分: 進捗なしでタイムアウト → 1009 発火（ラッチ成立）。
        timeProvider.Advance(ActiveNotificationConstants.SelfTestTimeout);
        await monitor.EvaluateOnceAsync();
        Assert.Single(collector.GetSnapshot(), r => r.Id.Id == 1009);

        // t=11分: 単発の進捗（drain がセグメントを 1 件消化）が観測される——ラッチにより 1010 へは
        // 戻らない（1009 は抑制窓内のため再発火もしない）。
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        spool.DeleteSegment(backlogSegmentA);
        await monitor.EvaluateOnceAsync();
        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id == 1010);

        // t=26分（抑制窓 15 分の経過後）: 依然としてラッチが効いており、直近に進捗があっても
        // 1010 ではなく 1009 が再発火する（同一の根本原因に対して 2 つの ID が交互に発火しない）。
        timeProvider.Advance(TimeSpan.FromMinutes(15));
        await monitor.EvaluateOnceAsync();
        Assert.Equal(2, collector.GetSnapshot().Count(r => r.Id.Id == 1009));
        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id == 1010);

        // t=1日: 次のマーカー B の投入でラッチが解除される（投入直前の判定でマーカー A の 1009 が
        // もう一度発火し得るのは従来どおり）。
        timeProvider.Advance(ActiveNotificationConstants.SelfTestInterval - TimeSpan.FromMinutes(26));
        await monitor.EvaluateOnceAsync();

        // マーカー B の窓で進捗を観測させ、タイムアウトに至る——ラッチ解除により白紙から
        // 1010（バックログの滞留）と判定される。
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        spool.DeleteSegment(backlogSegmentB);
        await monitor.EvaluateOnceAsync();

        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await monitor.EvaluateOnceAsync();

        Assert.Single(collector.GetSnapshot(), r => r.Id.Id == 1010);
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
    // 管理リスナのリモート HTTPS 証明書の周期監視（EventId 1014 / 1015。
    // ADR-0010 Phase 2 決定 4。PR #224 レビュー指摘 #2・#3）
    // ------------------------------------------------------------------

    [Fact]
    public async Task EvaluateOnce_AdminHttpsCertificateProbeNotWired_DoesNotWarn()
    {
        // リモートバインド opt-in が無効（プローブ未注入）の既定構成では評価自体を行わない。
        var collector = new FakeLogCollector();
        var monitor = CreateMonitor(spool: null, collector, out _);

        await monitor.EvaluateOnceAsync();

        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id is 1014 or 1015);
    }

    [Fact]
    public async Task EvaluateOnce_AdminHttpsCertificateHealthyAndFarFromExpiry_DoesNotWarn()
    {
        var collector = new FakeLogCollector();
        FakeTimeProvider timeProvider = null!;
        var monitor = CreateMonitor(spool: null, collector, out timeProvider,
            adminHttpsCertificateProbe: new FakeAdminHttpsCertificateStatusProbe(() =>
                new AdminHttpsCertificateStatus(
                    IsAvailable: true,
                    NotAfter: timeProvider.GetUtcNow().AddDays(365),
                    FailureReason: null)));

        await monitor.EvaluateOnceAsync();

        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id is 1014 or 1015);
    }

    [Fact]
    public async Task EvaluateOnce_AdminHttpsCertificateExpiryWithinWarningWindow_WarnsWithApproachingEventId()
    {
        var collector = new FakeLogCollector();
        FakeTimeProvider timeProvider = null!;
        var monitor = CreateMonitor(spool: null, collector, out timeProvider,
            adminHttpsCertificateProbe: new FakeAdminHttpsCertificateStatusProbe(() =>
                new AdminHttpsCertificateStatus(
                    IsAvailable: true,
                    NotAfter: timeProvider.GetUtcNow().AddDays(10), // 閾値(仮値 30 日)以内
                    FailureReason: null)));

        await monitor.EvaluateOnceAsync();

        var record = Assert.Single(collector.GetSnapshot(), r => r.Id.Id is 1014 or 1015);
        Assert.Equal(1014, record.Id.Id);
        Assert.Contains("[admin-https-certificate-expiry-approaching]", record.Message);
    }

    [Fact]
    public async Task EvaluateOnce_AdminHttpsCertificateExpired_WarnsWithUnavailableEventId_NotApproaching()
    {
        // 稼働中に期限切れへ遷移した状態(ServerCertificateSelector が新規ハンドシェイクを
        // 拒否している)は 1015 で通知し、1014(接近)とは区別する。
        var collector = new FakeLogCollector();
        FakeTimeProvider timeProvider = null!;
        var monitor = CreateMonitor(spool: null, collector, out timeProvider,
            adminHttpsCertificateProbe: new FakeAdminHttpsCertificateStatusProbe(() =>
                new AdminHttpsCertificateStatus(
                    IsAvailable: true,
                    NotAfter: timeProvider.GetUtcNow().AddDays(-1), // 既に期限切れ
                    FailureReason: null)));

        await monitor.EvaluateOnceAsync();

        var record = Assert.Single(collector.GetSnapshot(), r => r.Id.Id is 1014 or 1015);
        Assert.Equal(1015, record.Id.Id);
        Assert.Contains("[admin-https-certificate-unavailable-while-running]", record.Message);
    }

    [Fact]
    public async Task EvaluateOnce_AdminHttpsCertificateRemovedFromStoreWhileRunning_WarnsWithUnavailableEventId()
    {
        // 稼働中の遷移を模す: 1 周期目は健全(警告なし)、2 周期目でストアから削除された状態へ。
        var collector = new FakeLogCollector();
        FakeTimeProvider timeProvider = null!;
        var available = true;
        var monitor = CreateMonitor(spool: null, collector, out timeProvider,
            adminHttpsCertificateProbe: new FakeAdminHttpsCertificateStatusProbe(() =>
                available
                    ? new AdminHttpsCertificateStatus(true, timeProvider.GetUtcNow().AddDays(365), null)
                    : new AdminHttpsCertificateStatus(false, default, "拇印 XXXX の証明書が LocalMachine\\My ストアに見つかりません。")));

        await monitor.EvaluateOnceAsync();
        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id is 1014 or 1015);

        available = false;
        timeProvider.Advance(ActiveNotificationConstants.PollInterval);
        await monitor.EvaluateOnceAsync();

        var record = Assert.Single(collector.GetSnapshot(), r => r.Id.Id == 1015);
        Assert.Contains("見つかりません", record.Message);
    }

    [Fact]
    public async Task EvaluateOnce_AdminHttpsCertificateWarnings_AreSuppressedWithinWindow()
    {
        // 抑制窓(15 分の仮値)の適用——他トリガと同じ NotifyIfDue に乗ること。
        var collector = new FakeLogCollector();
        FakeTimeProvider timeProvider = null!;
        var monitor = CreateMonitor(spool: null, collector, out timeProvider,
            adminHttpsCertificateProbe: new FakeAdminHttpsCertificateStatusProbe(() =>
                new AdminHttpsCertificateStatus(true, timeProvider.GetUtcNow().AddDays(10), null)));

        await monitor.EvaluateOnceAsync();
        Assert.Single(collector.GetSnapshot(), r => r.Id.Id == 1014);

        timeProvider.Advance(TimeSpan.FromMinutes(10));
        await monitor.EvaluateOnceAsync();
        Assert.Single(collector.GetSnapshot(), r => r.Id.Id == 1014);

        timeProvider.Advance(TimeSpan.FromMinutes(10));
        await monitor.EvaluateOnceAsync();
        Assert.Equal(2, collector.GetSnapshot().Count(r => r.Id.Id == 1014));
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
        SpoolSelfTestTracker? selfTestTracker = null,
        IAdminHttpsCertificateStatusProbe? adminHttpsCertificateProbe = null)
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
            selfTestTracker,
            adminHttpsCertificateProbe);
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

    /// <summary>
    /// バックログ起因判別テスト（EventId 1010。Issue #202）用: 上限が実質無制限のスプールを開く
    /// （使用率ではなく「進捗の有無」だけを検証したいテストのため、上限到達で経路を汚さない）。
    /// </summary>
    private async Task<DiskSpool> OpenSpoolWithLargeQuotaAsync()
    {
        var directory = Path.Combine(_spoolDirectory, $"backlog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var spool = DiskSpool.TryOpen(
            new DiskSpoolOptions { Directory = directory, QuotaBytes = long.MaxValue / 2 },
            out _);
        Assert.NotNull(spool);
        _openedSpools.Add(spool);

        // TryOpen は非同期 API ではないが、他ヘルパーとの呼び出し形を揃えるため Task を返す。
        await Task.CompletedTask;
        return spool;
    }

    /// <summary>
    /// 1 件の通常ログを追記し、アクティブセグメントを封止してセグメントファイルを 1 つ確定させる
    /// （drain がまだ消化していない「未消化バックログのセグメント」を模す）。以後の追記は新しい
    /// アクティブセグメントへ向かうため、呼ぶたびに独立したセグメントを作れる——
    /// <see cref="DiskSpool.DeleteSegment"/> で個別に「drain が消化・削除した」ことを模せる。
    /// </summary>
    /// <returns>封止したセグメントファイルのパス（新しく封止された、最も新しいセグメント）。</returns>
    /// <remarks>
    /// <see cref="DiskSpool.TrySealActiveSegmentAndListDrainable"/> はディレクトリ内の drain 対象
    /// セグメントを毎回すべて列挙する（累積リスト）ため、本メソッドを複数回呼んでも、返る値は
    /// 「今回新たに封止した 1 件」を古い順ソートの末尾として取り出す。
    /// </remarks>
    private static async Task<string> AppendAndSealSegmentAsync(DiskSpool spool)
    {
        var result = await spool.TryAppendAsync(SpoolRecord.ForLog(SampleLogRecord()));
        Assert.Equal(SpoolAppendResult.Appended, result);

        var segments = spool.TrySealActiveSegmentAndListDrainable();
        Assert.NotEmpty(segments);
        return segments[^1];
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

    /// <summary>
    /// 管理リスナのリモート HTTPS 証明書の状態プローブのフェイク（呼び出しごとに差し替え可能——
    /// 「健全 → ストアから削除」等の稼働中遷移を模す）。
    /// </summary>
    private sealed class FakeAdminHttpsCertificateStatusProbe(Func<AdminHttpsCertificateStatus> behavior) : IAdminHttpsCertificateStatusProbe
    {
        public AdminHttpsCertificateStatus Check() => behavior();
    }
}
