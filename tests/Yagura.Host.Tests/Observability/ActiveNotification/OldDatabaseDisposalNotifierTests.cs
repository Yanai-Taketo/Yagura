using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Administration.OldDatabaseDisposal;
using Yagura.Host.Observability.ActiveNotification;

namespace Yagura.Host.Tests.Observability.ActiveNotification;

/// <summary>
/// <see cref="OldDatabaseDisposalNotifier"/> の単体テスト（database.md §6.3「未完了の判定と可視化・通知」・状態表の 1041 列・2031）。
/// </summary>
public sealed class OldDatabaseDisposalNotifierTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero));
    private readonly FakeLogger<OldDatabaseDisposalNotifier> _logger = new();
    private readonly RecordingAuditRecorder _audit = new();
    private OldDatabaseDisposalObservation _observation = Observation(OldDatabaseDisposalState.Pending);

    private OldDatabaseDisposalNotifier CreateNotifier() => new(() => _observation, _audit, _time, _logger);

    private const string CurrentFile = @"C:\ProgramData\Yagura\yagura.db";

    private static OldDatabaseDisposalObservation Observation(
        OldDatabaseDisposalState state,
        bool promoted = true,
        bool completedInThisProcess = false,
        string? unresolvable = null,
        string? misplaced = null,
        string currentFile = CurrentFile,
        IReadOnlyCollection<string>? disposed = null)
    {
        IReadOnlyList<OldDatabaseFile> files = state == OldDatabaseDisposalState.NotApplicable
            ? []
            : [new OldDatabaseFile(currentFile, 3L * 1024 * 1024)];
        return new OldDatabaseDisposalObservation(
            new OldDatabaseDisposalStatus(state, files, files.Sum(f => f.SizeBytes ?? 0), null, null, null, misplaced),
            promoted,
            disposed ?? (completedInThisProcess ? [currentFile] : []),
            unresolvable);
    }

    private List<FakeLogRecord> Notifications() =>
        _logger.Collector.GetSnapshot().Where(r => r.Id.Id == 1041).ToList();

    [Fact]
    public async Task Pending_DoesNotFireBeforeTheContinuationThreshold()
    {
        var notifier = CreateNotifier();

        await notifier.EvaluateAsync();
        _time.Advance(ActiveNotificationConstants.OldDatabaseDisposalIncompleteContinuationThreshold - TimeSpan.FromMinutes(1));
        await notifier.EvaluateAsync();

        Assert.Empty(Notifications());
    }

    [Fact]
    public async Task Pending_FiresAfterTheThreshold_ThenRepeatsOnlyAfterTheSuppressionWindow()
    {
        var notifier = CreateNotifier();
        await notifier.EvaluateAsync();

        _time.Advance(ActiveNotificationConstants.OldDatabaseDisposalIncompleteContinuationThreshold);
        await notifier.EvaluateAsync();
        var first = Assert.Single(Notifications());
        Assert.Equal(LogLevel.Warning, first.Level);
        Assert.StartsWith("[old-database-disposal-incomplete] 受信と保存は正常に続いています。", first.Message);
        Assert.Contains("後始末", first.Message);
        Assert.Contains("/admin/promotion", first.Message);
        Assert.Contains(@"C:\ProgramData\Yagura\yagura.db", first.Message);
        Assert.Contains("3.0 MB", first.Message);

        _time.Advance(ActiveNotificationConstants.OldDatabaseDisposalIncompleteSuppressionWindow - TimeSpan.FromMinutes(1));
        await notifier.EvaluateAsync();
        Assert.Single(Notifications());

        _time.Advance(TimeSpan.FromMinutes(1));
        await notifier.EvaluateAsync();
        Assert.Equal(2, Notifications().Count);
    }

    [Theory]
    [InlineData(OldDatabaseDisposalState.MigrationRunning)]
    [InlineData(OldDatabaseDisposalState.DisposalRunning)]
    [InlineData(OldDatabaseDisposalState.DisposalDeleting)]
    public async Task Running_NeverFires(OldDatabaseDisposalState running)
    {
        _observation = Observation(running);
        var notifier = CreateNotifier();

        await notifier.EvaluateAsync();
        _time.Advance(ActiveNotificationConstants.OldDatabaseDisposalIncompleteContinuationThreshold * 2);
        await notifier.EvaluateAsync();

        Assert.Empty(Notifications());
    }

    [Fact]
    public async Task NotPromoted_IsNeverIncomplete()
    {
        _observation = Observation(OldDatabaseDisposalState.NotApplicable, promoted: false);
        var notifier = CreateNotifier();

        await notifier.EvaluateAsync();
        _time.Advance(TimeSpan.FromDays(3));
        await notifier.EvaluateAsync();

        Assert.Empty(Notifications());
        Assert.Empty(_audit.Events);
    }

    [Fact]
    public async Task Resolution_ResetsTheContinuation()
    {
        var notifier = CreateNotifier();
        await notifier.EvaluateAsync();
        _time.Advance(TimeSpan.FromHours(20));
        _observation = Observation(OldDatabaseDisposalState.NotApplicable, completedInThisProcess: true);
        await notifier.EvaluateAsync();

        _observation = Observation(OldDatabaseDisposalState.Pending);
        _time.Advance(TimeSpan.FromHours(5));
        await notifier.EvaluateAsync();

        Assert.Empty(Notifications());
    }

    [Fact]
    public async Task DisappearanceWithoutCompletionInThisProcess_IsAuditedOnce()
    {
        var notifier = CreateNotifier();
        await notifier.EvaluateAsync();

        _observation = Observation(OldDatabaseDisposalState.NotApplicable);
        await notifier.EvaluateAsync();
        await notifier.EvaluateAsync();

        var audit = Assert.Single(_audit.Events);
        Assert.Equal(AuditEventKind.OldDatabaseRemovedOutsideProduct, audit.Kind);
        Assert.Null(audit.RemoteAddress);
    }

    [Fact]
    public async Task DisappearanceAfterCompletionInThisProcess_IsNotAudited()
    {
        var notifier = CreateNotifier();
        await notifier.EvaluateAsync();

        _observation = Observation(OldDatabaseDisposalState.NotApplicable, completedInThisProcess: true);
        await notifier.EvaluateAsync();

        Assert.Empty(_audit.Events);
    }

    [Fact]
    public async Task FileUnderAnotherNameRemovedOutside_AfterDisposingTheCurrentFile_IsAuditedWithItsPath()
    {
        // 今の設定名のファイルを製品で処分した後でも、切替の時点の別名のファイルが製品の外で消えたことは記録する。
        const string custom = @"C:\ProgramData\Yagura\custom.db";
        const string recorded = @"C:\ProgramData\Yagura\yagura.db";
        var notifier = CreateNotifier();
        _observation = Observation(OldDatabaseDisposalState.Pending, currentFile: custom, misplaced: recorded);
        await notifier.EvaluateAsync();

        _observation = Observation(OldDatabaseDisposalState.NotApplicable, misplaced: recorded, disposed: [custom]);
        await notifier.EvaluateAsync();
        Assert.Empty(_audit.Events);

        _observation = Observation(OldDatabaseDisposalState.NotApplicable, disposed: [custom]);
        await notifier.EvaluateAsync();

        var audit = Assert.Single(_audit.Events);
        Assert.Equal(AuditEventKind.OldDatabaseRemovedOutsideProduct, audit.Kind);
        Assert.Contains(recorded, audit.Detail);
        Assert.DoesNotContain(custom, audit.Detail);
    }

    [Fact]
    public async Task FailureFinalized_FiresImmediately_RegardlessOfTheThreshold_WithResultAndReason()
    {
        var notifier = CreateNotifier();
        await notifier.EvaluateAsync();

        notifier.NotifyFinalized(
            new OldDatabaseDisposalResult(
                OldDatabaseDisposalOutcome.DeletePartiallyFailed,
                "元のファイルの一部を削除できませんでした（yagura.db）。",
                OldDatabaseDisposalReason.DeleteFailed),
            Observation(OldDatabaseDisposalState.LastAttemptFailed));

        var notification = Assert.Single(Notifications());
        Assert.StartsWith("[old-database-disposal-failed] 受信と保存は正常に続いています。", notification.Message);
        Assert.Contains("元のファイルの一部を削除できませんでした", notification.Message);

        // 以後は処分待ちと同じ——抑制窓の間は周期評価で重ねて鳴らさない。
        _observation = Observation(OldDatabaseDisposalState.LastAttemptFailed);
        _time.Advance(ActiveNotificationConstants.OldDatabaseDisposalIncompleteSuppressionWindow - TimeSpan.FromMinutes(1));
        await notifier.EvaluateAsync();
        Assert.Single(Notifications());
    }

    [Fact]
    public async Task UnresolvableState_IncludesTheReasonAndTheManualProcedure()
    {
        _observation = Observation(OldDatabaseDisposalState.Pending, unresolvable: "対象の安全検査を満たさない");
        var notifier = CreateNotifier();
        await notifier.EvaluateAsync();
        _time.Advance(ActiveNotificationConstants.OldDatabaseDisposalIncompleteContinuationThreshold);

        await notifier.EvaluateAsync();

        var notification = Assert.Single(Notifications());
        Assert.Contains("管理画面の操作では解消できません", notification.Message);
        Assert.Contains("手作業の手順", notification.Message);
    }

    [Fact]
    public async Task MisplacedRecordedOldFile_CountsAsIncomplete()
    {
        _observation = Observation(OldDatabaseDisposalState.NotApplicable, misplaced: @"C:\ProgramData\Yagura\old.db",
            unresolvable: "別の名前で残っています");
        var notifier = CreateNotifier();
        await notifier.EvaluateAsync();
        _time.Advance(ActiveNotificationConstants.OldDatabaseDisposalIncompleteContinuationThreshold);

        await notifier.EvaluateAsync();

        var notification = Assert.Single(Notifications());
        Assert.Contains(@"C:\ProgramData\Yagura\old.db", notification.Message);
    }

    [Fact]
    public void ThresholdAndWindow_AreTheDocumentedProvisionalValues()
    {
        Assert.Equal(TimeSpan.FromHours(24), ActiveNotificationConstants.OldDatabaseDisposalIncompleteContinuationThreshold);
        Assert.Equal(TimeSpan.FromHours(24), ActiveNotificationConstants.OldDatabaseDisposalIncompleteSuppressionWindow);
    }

    private sealed class RecordingAuditRecorder : IAuditRecorder
    {
        public List<AuditEvent> Events { get; } = [];

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }
}
