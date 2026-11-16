using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Yagura.Host.Observability.ActiveNotification;
using Yagura.Ingestion.Diagnostics;

namespace Yagura.Host.Tests.Observability.ActiveNotification;

/// <summary>
/// フォワーダ MSI 配置フォルダの ACL 検出二系統——乖離警告（1033）と開放継続の通知（1034）。
/// ADR-0020 決定 2・委任 7——の発火経路の単体テスト（Issue #465 の回帰防止を含む）。
/// </summary>
/// <remarks>
/// 1033 で固定する不変条件は 2 つ:
/// ①<b>起動時検査と周期評価が抑制窓を共有する</b>——以前は <c>Program.cs</c> が独自の
/// <c>LogWarning</c> で直接発火し、抑制記録を更新しなかったため「起動直後 + その約 60 秒後」の
/// 2 連発が再起動のたびに起きていた（メール通知構成では 1 回の再起動で 2 通）。
/// ②<b>本文に配置フォルダのパスが載る</b>——データルートは <c>YAGURA_DATAROOT</c> で既定以外にも
/// 置けるため、撤去先を本文だけで特定できなければ lab ③-1 の合否基準を満たさない。
/// 1034 は継続の起点をプロセス内に持ち再起動のたびに計測が戻るため、継続判定期間を実機で通して
/// 観測しにくい。起点・継続判定・専用の抑制窓を時刻を進めて機械検証する（期間は定数を参照し、
/// 仮値の改訂に追従させる）。
/// </remarks>
public sealed class ActiveNotificationMonitorForwarderMsiAclTests
{
    private const string FolderPath = @"D:\CustomRoot\Yagura\forwarder";

    private static readonly TimeSpan PollInterval = ActiveNotificationConstants.PollInterval;
    private static readonly TimeSpan ContinuationThreshold = ActiveNotificationConstants.ForwarderMsiOpenContinuationThreshold;
    private static readonly TimeSpan ContinuationSuppressionWindow =
        ActiveNotificationConstants.ForwarderMsiOpenContinuationSuppressionWindow;

    [Fact]
    public async Task StartupCheckThenPeriodicEvaluation_WithinSuppressionWindow_NotifiesOnce()
    {
        // Issue #465 の本体: 起動時検査で 1033 が出たあと、続く周期評価は抑制窓により沈黙する。
        var collector = new FakeLogCollector();
        var monitor = CreateMonitor(collector, out var timeProvider, writable: true, uploadEnabled: false);

        monitor.EvaluateForwarderMsiFolderAclAtStartup();

        // 実測（#465）の再現条件: 起動直後の発火から約 60 秒後に最初の周期評価が走る。
        timeProvider.Advance(TimeSpan.FromSeconds(60));
        await monitor.EvaluateOnceAsync();

        Assert.Single(collector.GetSnapshot(), r => r.Id.Id == 1033);
    }

    [Fact]
    public async Task StartupCheck_ThenPeriodicAfterSuppressionWindow_NotifiesAgain()
    {
        // 抑制はあくまで窓であり、条件が続く限り窓明けには再通知される（沈黙させ切らない）。
        var collector = new FakeLogCollector();
        var monitor = CreateMonitor(collector, out var timeProvider, writable: true, uploadEnabled: false);

        monitor.EvaluateForwarderMsiFolderAclAtStartup();
        timeProvider.Advance(ActiveNotificationConstants.SuppressionWindow + TimeSpan.FromSeconds(1));
        await monitor.EvaluateOnceAsync();

        Assert.Equal(2, collector.GetSnapshot().Count(r => r.Id.Id == 1033));
    }

    [Fact]
    public void StartupCheck_MessageContainsFolderPathAndRemovalGuidance()
    {
        // lab ③-1 の合否基準（本文に配置フォルダパスと撤去の誘導）。周期側と同一本文であることは
        // 発火経路の一本化そのもので担保される。
        var collector = new FakeLogCollector();
        var monitor = CreateMonitor(collector, out _, writable: true, uploadEnabled: false);

        monitor.EvaluateForwarderMsiFolderAclAtStartup();

        var record = Assert.Single(collector.GetSnapshot(), r => r.Id.Id == 1033);
        Assert.Contains(FolderPath, record.Message, StringComparison.Ordinal);
        Assert.Contains("撤去", record.Message, StringComparison.Ordinal);
        // 危険性の説明（旧・起動時経路には無かった。2 経路が情報を非対称に持ち合っていた）。
        Assert.Contains("差し替えに波及", record.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupCheck_UploadEnabled_DoesNotWarnAsDrift()
    {
        // opt-in 有効なら乖離ではない（1034 の開放継続は「継続」の観測を要するため起動時は出ない）。
        var collector = new FakeLogCollector();
        var monitor = CreateMonitor(collector, out _, writable: true, uploadEnabled: true);

        monitor.EvaluateForwarderMsiFolderAclAtStartup();

        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id is 1033 or 1034);
    }

    [Fact]
    public void StartupCheck_NotWritable_IsSilent()
    {
        // 既定（読み取りのみ）は沈黙。判定不能（null）も同様——安全側。
        var collector = new FakeLogCollector();
        var monitor = CreateMonitor(collector, out _, writable: false, uploadEnabled: false);

        monitor.EvaluateForwarderMsiFolderAclAtStartup();

        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id == 1033);
    }

    // ---- 開放継続の通知（1034） ----

