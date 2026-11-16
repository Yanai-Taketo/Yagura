using System.Text;
using Microsoft.Data.Sqlite;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Administration;
using Yagura.Host.Administration.OldDatabaseDisposal;
using Yagura.Storage;
using Yagura.Storage.Sqlite;
using Yagura.TestSupport;

namespace Yagura.Host.Tests.Administration.OldDatabaseDisposal;

/// <summary>
/// <see cref="OldDatabaseDisposalService"/> の単体テスト（database.md §6.3）。旧ファイル・移行先とも SQLite、
/// ACL とボリュームの判定は差し替え口（<see cref="FakeArchivePlatform"/>）の後ろに置く——Windows 以外でも
/// 状態表・前提条件・削除順序・失敗経路を固定する。
/// </summary>
public sealed class OldDatabaseDisposalServiceTests : IAsyncLifetime
{
    private readonly TestTempDirectory _temp = new("disposal-test");
    private readonly ConcurrentAuditRecorder _audit = new();
    private readonly FakeArchivePlatform _platform = new();
    private readonly ScriptedFileDeleter _deleter = new();
    private readonly OldDatabaseAccessGate _gate = new();
    private string _dataRoot = null!;
    private string _archiveParent = null!;
    private OldDatabaseTargetPaths _targets = null!;
    private SqliteLogStore _target = null!;
    private bool _underPressure;
    private int? _retentionDays;

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(_temp.Path, "data");
        _archiveParent = Path.Combine(_temp.Path, "archive");
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_archiveParent);
        _targets = OldDatabaseTargetPaths.Derive(_dataRoot, "yagura.db");
        _target = new SqliteLogStore(Path.Combine(_temp.Path, "target.db"));
        await _target.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _target.DisposeAsync();
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private OldDatabaseDisposalService CreateService(
        bool promoted = true,
        OldDatabaseDisposalOptions? options = null,
        TimeProvider? timeProvider = null) => new(
        _targets,
        promoted,
        _target,
        _gate,
        _audit,
        _platform,
        new OldDatabaseDisposalEnvironment(
            SpoolDirectory: Path.Combine(_dataRoot, "spool"),
            InstallDirectory: Path.Combine(_temp.Path, "install"),
            SpoolRemainingQuotaBytes: () => 0,
            IsLiveIngestionUnderPressure: () => Volatile.Read(ref _underPressure),
            RetentionDays: () => _retentionDays),
        options ?? new OldDatabaseDisposalOptions
        {
            FreeSpaceFloorBytes = 1024,
            DeleteRetryInterval = TimeSpan.FromMilliseconds(10),
            DeleteRetryBudget = TimeSpan.FromMilliseconds(500),
            WatchdogInterval = TimeSpan.FromMilliseconds(20),
            GateWait = TimeSpan.FromMilliseconds(100),
        },
        _deleter,
        timeProvider);

    /// <summary>旧ファイルを作り、すべて移行済み（移行先にあり・完了記録・切替の境界あり）の状態にする。</summary>
    private async Task<List<LogRecord>> SeedMigratedAsync(int count = 50)
    {
        var records = await DisposalFixtures.SeedOldAsync(_targets.BodyPath, count);
        await DisposalFixtures.WriteAsync(_target, records);
        var position = await DisposalFixtures.LatestPositionAsync(_targets.BodyPath);
        DisposalFixtures.WriteCheckpoint(_dataRoot, position.ReceivedAt, position.Id, count, completed: true);
        PromotionSwitchBoundaryStore.Write(_dataRoot, new PromotionSwitchBoundary(
            DisposalFixtures.BaseTime.AddDays(1).UtcDateTime, _targets.BodyPath, null, null, count,
            EffectiveSinceUtc: DisposalFixtures.BaseTime.AddDays(1).UtcDateTime));
        return records;
    }

    private static async Task<OldDatabaseDisposalResult> RunToEndAsync(OldDatabaseDisposalService service, OldDatabaseDisposalRequest request)
    {
        var started = await service.StartAsync(request);
        Assert.True(started.Outcome == OldDatabaseDisposalOutcome.Started, started.Message);
        await service.CurrentRun!.WaitAsync(TimeSpan.FromSeconds(30));
        return (await service.GetStatusAsync()).LastResult!;
    }

    private static async Task<string> TokenAsync(OldDatabaseDisposalService service) =>
        (await service.GetStatusAsync()).ExecuteIdempotencyToken!;

    private List<AuditEvent> DisposalAudits() =>
        _audit.Events.Where(e => e.Kind == AuditEventKind.OldDatabaseDisposalExecuted).ToList();

    // ---- 状態表 ----

    [Fact]
    public async Task Status_NotPromoted_IsNotApplicable_EvenWhenFilesExist()
    {
        await DisposalFixtures.SeedOldAsync(_targets.BodyPath, 3);

        var status = await CreateService(promoted: false).GetStatusAsync();

        Assert.Equal(OldDatabaseDisposalState.NotApplicable, status.State);
        Assert.Null(status.ExecuteIdempotencyToken);
    }

    [Fact]
    public async Task Status_NoTargetFiles_IsNotApplicable()
    {
        var status = await CreateService().GetStatusAsync();

        Assert.Equal(OldDatabaseDisposalState.NotApplicable, status.State);
        Assert.Empty(status.TargetFiles);
    }

    [Fact]
    public async Task Status_AnyOfTheThreeFiles_IsPending_WithToken()
    {
        await File.WriteAllBytesAsync(_targets.ShmPath, new byte[16]);

        var status = await CreateService().GetStatusAsync();

        Assert.Equal(OldDatabaseDisposalState.Pending, status.State);
        Assert.Equal(_targets.ShmPath, Assert.Single(status.TargetFiles).Path);
        Assert.NotNull(status.ExecuteIdempotencyToken);
    }

    [Fact]
    public async Task Status_WhileMigrationHoldsTheGate_IsMigrationRunning_AndStartIsRejected()
    {
        await SeedMigratedAsync();
        var service = CreateService();
        var token = await TokenAsync(service);

        using (await _gate.TryAcquireAsync(OldDatabaseAccessHolder.Migration, TimeSpan.Zero))
        {
            Assert.Equal(OldDatabaseDisposalState.MigrationRunning, (await service.GetStatusAsync()).State);

            var result = await service.StartAsync(new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, token));
            Assert.Equal(OldDatabaseDisposalOutcome.Rejected, result.Outcome);
            Assert.Equal(OldDatabaseDisposalReason.MigrationRunning, result.Reason);
        }

        Assert.True(File.Exists(_targets.BodyPath));
        Assert.Empty(DisposalAudits());
    }

    [Fact]
    public async Task Status_DuringDeletion_IsDisposalDeleting_AndCannotBeCancelled()
    {
        await SeedMigratedAsync();
        var service = CreateService();
        _deleter.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var started = await service.StartAsync(new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, await TokenAsync(service)));
        Assert.Equal(OldDatabaseDisposalOutcome.Started, started.Outcome);
        await _deleter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var status = await service.GetStatusAsync();
        Assert.Equal(OldDatabaseDisposalState.DisposalDeleting, status.State);
        Assert.False(status.Progress!.Cancellable);
        Assert.False(await service.CancelAsync());

        _deleter.Gate.SetResult();
        await service.CurrentRun!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(OldDatabaseDisposalOutcome.Completed, (await service.GetStatusAsync()).LastResult!.Outcome);
    }

    [Fact]
    public async Task Status_AfterFailure_IsLastAttemptFailed_AndNotifiesOnce()
    {
        await SeedMigratedAsync();
        var service = CreateService();
        var notified = new List<OldDatabaseDisposalResult>();
        service.FailureFinalized = (result, _) => notified.Add(result);
        _deleter.FailSuffix = "yagura.db";

        var result = await RunToEndAsync(service, new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, await TokenAsync(service)));

        Assert.Equal(OldDatabaseDisposalOutcome.DeletePartiallyFailed, result.Outcome);
        var status = await service.GetStatusAsync();
        Assert.Equal(OldDatabaseDisposalState.LastAttemptFailed, status.State);
        Assert.NotNull(status.ExecuteIdempotencyToken);
        Assert.Single(notified);
    }

    // ---- 冪等トークン ----

    [Fact]
    public async Task Start_WithUnknownToken_IsInvalidToken_AndChangesNothing()
    {
        await SeedMigratedAsync();

        var result = await CreateService().StartAsync(new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, "stale"));

        Assert.Equal(OldDatabaseDisposalOutcome.InvalidToken, result.Outcome);
        Assert.True(File.Exists(_targets.BodyPath));
    }

    [Fact]
    public async Task Start_SameTokenAfterRun_IsAlreadyApplied_AndDoesNotRunTwice()
    {
        await SeedMigratedAsync();
        var service = CreateService();
        var token = await TokenAsync(service);
        await RunToEndAsync(service, new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, token));
        var auditsAfterFirst = DisposalAudits().Count;

        var second = await service.StartAsync(new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, token));

        Assert.Equal(OldDatabaseDisposalOutcome.AlreadyApplied, second.Outcome);
        Assert.Equal(auditsAfterFirst, DisposalAudits().Count);
    }

    [Fact]
    public async Task Start_RejectionDoesNotConsumeTheToken()
    {
        await SeedMigratedAsync();
        var service = CreateService();
        var token = await TokenAsync(service);
        _underPressure = true;

        var rejected = await service.StartAsync(new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, token));
        Assert.Equal(OldDatabaseDisposalReason.LiveIngestionUnderPressure, rejected.Reason);

        _underPressure = false;
        var result = await RunToEndAsync(service, new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, token));
        Assert.Equal(OldDatabaseDisposalOutcome.Completed, result.Outcome);
    }

    // ---- 前提条件 ----

    [Fact]
    public async Task Start_NotPromoted_IsRejected()
    {
        await SeedMigratedAsync();
        var service = CreateService(promoted: false);

        var result = await service.StartAsync(new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, "any"));

        // 対象外の状態ではトークンを出さないため、トークンの検査より前に拒否されることはない——
        // どちらの拒否でも何も変更しないことを確かめる。
        Assert.NotEqual(OldDatabaseDisposalOutcome.Started, result.Outcome);
        Assert.True(File.Exists(_targets.BodyPath));
    }

    [Fact]
    public async Task Start_Unmigrated_RequiresExplicitConfirmation_WithCountAndRange()
    {
        var records = await DisposalFixtures.SeedOldAsync(_targets.BodyPath, 30);
        var service = CreateService();
        var token = await TokenAsync(service);

        var rejected = await service.StartAsync(new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, token));

        Assert.Equal(OldDatabaseDisposalReason.MigrationConfirmationRequired, rejected.Reason);
        Assert.Contains("30", rejected.Message);
        Assert.True(File.Exists(_targets.BodyPath));
        Assert.Empty(DisposalAudits());

        var confirmed = await RunToEndAsync(service, new OldDatabaseDisposalRequest(
            OldDatabaseDisposalMethod.Delete, token, ConfirmDisposeWithoutMigration: true));
        Assert.Equal(OldDatabaseDisposalOutcome.Completed, confirmed.Outcome);
        Assert.Contains(DisposalAudits(), e => e.Detail!.Contains("移行せずに後始末する=確認あり"));
        Assert.Equal(30, records.Count);
    }

    [Fact]
    public async Task Start_Migrated_DoesNotRequireConfirmation()
    {
        await SeedMigratedAsync();
        var service = CreateService();

        var result = await RunToEndAsync(service, new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, await TokenAsync(service)));

        Assert.Equal(OldDatabaseDisposalOutcome.Completed, result.Outcome);
        Assert.Contains(DisposalAudits(), e => e.Detail!.Contains("移行の状態=移行済み"));
    }

    [Fact]
    public async Task Start_LiveIngestionUnderPressure_IsRejected()
    {
        await SeedMigratedAsync();
        var service = CreateService();
        _underPressure = true;

        var result = await service.StartAsync(new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, await TokenAsync(service)));

        Assert.Equal(OldDatabaseDisposalReason.LiveIngestionUnderPressure, result.Reason);
        Assert.True(File.Exists(_targets.BodyPath));
    }

    // ---- 対象の安全検査 ----

    [Fact]
    public async Task Start_TargetIsDirectory_IsRejected_AndTheRejectionIsAudited()
    {
        Directory.CreateDirectory(_targets.BodyPath);
        var service = CreateService();

        var result = await service.StartAsync(new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, await TokenAsync(service)));

        Assert.Equal(OldDatabaseDisposalReason.TargetUnsafe, result.Reason);
        Assert.True(Directory.Exists(_targets.BodyPath));
        var audit = Assert.Single(DisposalAudits());
        Assert.Contains("拒否（対象の安全検査）", audit.Detail);
    }

    [Fact]
    public async Task Start_SqliteFileWithoutTheProductSchema_IsRejected_AndAudited()
    {
        await DisposalFixtures.ExecuteAsync(_targets.BodyPath, "CREATE TABLE Other (X INTEGER);");
        var service = CreateService();

        var result = await service.StartAsync(new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, await TokenAsync(service)));

        Assert.Equal(OldDatabaseDisposalReason.TargetUnsafe, result.Reason);
        Assert.True(File.Exists(_targets.BodyPath));
        Assert.Contains("拒否（対象の安全検査）", Assert.Single(DisposalAudits()).Detail);
    }

    [Fact]
    public async Task Start_UnverifiableBody_CanOnlyBeDeleted_WithExplicitConfirmation()
    {
        await File.WriteAllTextAsync(_targets.BodyPath, "not a database");
        var service = CreateService();
        var token = await TokenAsync(service);

        var archive = await service.StartAsync(new OldDatabaseDisposalRequest(
            OldDatabaseDisposalMethod.Archive, token, _archiveParent));
        Assert.Equal(OldDatabaseDisposalReason.ArchiveNotAllowedForTarget, archive.Reason);

        var unconfirmed = await service.StartAsync(new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, token));
        Assert.Equal(OldDatabaseDisposalReason.UnverifiableConfirmationRequired, unconfirmed.Reason);
        Assert.Contains("yagura.db", unconfirmed.Message);

        var deleted = await RunToEndAsync(service, new OldDatabaseDisposalRequest(
            OldDatabaseDisposalMethod.Delete, token, ConfirmDeleteUnverifiableFile: true));
        Assert.Equal(OldDatabaseDisposalOutcome.Completed, deleted.Outcome);
        Assert.False(File.Exists(_targets.BodyPath));
    }

    [Fact]
    public async Task Start_EmptyBody_IsDeletedWithoutConfirmation()
    {
        await File.WriteAllBytesAsync(_targets.BodyPath, []);
        var service = CreateService();

        var result = await RunToEndAsync(service, new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, await TokenAsync(service)));

        Assert.Equal(OldDatabaseDisposalOutcome.Completed, result.Outcome);
        Assert.False(File.Exists(_targets.BodyPath));
    }

    [Fact]
    public async Task Start_SidecarFilesOnly_AreDeleted()
    {
        await File.WriteAllBytesAsync(_targets.WalPath, new byte[32]);
        await File.WriteAllBytesAsync(_targets.ShmPath, new byte[32]);
        var service = CreateService();

        var result = await RunToEndAsync(service, new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, await TokenAsync(service)));

        Assert.Equal(OldDatabaseDisposalOutcome.Completed, result.Outcome);
        Assert.False(File.Exists(_targets.WalPath));
        Assert.False(File.Exists(_targets.ShmPath));
    }

    // ---- 削除の手順 ----

    [Fact]
    public async Task Delete_RemovesWalAndShmBeforeTheBody()
    {
        // 内容を持たない本体（チェックポイントを通らない）で、3 ファイルの削除の順序を決定的に確かめる。
        await File.WriteAllBytesAsync(_targets.BodyPath, []);
        await File.WriteAllBytesAsync(_targets.WalPath, new byte[8]);
        await File.WriteAllBytesAsync(_targets.ShmPath, new byte[8]);
        var service = CreateService();

        var result = await RunToEndAsync(service, new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, await TokenAsync(service)));

        Assert.Equal(OldDatabaseDisposalOutcome.Completed, result.Outcome);
        Assert.Equal([_targets.WalPath, _targets.ShmPath, _targets.BodyPath], _deleter.Deleted);
    }

    [Fact]
    public async Task Delete_StopsAtTheFirstFailure_AndKeepsTheBody()
    {
        await File.WriteAllBytesAsync(_targets.BodyPath, []);
        await File.WriteAllBytesAsync(_targets.WalPath, new byte[8]);
        await File.WriteAllBytesAsync(_targets.ShmPath, new byte[8]);
        _deleter.FailSuffix = "-shm";
        var service = CreateService();

        var result = await RunToEndAsync(service, new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, await TokenAsync(service)));

        Assert.Equal(OldDatabaseDisposalOutcome.DeletePartiallyFailed, result.Outcome);
        Assert.Equal([_targets.WalPath], _deleter.Deleted);
        Assert.Equal([_targets.ShmPath, _targets.BodyPath], result.RemainingFiles);
        Assert.True(File.Exists(_targets.BodyPath));
    }

    [Fact]
    public async Task Delete_BodyFailure_IsPartialFailure_ListingTheRemainingFile()
    {
        await SeedMigratedAsync();
        var service = CreateService();
        _deleter.FailSuffix = "yagura.db";

        var result = await RunToEndAsync(service, new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, await TokenAsync(service)));

        Assert.Equal(OldDatabaseDisposalOutcome.DeletePartiallyFailed, result.Outcome);
        Assert.Equal(OldDatabaseDisposalReason.DeleteFailed, result.Reason);
        Assert.Equal([_targets.BodyPath], result.RemainingFiles);
        Assert.Contains("閉じてから", result.Message);
        Assert.Contains(DisposalAudits(), e => e.Detail!.Contains("結果=削除の部分失敗") && e.Detail.Contains(_targets.BodyPath));
    }

    [Fact]
    public async Task Delete_SharingViolation_IsRetried_ThenSucceeds()
    {
        await SeedMigratedAsync();
        var service = CreateService();
        _deleter.SharingViolationSuffix = "yagura.db";
        _deleter.SharingViolationTimes = 3;

        var result = await RunToEndAsync(service, new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, await TokenAsync(service)));

        Assert.Equal(OldDatabaseDisposalOutcome.Completed, result.Outcome);
        Assert.False(File.Exists(_targets.BodyPath));
    }

    [Fact]
    public async Task Delete_SharingViolationBeyondTheBudget_IsPartialFailure()
    {
        await SeedMigratedAsync();
        var service = CreateService();
        _deleter.SharingViolationSuffix = "yagura.db";
        _deleter.SharingViolationTimes = int.MaxValue;

        var result = await RunToEndAsync(service, new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, await TokenAsync(service)));

        Assert.Equal(OldDatabaseDisposalOutcome.DeletePartiallyFailed, result.Outcome);
        Assert.True(File.Exists(_targets.BodyPath));
    }

    [Fact]
    public async Task Rerun_AfterPartialFailure_StartsFromTheRemainingFiles()
    {
        await SeedMigratedAsync();
        var service = CreateService();
        _deleter.FailSuffix = "yagura.db";
        await RunToEndAsync(service, new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, await TokenAsync(service)));

        _deleter.FailSuffix = null;
        var second = await RunToEndAsync(service, new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, await TokenAsync(service)));

        Assert.Equal(OldDatabaseDisposalOutcome.Completed, second.Outcome);
        Assert.Equal(OldDatabaseDisposalState.NotApplicable, (await service.GetStatusAsync()).State);
    }

    // ---- 完了時の後片付けと監査記録 ----

    [Fact]
    public async Task Completion_RemovesTheMigrationCheckpoint_AndRecordsStartAndEnd()
    {
        await SeedMigratedAsync(20);
        var service = CreateService();

        var result = await RunToEndAsync(service, new OldDatabaseDisposalRequest(
            OldDatabaseDisposalMethod.Delete, await TokenAsync(service), OperatorAddress: "127.0.0.1"));

        Assert.Equal(OldDatabaseDisposalOutcome.Completed, result.Outcome);
        Assert.False(File.Exists(Path.Combine(_dataRoot, LogMigrationService.CheckpointFileName)));
        Assert.Null(PromotionSwitchBoundaryStore.TryRead(_dataRoot));

        var audits = DisposalAudits();
        Assert.Equal(2, audits.Count);
        Assert.Contains("開始", audits[0].Detail);
        Assert.Contains(_targets.BodyPath, audits[0].Detail);
        Assert.Contains("結果=完了", audits[1].Detail);
        Assert.Contains("移行チェックポイント=撤去（完了記録=あり、移行件数=20）", audits[1].Detail);
        Assert.All(audits, e => Assert.Equal("127.0.0.1", e.RemoteAddress));
        // ログの内容は記録しない。
        Assert.All(audits, e => Assert.DoesNotContain("old-0", e.Detail));
    }

    // ---- 削除の前の監査記録は必須（database.md §6.3「監査記録」） ----

    [Fact]
    public async Task Start_StartRecordCannotBeWritten_IsRejected_AndChangesNothing()
    {
        await SeedMigratedAsync(10);
        _audit.ConfirmedWriteFails = _ => true;
        var service = CreateService();
        var token = await TokenAsync(service);

        var result = await service.StartAsync(new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Delete, token));

        Assert.Equal(OldDatabaseDisposalOutcome.Rejected, result.Outcome);
        Assert.Equal(OldDatabaseDisposalReason.AuditUnavailable, result.Reason);
        Assert.Null(service.CurrentRun);
        Assert.True(File.Exists(_targets.BodyPath));
        Assert.Empty(_deleter.Deleted);
        Assert.True(File.Exists(Path.Combine(_dataRoot, LogMigrationService.CheckpointFileName)));
        Assert.Empty(DisposalAudits());

        // 拒否はトークンを消費しない（監査の経路が戻れば同じ画面から実行できる）。
        Assert.Equal(token, await TokenAsync(service));
        Assert.Equal(OldDatabaseDisposalState.Pending, (await service.GetStatusAsync()).State);
    }

    [Fact]
    public async Task Archive_StartRecordCannotBeWritten_RemovesTheWorkDirectory()
    {
        await SeedMigratedAsync(10);
        _audit.ConfirmedWriteFails = _ => true;
        var service = CreateService();

        var result = await service.StartAsync(new OldDatabaseDisposalRequest(
            OldDatabaseDisposalMethod.Archive, await TokenAsync(service), _archiveParent));

        Assert.Equal(OldDatabaseDisposalReason.AuditUnavailable, result.Reason);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_archiveParent));
        Assert.True(File.Exists(_targets.BodyPath));
    }

    [Fact]
    public async Task Archive_DeletionStartRecordCannotBeWritten_StopsBeforeDeleting()
    {
        await SeedMigratedAsync(30);
        _audit.ConfirmedWriteFails = e => e.Detail!.Contains("削除の開始", StringComparison.Ordinal);
        var service = CreateService();

        var result = await RunToEndAsync(service, new OldDatabaseDisposalRequest(
            OldDatabaseDisposalMethod.Archive, await TokenAsync(service), _archiveParent));

        Assert.Equal(OldDatabaseDisposalOutcome.Interrupted, result.Outcome);
        Assert.Equal(OldDatabaseDisposalReason.AuditUnavailable, result.Reason);
        Assert.Empty(_deleter.Deleted);
        Assert.True(File.Exists(_targets.BodyPath));

        // 確定済みの書き出しファイルは残り、結果と終了の記録から辿れる。
        Assert.NotNull(result.ArchiveFilePath);
        Assert.True(File.Exists(result.ArchiveFilePath));
        Assert.Null(result.WorkDirectoryLeftBehind);
        var end = DisposalAudits().Last();
        Assert.Contains("結果=中断", end.Detail);
        Assert.Contains(result.ArchiveFilePath!, end.Detail);
        Assert.Equal(OldDatabaseDisposalState.LastAttemptFailed, (await service.GetStatusAsync()).State);
    }

    [Fact]
    public async Task Archive_StartRecord_DisclosesLogsBeyondRetention()
    {
        // 保存期間を過ぎたログも書き出したファイルに含める——その件数を開始の記録に残す。
        await SeedMigratedAsync(10);
        _retentionDays = 1;
        var service = CreateService();

        await RunToEndAsync(service, new OldDatabaseDisposalRequest(
            OldDatabaseDisposalMethod.Archive, await TokenAsync(service), _archiveParent));

        var start = DisposalAudits().First();
        Assert.Contains("保存期間を過ぎたログ=10 件（書き出したファイルに含む）", start.Detail);
    }

    // ---- 書き出して保管 ----

    [Fact]
    public async Task Archive_WritesAVerifiedSingleFile_AppliesTheFinalAcl_ThenDeletesTheOriginals()
    {
        var records = await SeedMigratedAsync(120);
        await DisposalFixtures.ExecuteAsync(_targets.BodyPath,
            "CREATE TABLE AdminAccounts (Username TEXT, PasswordHash TEXT); " +
            "INSERT INTO AdminAccounts VALUES ('ledger-marker-user', 'ledger-marker-hash');");
        var service = CreateService();

        var result = await RunToEndAsync(service, new OldDatabaseDisposalRequest(
            OldDatabaseDisposalMethod.Archive, await TokenAsync(service), _archiveParent));

        Assert.Equal(OldDatabaseDisposalOutcome.Completed, result.Outcome);
        var archive = result.ArchiveFilePath!;
        Assert.True(File.Exists(archive));
        var workDirectory = Path.GetDirectoryName(archive)!;
        Assert.Equal(_archiveParent, Path.GetDirectoryName(workDirectory));
        Assert.Matches(@"^yagura-archive-\d{8}T\d{6}Z$", Path.GetFileName(workDirectory));
        Assert.Equal(Path.GetFileName(workDirectory) + ".db", Path.GetFileName(archive));
        Assert.True(_platform.IsFinalized(workDirectory));
        Assert.True(_platform.IsFinalized(archive));
        Assert.Equal([archive], Directory.GetFiles(workDirectory));
        Assert.Equal(await SqliteArchiveExporter.ComputeSha256Async(archive, CancellationToken.None), result.ArchiveSha256);
        Assert.False(File.Exists(_targets.BodyPath));

        // 内容: ログの件数が一致し、管理者アカウント台帳の表は含まれない。
        await using (var connection = await OldSqliteDatabase.OpenAsync(archive, CancellationToken.None, SqliteOpenMode.ReadOnly))
        {
            var summary = await OldSqliteDatabase.ReadSummaryAsync(connection, CancellationToken.None);
            Assert.Equal(records.Count, summary.RecordCount);
            Assert.False(await OldSqliteDatabase.TableExistsAsync(connection, "AdminAccounts", CancellationToken.None));
        }

        // 監査: 開始・削除の開始（SHA-256 つき）・終了の 3 件。
        var audits = DisposalAudits();
        Assert.Equal(3, audits.Count);
        Assert.Contains("削除の開始", audits[1].Detail);
        Assert.Contains(result.ArchiveSha256!, audits[1].Detail);
        Assert.Contains("件数照合=ログ 120/120", audits[1].Detail);
        Assert.Contains("整合性検査=ok", audits[1].Detail);
        Assert.Contains("保管先のドライブ種別=Fixed", audits[0].Detail);
    }

    [Fact]
    public async Task Archive_FileBytesContainNeitherDeletedRowsNorTheLedger()
    {
        // 空き領域の残留（保存期間で削除したログ・管理者アカウント台帳）を書き出したファイルに残さない
        // （database.md §6.3 手順 6）。同梱の SQLite で、書き出し方式が残留を除くことをバイト列で確かめる。
        await SeedMigratedAsync(50);
        await using (var old = new SqliteLogStore(_targets.BodyPath))
        {
            await old.WriteBatchAsync(Enumerable.Range(0, 200)
                .Select(i => DisposalFixtures.Record(DisposalFixtures.BaseTime.AddYears(-1).AddMinutes(i), $"DELETED-ROW-MARKER-{i:D4}"))
                .ToList());
            await old.DeleteOlderThanAsync(DisposalFixtures.BaseTime.AddMonths(-1));
        }

        await DisposalFixtures.ExecuteAsync(_targets.BodyPath,
            "CREATE TABLE AdminAccounts (Username TEXT, PasswordHash TEXT); " +
            "INSERT INTO AdminAccounts VALUES ('LEDGER-USER-MARKER', 'LEDGER-HASH-MARKER');");
        var service = CreateService();

        var result = await RunToEndAsync(service, new OldDatabaseDisposalRequest(
            OldDatabaseDisposalMethod.Archive, await TokenAsync(service), _archiveParent, ConfirmDisposeWithoutMigration: true));

        Assert.Equal(OldDatabaseDisposalOutcome.Completed, result.Outcome);
        var bytes = await File.ReadAllBytesAsync(result.ArchiveFilePath!);
        Assert.False(Contains(bytes, "DELETED-ROW-MARKER"), "削除済みの行の目印が書き出したファイルに残っている。");
        Assert.False(Contains(bytes, "LEDGER-USER-MARKER"), "管理者アカウント台帳の目印が書き出したファイルに残っている。");
        Assert.False(Contains(bytes, "LEDGER-HASH-MARKER"), "管理者アカウント台帳の目印が書き出したファイルに残っている。");
        Assert.True(Contains(bytes, "old-00000"), "ログ本体は書き出したファイルに含まれる。");
    }

    [Fact]
    public async Task Archive_FinalAclFailure_IsArchiveFailure_AndTheOriginalsStay()
    {
        await SeedMigratedAsync();
        _platform.ApplyFinalAclFailure = new UnauthorizedAccessException("cannot set acl (test)");
        var service = CreateService();
        var notified = 0;
        service.FailureFinalized = (_, _) => Interlocked.Increment(ref notified);

        var result = await RunToEndAsync(service, new OldDatabaseDisposalRequest(
            OldDatabaseDisposalMethod.Archive, await TokenAsync(service), _archiveParent));

        Assert.Equal(OldDatabaseDisposalOutcome.ArchiveFailed, result.Outcome);
        Assert.Equal(OldDatabaseDisposalReason.FinalizationFailed, result.Reason);
        Assert.StartsWith("書き出して保管できませんでした。元のファイルは変更していません。", result.Message);
        Assert.True(File.Exists(_targets.BodyPath));
        Assert.Empty(Directory.GetDirectories(_archiveParent));
        Assert.Equal(1, notified);
        Assert.DoesNotContain(DisposalAudits(), e => e.Detail!.Contains("削除の開始"));
    }

    [Fact]
    public async Task Archive_CancelBeforeConfirmation_IsInterrupted_AndCleansUp()
    {
        await SeedMigratedAsync();
        var service = CreateService();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.BeforeExportForTest = async token =>
        {
            reached.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        };

        var started = await service.StartAsync(new OldDatabaseDisposalRequest(
            OldDatabaseDisposalMethod.Archive, await TokenAsync(service), _archiveParent));
        Assert.Equal(OldDatabaseDisposalOutcome.Started, started.Outcome);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(OldDatabaseDisposalState.DisposalRunning, (await service.GetStatusAsync()).State);

        Assert.True(await service.CancelAsync());
        await service.CurrentRun!.WaitAsync(TimeSpan.FromSeconds(10));

        var result = (await service.GetStatusAsync()).LastResult!;
        Assert.Equal(OldDatabaseDisposalOutcome.Interrupted, result.Outcome);
        Assert.Equal(OldDatabaseDisposalReason.Cancelled, result.Reason);
        Assert.True(File.Exists(_targets.BodyPath));
        Assert.Empty(Directory.GetDirectories(_archiveParent));
        Assert.Contains(DisposalAudits(), e => e.Detail!.Contains("結果=中断"));
    }

    [Fact]
    public async Task Archive_ServiceStop_CancelsWithinTheLimit()
    {
        await SeedMigratedAsync();
        var service = CreateService();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.BeforeExportForTest = async token =>
        {
            reached.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        };

        await service.StartAsync(new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Archive, await TokenAsync(service), _archiveParent));
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await service.StopAsync();
        stopwatch.Stop();

        Assert.True(service.CurrentRun!.IsCompleted);
        Assert.True(stopwatch.Elapsed < OldDatabaseDisposalOptions.Default.StopResponseTimeout);
        var result = (await service.GetStatusAsync()).LastResult!;
        Assert.Equal(OldDatabaseDisposalOutcome.Interrupted, result.Outcome);
        Assert.Equal(OldDatabaseDisposalReason.ServiceStopping, result.Reason);
        Assert.True(File.Exists(_targets.BodyPath));
    }

    [Fact]
    public async Task Archive_LivePressureDuringExport_CancelsAsArchiveFailure()
    {
        await SeedMigratedAsync();
        var service = CreateService();
        service.BeforeExportForTest = async token =>
        {
            Volatile.Write(ref _underPressure, true);
            await Task.Delay(Timeout.Infinite, token);
        };

        var result = await RunToEndAsync(service, new OldDatabaseDisposalRequest(
            OldDatabaseDisposalMethod.Archive, await TokenAsync(service), _archiveParent));

        Assert.Equal(OldDatabaseDisposalOutcome.ArchiveFailed, result.Outcome);
        Assert.Equal(OldDatabaseDisposalReason.LiveIngestionUnderPressure, result.Reason);
        Assert.True(File.Exists(_targets.BodyPath));
        Assert.Empty(Directory.GetDirectories(_archiveParent));
    }

    [Fact]
    public async Task Archive_FreeSpaceDropsDuringExport_CancelsAsArchiveFailure()
    {
        await SeedMigratedAsync();
        var service = CreateService();
        service.BeforeExportForTest = async token =>
        {
            _platform.AvailableFreeBytes = 10;
            await Task.Delay(Timeout.Infinite, token);
        };

        var result = await RunToEndAsync(service, new OldDatabaseDisposalRequest(
            OldDatabaseDisposalMethod.Archive, await TokenAsync(service), _archiveParent));

        Assert.Equal(OldDatabaseDisposalOutcome.ArchiveFailed, result.Outcome);
        Assert.Equal(OldDatabaseDisposalReason.InsufficientFreeSpace, result.Reason);
        Assert.True(File.Exists(_targets.BodyPath));
    }

    // ---- 保管先の条件 ----

    [Theory]
    [InlineData("", OldDatabaseDisposalReason.DestinationNotLocalAbsolutePath)]
    [InlineData("relative/folder", OldDatabaseDisposalReason.DestinationNotLocalAbsolutePath)]
    [InlineData(@"\\server\share", OldDatabaseDisposalReason.DestinationNetwork)]
    [InlineData(@"\\?\C:\Archive", OldDatabaseDisposalReason.DestinationNetwork)]
    public async Task Archive_DestinationForm_IsValidated(string input, OldDatabaseDisposalReason expected)
    {
        await SeedMigratedAsync();
        var service = CreateService();

        var result = await service.StartAsync(new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Archive, await TokenAsync(service), input));

        Assert.Equal(expected, result.Reason);
        Assert.True(File.Exists(_targets.BodyPath));
        Assert.Empty(DisposalAudits());
    }

    [Theory]
    [InlineData(ArchiveDriveKind.Network, OldDatabaseDisposalReason.DestinationNetwork)]
    [InlineData(ArchiveDriveKind.Removable, OldDatabaseDisposalReason.DestinationRemovable)]
    [InlineData(ArchiveDriveKind.Optical, OldDatabaseDisposalReason.DestinationRemovable)]
    public async Task Archive_DestinationDriveKind_IsValidated(ArchiveDriveKind kind, OldDatabaseDisposalReason expected)
    {
        await SeedMigratedAsync();
        _platform.DriveKind = kind;
        var service = CreateService();

        var result = await service.StartAsync(new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Archive, await TokenAsync(service), _archiveParent));

        Assert.Equal(expected, result.Reason);
    }

    [Fact]
    public async Task Archive_DestinationWithoutAcl_IsRejected()
    {
        await SeedMigratedAsync();
        _platform.PreservesAcl = false;
        var service = CreateService();

        var result = await service.StartAsync(new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Archive, await TokenAsync(service), _archiveParent));

        Assert.Equal(OldDatabaseDisposalReason.DestinationNoAcl, result.Reason);
    }

    [Fact]
    public async Task Archive_DestinationMissing_IsRejected_ProductDoesNotCreateTheParent()
    {
        await SeedMigratedAsync();
        var service = CreateService();
        var missing = Path.Combine(_temp.Path, "missing");

        var result = await service.StartAsync(new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Archive, await TokenAsync(service), missing));

        Assert.Equal(OldDatabaseDisposalReason.DestinationMissing, result.Reason);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task Archive_DestinationInsideTheDataRootOrSpool_IsRejected()
    {
        await SeedMigratedAsync();
        var service = CreateService();
        var insideData = Path.Combine(_dataRoot, "sub");
        var insideSpool = Path.Combine(_dataRoot, "spool", "x");
        Directory.CreateDirectory(insideData);
        Directory.CreateDirectory(insideSpool);
        var token = await TokenAsync(service);

        Assert.Equal(OldDatabaseDisposalReason.DestinationInsideManagedArea,
            (await service.StartAsync(new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Archive, token, insideData))).Reason);
        Assert.Equal(OldDatabaseDisposalReason.DestinationInsideManagedArea,
            (await service.StartAsync(new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Archive, token, insideSpool))).Reason);
    }

    [Fact]
    public async Task Archive_DestinationReachedThroughALinkIntoTheDataRoot_IsRejected()
    {
        await SeedMigratedAsync();
        var link = Path.Combine(_temp.Path, "link-to-data");
        try
        {
            Directory.CreateSymbolicLink(link, _dataRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // シンボリックリンクを作れない環境（権限なしの Windows 等）では検証できない。
        }

        var service = CreateService();

        var result = await service.StartAsync(new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Archive, await TokenAsync(service), link));

        Assert.Equal(OldDatabaseDisposalReason.DestinationInsideManagedArea, result.Reason);
    }

    [Fact]
    public async Task Archive_InsufficientFreeSpace_IsRejectedBeforeStarting()
    {
        await SeedMigratedAsync();
        _platform.AvailableFreeBytes = 2048;
        var service = CreateService();

        var result = await service.StartAsync(new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Archive, await TokenAsync(service), _archiveParent));

        Assert.Equal(OldDatabaseDisposalReason.InsufficientFreeSpace, result.Reason);
        Assert.Empty(Directory.GetDirectories(_archiveParent));
    }

    [Fact]
    public async Task Archive_ParentNotWritable_ShowsGrantAndRevokeCommands_WithTheServiceAccount()
    {
        await SeedMigratedAsync();
        _platform.DeniedParents.Add(_archiveParent);
        var service = CreateService();

        var result = await service.StartAsync(new OldDatabaseDisposalRequest(OldDatabaseDisposalMethod.Archive, await TokenAsync(service), _archiveParent));

        Assert.Equal(OldDatabaseDisposalReason.DestinationNotWritable, result.Reason);
        Assert.Equal($"icacls \"{_archiveParent}\" /grant \"NT SERVICE\\Yagura:(AD)\"", result.GrantCommand);
        Assert.Equal($"icacls \"{_archiveParent}\" /remove:g \"NT SERVICE\\Yagura\"", result.RevokeCommand);
        Assert.Empty(DisposalAudits());
    }

    [Fact]
    public async Task Archive_ExistingWorkDirectoryName_IsNotReused()
    {
        await SeedMigratedAsync();
        var now = new DateTimeOffset(2026, 11, 16, 12, 34, 56, TimeSpan.Zero);
        var service = CreateService(timeProvider: new FixedClock(now));
        var foreign = Path.Combine(_archiveParent, "yagura-archive-20261116T123456Z");
        Directory.CreateDirectory(foreign);
        await File.WriteAllTextAsync(Path.Combine(foreign, "keep.txt"), "not ours");

        var result = await RunToEndAsync(service, new OldDatabaseDisposalRequest(
            OldDatabaseDisposalMethod.Archive, await TokenAsync(service), _archiveParent));

        Assert.Equal(OldDatabaseDisposalOutcome.Completed, result.Outcome);
        Assert.True(File.Exists(Path.Combine(foreign, "keep.txt")));
        Assert.Equal("yagura-archive-20261116T123456Z-2", Path.GetFileName(Path.GetDirectoryName(result.ArchiveFilePath)));
    }

    [Fact]
    public async Task Archive_CleansUpOnlyAbandonedWorkDirectoriesWithTheWorkAcl()
    {
        await SeedMigratedAsync();
        var abandoned = Path.Combine(_archiveParent, "yagura-archive-20260101T000000Z");
        var lookalike = Path.Combine(_archiveParent, "yagura-archive-20260101T000001Z");
        var otherName = Path.Combine(_archiveParent, "yagura-archive-manual");
        foreach (var directory in new[] { abandoned, lookalike, otherName })
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "f.tmp"), "x");
        }

        _platform.MarkWorkInProgress(abandoned);
        _platform.MarkWorkInProgress(otherName);
        var service = CreateService();

        var result = await RunToEndAsync(service, new OldDatabaseDisposalRequest(
            OldDatabaseDisposalMethod.Archive, await TokenAsync(service), _archiveParent));

        Assert.Equal(OldDatabaseDisposalOutcome.Completed, result.Outcome);
        Assert.False(Directory.Exists(abandoned));
        Assert.True(Directory.Exists(lookalike));
        Assert.True(Directory.Exists(otherName));
        Assert.Contains(DisposalAudits(), e => e.Detail!.Contains(abandoned));
    }

    // ---- 確認材料 ----

    [Fact]
    public async Task Inspect_ReportsCounts_Judgment_AndLogsBeyondRetention()
    {
        await SeedMigratedAsync(30);
        _retentionDays = 1;
        var service = CreateService();

        var inspection = await service.InspectAsync();

        Assert.Equal(OldDatabaseTargetKind.SqliteDatabase, inspection.TargetKind);
        Assert.True(inspection.ArchiveAllowed);
        Assert.Equal(30, inspection.RecordCount);
        Assert.NotNull(inspection.Migration);
        Assert.True(inspection.Migration.Migrated, string.Join(",", inspection.Migration.Gaps));
        Assert.Equal(1, inspection.RetentionDays);
        Assert.Equal(30, inspection.RecordsBeyondRetentionCount);

        // 保管先の入力の前に示す目安: 作業ファイル 2 つ分（本体 + -wal の 2 倍）に空き容量の下限を足したもの。
        var oldBytes = SizeOrZero(_targets.BodyPath) + SizeOrZero(_targets.WalPath);
        Assert.Equal((2 * oldBytes) + 1024, inspection.RequiredArchiveFreeBytes);
    }

    private static long SizeOrZero(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    [Fact]
    public async Task Inspect_WhileDisposalHoldsTheGate_DoesNotOpenTheFile()
    {
        await SeedMigratedAsync();
        var service = CreateService();

        using (await _gate.TryAcquireAsync(OldDatabaseAccessHolder.Disposal, TimeSpan.Zero))
        {
            var inspection = await service.InspectAsync();
            Assert.Null(inspection.Migration);
            Assert.Null(inspection.RecordCount);
        }
    }

    [Fact]
    public async Task Status_RecordedOldFileUnderAnotherName_IsReportedAsMisplaced()
    {
        var renamed = Path.Combine(_dataRoot, "old-name.db");
        await DisposalFixtures.SeedOldAsync(renamed, 3);
        PromotionSwitchBoundaryStore.Write(_dataRoot, new PromotionSwitchBoundary(DateTime.UtcNow, renamed, null, null, 3));
        var service = CreateService();

        var status = await service.GetStatusAsync();
        var observation = service.Observe();

        Assert.Equal(OldDatabaseDisposalState.NotApplicable, status.State);
        Assert.Equal(renamed, status.MisplacedRecordedOldDatabasePath);
        Assert.NotNull(observation.UnresolvableReason);
    }

    [Fact]
    public async Task Status_DoesNotCreateTheOldFile()
    {
        var service = CreateService();

        await service.GetStatusAsync();
        await service.InspectAsync();
        service.Observe();

        Assert.False(File.Exists(_targets.BodyPath));
    }

    /// <summary>時刻だけを固定し、待機は実時間で進める時計。</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static bool Contains(byte[] haystack, string needle) =>
        haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0;
}
