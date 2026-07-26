using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Yagura.Host.Observability.ActiveNotification;
using Yagura.Ingestion.Diagnostics;

namespace Yagura.Host.Tests.Observability.ActiveNotification;

/// <summary>
/// フォワーダ MSI 配置フォルダの ACL 乖離警告（1033。ADR-0020 決定 2・委任 7）の発火経路の
/// 単体テスト（Issue #465 の回帰防止）。
/// </summary>
/// <remarks>
/// 固定する不変条件は 2 つ:
/// ①<b>起動時検査と周期評価が抑制窓を共有する</b>——以前は <c>Program.cs</c> が独自の
/// <c>LogWarning</c> で直接発火し、抑制記録を更新しなかったため「起動直後 + その約 60 秒後」の
/// 2 連発が再起動のたびに起きていた（メール通知構成では 1 回の再起動で 2 通）。
/// ②<b>本文に配置フォルダのパスが載る</b>——データルートは <c>YAGURA_DATAROOT</c> で既定以外にも
/// 置けるため、撤去先を本文だけで特定できなければ lab ③-1 の合否基準を満たさない。
/// </remarks>
public sealed class ActiveNotificationMonitorForwarderMsiAclTests
{
    private const string FolderPath = @"D:\CustomRoot\Yagura\forwarder";

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

    private static ActiveNotificationMonitor CreateMonitor(
        FakeLogCollector collector,
        out FakeTimeProvider timeProvider,
        bool? writable,
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
            forwarderMsiFolderWritableProbe: () => writable,
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