    [Fact]
    public async Task OpenContinuation_NotifiesOnceWhenThresholdElapses()
    {
        // 継続判定期間の 1 周期手前までは沈黙し、期間に達した周期評価で 1 回だけ通知する。
        var collector = new FakeLogCollector();
        var monitor = CreateMonitor(collector, out var timeProvider, writable: true, uploadEnabled: true);

        // 起動時検査が継続の起点を置く。
        monitor.EvaluateForwarderMsiFolderAclAtStartup();

        await PollForAsync(monitor, timeProvider, ContinuationThreshold - PollInterval);
        Assert.Equal(0, CountOpenContinuationNotifications(collector));

        await PollForAsync(monitor, timeProvider, PollInterval);
        var record = Assert.Single(collector.GetSnapshot(), r => r.Id.Id == 1034);
        Assert.Equal(LogLevel.Information, record.Level);
        // 乖離警告（1033）とは排他（アップロード機能が有効なら閉じ忘れではない）。
        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Id.Id == 1033);
    }

    [Fact]
    public async Task OpenContinuation_StaysSilentThroughSuppressionWindow_ThenRemindsAgain()
    {
        // 常置運用で騒音にならないことの固定: 通知後は専用の抑制窓（既定の 15 分ではない）の間、
        // 毎周期の評価が続いても再通知しない。窓明けにはリマインダとして再通知する。
        var collector = new FakeLogCollector();
        var monitor = CreateMonitor(collector, out var timeProvider, writable: true, uploadEnabled: true);

        monitor.EvaluateForwarderMsiFolderAclAtStartup();
        await PollForAsync(monitor, timeProvider, ContinuationThreshold);
        Assert.Equal(1, CountOpenContinuationNotifications(collector));

        await PollForAsync(monitor, timeProvider, ContinuationSuppressionWindow - PollInterval);
        Assert.Equal(1, CountOpenContinuationNotifications(collector));

        await PollForAsync(monitor, timeProvider, PollInterval);
        Assert.Equal(2, CountOpenContinuationNotifications(collector));
    }

    [Fact]
    public async Task OpenContinuation_AceRemovalResetsOrigin()
    {
        // 「使うときだけ開く」運用: 撤去を挟んだ開放は通算しない。撤去後に開き直したのを観測した
        // 時点が新しい起点になる。
        var collector = new FakeLogCollector();
        bool? writable = true;
        var monitor = CreateMonitor(collector, out var timeProvider, () => writable, uploadEnabled: true);

        monitor.EvaluateForwarderMsiFolderAclAtStartup();
        await PollForAsync(monitor, timeProvider, ContinuationThreshold - PollInterval);

        // 撤去。通算していれば、この周期で継続判定期間に達して通知されてしまう。
        writable = false;
        await PollForAsync(monitor, timeProvider, PollInterval);

        writable = true;
        await PollForAsync(monitor, timeProvider, PollInterval);
        await PollForAsync(monitor, timeProvider, ContinuationThreshold - PollInterval);
        Assert.Equal(0, CountOpenContinuationNotifications(collector));

        await PollForAsync(monitor, timeProvider, PollInterval);
        Assert.Equal(1, CountOpenContinuationNotifications(collector));
    }

    /// <summary>
    /// 監視ループと同じ間隔（<see cref="ActiveNotificationConstants.PollInterval"/>）で時刻を進めながら、
    /// <paramref name="duration"/> の間の周期評価を順に実行する。
    /// </summary>
    /// <remarks>
    /// 間の評価を飛ばして一気に時刻を進めると、抑制記録の掃引（評価の末尾で毎周期走る）が窓の途中で
    /// 記録を消す経路を通らず、実運用で起きる再通知を見逃す。
    /// </remarks>
    private static async Task PollForAsync(
        ActiveNotificationMonitor monitor, FakeTimeProvider timeProvider, TimeSpan duration)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += PollInterval)
        {
            timeProvider.Advance(PollInterval);
            await monitor.EvaluateOnceAsync();
        }
    }

    private static int CountOpenContinuationNotifications(FakeLogCollector collector) =>
        collector.GetSnapshot().Count(r => r.Id.Id == 1034);

    private static ActiveNotificationMonitor CreateMonitor(
        FakeLogCollector collector,
        out FakeTimeProvider timeProvider,
        bool? writable,
        bool uploadEnabled) =>
        CreateMonitor(collector, out timeProvider, () => writable, uploadEnabled);

    private static ActiveNotificationMonitor CreateMonitor(
        FakeLogCollector collector,
        out FakeTimeProvider timeProvider,
        Func<bool?> writableProbe,
        bool uploadEnabled)
    {
        timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-26T15:30:00Z"));

        return new ActiveNotificationMonitor(
            spool: null,
            new IngestionMetrics(),
            new StubVolumeInfo(),
            new StubExpressCapacityChecker(),
            timeProvider,
            new FakeLogger<ActiveNotificationMonitor>(collector),
            forwarderMsiFolderWritableProbe: writableProbe,
            forwarderMsiUploadEnabled: uploadEnabled,
            forwarderMsiFolderPath: FolderPath);
    }

    private sealed class StubVolumeInfo : IMonitoredVolumeInfo
    {
        public IReadOnlyList<MonitoredVolumeReading> ReadMonitoredVolumes() => [];
    }

    private sealed class StubExpressCapacityChecker : IExpressCapacityChecker
    {
        public Task<ExpressCapacityReading?> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ExpressCapacityReading?>(null);
    }
}
