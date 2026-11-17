using Yagura.Abstractions.Administration;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// 後始末（旧・組み込み DB ファイルの処分）の画面テスト用のフェイク。状態・確認材料・各操作の結果を
/// テストが差し替え、画面から届いた要求を記録する。
/// </summary>
internal sealed class FakeOldDatabaseDisposalService : IOldDatabaseDisposalService
{
    internal const string Token = "disposal-token";
    internal const string BodyPath = @"C:\ProgramData\Yagura\yagura.db";
    internal const string WalPath = @"C:\ProgramData\Yagura\yagura.db-wal";

    private readonly object _gate = new();
    private OldDatabaseDisposalStatus _status = NotApplicable();

    /// <summary>現在の状態（テストの途中で差し替えると、画面の定期更新で反映される）。</summary>
    internal OldDatabaseDisposalStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }

        set
        {
            lock (_gate)
            {
                _status = value;
            }
        }
    }

    /// <summary>次の照合の結果（null なら状態だけを持つ空の結果）。</summary>
    internal OldDatabaseDisposalInspection? Inspection { get; set; }

    /// <summary>保管先の事前検査の結果。</summary>
    internal OldDatabaseArchiveDestinationCheck DestinationCheck { get; set; } =
        new(true, null, "保管先の条件を満たしています。", 2048, 1L << 40);

    /// <summary>次の開始の結果（既定は開始）。</summary>
    internal OldDatabaseDisposalResult StartResult { get; set; } =
        new(OldDatabaseDisposalOutcome.Started, "後始末を開始しました。画面を閉じても処理は続きます。");

    /// <summary>開始が受け付けられたときに状態を差し替える（実行中への遷移の再現）。</summary>
    internal OldDatabaseDisposalStatus? StatusAfterStart { get; set; }

    /// <summary>取り消しの結果。</summary>
    internal bool CancelResult { get; set; } = true;

    internal List<OldDatabaseDisposalRequest> StartRequests { get; } = [];

    internal List<string> CheckedDestinations { get; } = [];

    internal int InspectCount { get; private set; }

    internal int CancelCount { get; private set; }

    public Task<OldDatabaseDisposalStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Status);

    public Task<OldDatabaseDisposalInspection> InspectAsync(CancellationToken cancellationToken = default)
    {
        InspectCount++;
        return Task.FromResult(Inspection ?? new OldDatabaseDisposalInspection(
            Status, OldDatabaseTargetKind.SqliteDatabase, null, false, null, null, null, null, null, null, null, 0));
    }

    public Task<OldDatabaseArchiveDestinationCheck> CheckArchiveDestinationAsync(
        string parentDirectory,
        CancellationToken cancellationToken = default)
    {
        CheckedDestinations.Add(parentDirectory);
        return Task.FromResult(DestinationCheck);
    }

    public Task<OldDatabaseDisposalResult> StartAsync(
        OldDatabaseDisposalRequest request,
        CancellationToken cancellationToken = default)
    {
        StartRequests.Add(request);
        if (StartResult.Outcome == OldDatabaseDisposalOutcome.Started && StatusAfterStart is { } next)
        {
            Status = next;
        }

        return Task.FromResult(StartResult);
    }

    public Task<bool> CancelAsync(CancellationToken cancellationToken = default)
    {
        CancelCount++;
        return Task.FromResult(CancelResult);
    }

    internal static IReadOnlyList<OldDatabaseFile> Files() =>
    [
        new(WalPath, 4096),
        new(BodyPath, 3L * 1024 * 1024),
    ];

    internal static OldDatabaseDisposalStatus NotApplicable(OldDatabaseDisposalResult? lastResult = null) =>
        new(OldDatabaseDisposalState.NotApplicable, [], 0, null, lastResult, null);

    internal static OldDatabaseDisposalStatus Pending() =>
        new(OldDatabaseDisposalState.Pending, Files(), Files().Sum(f => f.SizeBytes ?? 0), null, null, Token);

    internal static OldDatabaseDisposalStatus MigrationRunning() =>
        new(OldDatabaseDisposalState.MigrationRunning, Files(), Files().Sum(f => f.SizeBytes ?? 0), null, null, null);

    internal static OldDatabaseDisposalStatus Running(
        OldDatabaseDisposalStage stage,
        OldDatabaseDisposalMethod method = OldDatabaseDisposalMethod.Archive) =>
        new(
            stage == OldDatabaseDisposalStage.Deleting
                ? OldDatabaseDisposalState.DisposalDeleting
                : OldDatabaseDisposalState.DisposalRunning,
            Files(),
            Files().Sum(f => f.SizeBytes ?? 0),
            new OldDatabaseDisposalProgress(
                method, stage, stage != OldDatabaseDisposalStage.Deleting, new DateTimeOffset(2026, 11, 16, 1, 0, 0, TimeSpan.Zero)),
            null,
            null);

    internal static OldDatabaseDisposalStatus LastAttemptFailed(OldDatabaseDisposalResult result) =>
        new(OldDatabaseDisposalState.LastAttemptFailed, Files(), Files().Sum(f => f.SizeBytes ?? 0), null, result, Token);

    /// <summary>旧ファイルを読めた照合の結果（既定は移行済み）。</summary>
    internal static OldDatabaseDisposalInspection SqliteInspection(
        OldDatabaseMigrationJudgment? judgment = null,
        int? retentionDays = 30,
        long? beyondRetention = 0) =>
        new(
            Pending(),
            OldDatabaseTargetKind.SqliteDatabase,
            null,
            ArchiveAllowed: true,
            judgment ?? Migrated(),
            RecordCount: 1200,
            OldestReceivedAt: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            NewestReceivedAt: new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
            SystemEventCount: 3,
            RetentionDays: retentionDays,
            RecordsBeyondRetentionCount: beyondRetention,
            RequiredArchiveFreeBytes: (7L * 1024 * 1024) + (1L << 30));

    internal static OldDatabaseMigrationJudgment Migrated() =>
        new(true, [], 0, null, null, 0, null, null, 0, null, null);

    /// <summary>移行の完了後に縮退運転で書き込まれたログが残っている判定。</summary>
    internal static OldDatabaseMigrationJudgment UnmigratedAfterSwitch() =>
        new(
            false,
            [OldDatabaseMigrationGap.RecordsAddedAfterCompletion, OldDatabaseMigrationGap.RecordsInUnreconcilableInterval],
            UnmigratedRecordCount: 42,
            UnmigratedOldest: new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero),
            UnmigratedNewest: new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero),
            RecordsAfterSwitchCount: 42,
            RecordsAfterSwitchOldest: new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero),
            RecordsAfterSwitchNewest: new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero),
            UnmigratedSystemEventCount: 2,
            UnmigratedSystemEventOldest: new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero),
            UnmigratedSystemEventNewest: new DateTimeOffset(2026, 11, 10, 1, 0, 0, TimeSpan.Zero));
}
