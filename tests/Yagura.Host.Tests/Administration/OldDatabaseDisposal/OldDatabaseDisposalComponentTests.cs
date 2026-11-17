using Microsoft.Data.Sqlite;
using Yagura.Abstractions.Administration;
using Yagura.Host.Administration.OldDatabaseDisposal;
using Yagura.TestSupport;

namespace Yagura.Host.Tests.Administration.OldDatabaseDisposal;

/// <summary>
/// 処分の部品（対象の導出と安全検査・保管先のパスの形・切替の境界の記録・旧ファイルの読み取りの取り消し）の単体テスト
/// （database.md §6.3）。
/// </summary>
public sealed class OldDatabaseDisposalComponentTests : IDisposable
{
    private readonly TestTempDirectory _temp = new("disposal-parts");

    public OldDatabaseDisposalComponentTests() => Directory.CreateDirectory(_temp.Path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    // ---- 対象の導出 ----

    [Fact]
    public void Targets_AreDerivedFromTheDataRootAndTheConfiguredFileName_InDeletionOrder()
    {
        var targets = OldDatabaseTargetPaths.Derive(_temp.Path, "custom.db");

        Assert.Equal(Path.Combine(_temp.Path, "custom.db"), targets.BodyPath);
        Assert.Equal(
            [targets.BodyPath + "-wal", targets.BodyPath + "-shm", targets.BodyPath],
            targets.DeletionOrder);
    }

    // ---- 対象の安全検査 ----

    [Fact]
    public async Task Inspect_SqliteBody_IsSqliteDatabase()
    {
        var targets = OldDatabaseTargetPaths.Derive(_temp.Path, "yagura.db");
        await DisposalFixtures.SeedOldAsync(targets.BodyPath, 1);

        var inspection = OldDatabaseTargetInspector.Inspect(targets);

        Assert.Equal(OldDatabaseTargetKind.SqliteDatabase, inspection.Kind);
    }

    [Fact]
    public void Inspect_NoFiles_HasNoTargets()
    {
        Assert.True(OldDatabaseTargetInspector.Inspect(OldDatabaseTargetPaths.Derive(_temp.Path, "yagura.db")).NoTargets);
    }

    [Fact]
    public async Task Inspect_EmptyBody_IsEmptyBody()
    {
        var targets = OldDatabaseTargetPaths.Derive(_temp.Path, "yagura.db");
        await File.WriteAllBytesAsync(targets.BodyPath, []);

        Assert.Equal(OldDatabaseTargetKind.EmptyBody, OldDatabaseTargetInspector.Inspect(targets).Kind);
    }

    [Fact]
    public async Task Inspect_BodyWithoutTheSqliteHeader_IsUnverifiable()
    {
        var targets = OldDatabaseTargetPaths.Derive(_temp.Path, "yagura.db");
        await File.WriteAllTextAsync(targets.BodyPath, "hello, not sqlite");

        Assert.Equal(OldDatabaseTargetKind.UnverifiableBody, OldDatabaseTargetInspector.Inspect(targets).Kind);
    }

    [Fact]
    public async Task Inspect_OnlySidecarFiles_IsSidecarFilesOnly()
    {
        var targets = OldDatabaseTargetPaths.Derive(_temp.Path, "yagura.db");
        await File.WriteAllBytesAsync(targets.WalPath, new byte[4]);

        var inspection = OldDatabaseTargetInspector.Inspect(targets);

        Assert.Equal(OldDatabaseTargetKind.SidecarFilesOnly, inspection.Kind);
        Assert.Equal(targets.WalPath, Assert.Single(inspection.ExistingFiles).Path);
    }

    [Fact]
    public void Inspect_BodyIsADirectory_IsUnsafe()
    {
        var targets = OldDatabaseTargetPaths.Derive(_temp.Path, "yagura.db");
        Directory.CreateDirectory(targets.BodyPath);

        var inspection = OldDatabaseTargetInspector.Inspect(targets);

        Assert.Equal(OldDatabaseTargetKind.Unsafe, inspection.Kind);
        Assert.Contains("フォルダ", inspection.RejectionReason);
    }

    [Fact]
    public async Task Inspect_BodyIsASymbolicLink_IsUnsafe()
    {
        var targets = OldDatabaseTargetPaths.Derive(_temp.Path, "yagura.db");
        var elsewhere = Path.Combine(_temp.Path, "elsewhere.db");
        await DisposalFixtures.SeedOldAsync(elsewhere, 1);
        try
        {
            File.CreateSymbolicLink(targets.BodyPath, elsewhere);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // シンボリックリンクを作れない環境では検証できない。
        }

        var inspection = OldDatabaseTargetInspector.Inspect(targets);

        Assert.Equal(OldDatabaseTargetKind.Unsafe, inspection.Kind);
        Assert.Contains("再解析ポイント", inspection.RejectionReason);
    }

    [Fact]
    public async Task Inspect_ConfiguredNameCollidesWithAProductFile_IsUnsafe_EvenWhenEmpty()
    {
        // 設定の誤記で製品の別のファイル（設定ファイル等）を指していても消さない。
        var targets = OldDatabaseTargetPaths.Derive(_temp.Path, "yagura.json");
        await File.WriteAllBytesAsync(targets.BodyPath, []);

        Assert.Equal(OldDatabaseTargetKind.Unsafe, OldDatabaseTargetInspector.Inspect(targets).Kind);
    }

    // ---- 保管先のパスの形 ----

    [Theory]
    [InlineData(@"D:\Archive", true)]
    [InlineData(@"d:/Archive", true)]
    [InlineData(@"D:Archive", false)]
    [InlineData(@"Archive", false)]
    [InlineData(@"\Archive", false)]
    [InlineData(@"\\server\share", false)]
    public void DriveAbsolutePath_IsRecognized(string path, bool expected)
    {
        Assert.Equal(expected, ArchivePathRules.IsDriveAbsolutePath(path));
    }

    [Theory]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"\\?\C:\Archive", true)]
    [InlineData(@"\\.\PhysicalDrive0", true)]
    [InlineData("//server/share", true)]
    [InlineData(@"D:\Archive", false)]
    public void NetworkOrDevicePath_IsRecognized(string path, bool expected)
    {
        Assert.Equal(expected, ArchivePathRules.IsNetworkOrDevicePath(path));
    }

    [Fact]
    public void IsSameOrUnder_ComparesWholeSegments()
    {
        var root = Path.Combine(_temp.Path, "data");

        Assert.True(ArchivePathRules.IsSameOrUnder(root, root));
        Assert.True(ArchivePathRules.IsSameOrUnder(Path.Combine(root, "x"), root));
        Assert.False(ArchivePathRules.IsSameOrUnder(root + "-other", root));
        Assert.True(ArchivePathRules.IsSameOrUnder(root, Path.GetPathRoot(root)!));
    }

    [Fact]
    public void ResolveFinalPath_FollowsDirectoryLinks()
    {
        var real = Path.Combine(_temp.Path, "real");
        var link = Path.Combine(_temp.Path, "link");
        Directory.CreateDirectory(Path.Combine(real, "child"));
        try
        {
            Directory.CreateSymbolicLink(link, real);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var resolved = ArchivePathRules.ResolveFinalPath(Path.Combine(link, "child"));

        Assert.Equal(Path.Combine(Path.GetFullPath(real), "child"), resolved);
    }

    // ---- 切替の境界の記録 ----

    [Fact]
    public void Boundary_RoundTrips_AndMarkEffectiveOnlyOnce()
    {
        var switchedAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        PromotionSwitchBoundaryStore.Write(_temp.Path, new PromotionSwitchBoundary(switchedAt, @"C:\data\yagura.db", null, null, 10));

        var first = PromotionSwitchBoundaryStore.TryRead(_temp.Path)!;
        Assert.Equal(new DateTimeOffset(switchedAt), first.UnreconcilableAfter);

        var effective = switchedAt.AddMinutes(5);
        Assert.True(PromotionSwitchBoundaryStore.MarkEffectiveIfAbsent(_temp.Path, effective));
        Assert.False(PromotionSwitchBoundaryStore.MarkEffectiveIfAbsent(_temp.Path, effective.AddDays(1)));

        var marked = PromotionSwitchBoundaryStore.TryRead(_temp.Path)!;
        Assert.Equal(effective, marked.EffectiveSinceUtc);
        Assert.Equal(new DateTimeOffset(effective), marked.UnreconcilableAfter);
        Assert.Equal(10, marked.RecordCount);
    }

    [Fact]
    public void Boundary_AbsentOrCorrupt_ReadsAsUnknown_AndIsNotCreatedByTheEffectiveMark()
    {
        Assert.Null(PromotionSwitchBoundaryStore.TryRead(_temp.Path));
        Assert.False(PromotionSwitchBoundaryStore.MarkEffectiveIfAbsent(_temp.Path, DateTime.UtcNow));
        Assert.False(File.Exists(Path.Combine(_temp.Path, PromotionSwitchBoundaryStore.FileName)));

        File.WriteAllText(Path.Combine(_temp.Path, PromotionSwitchBoundaryStore.FileName), "{ broken");
        Assert.Null(PromotionSwitchBoundaryStore.TryRead(_temp.Path));
    }

    [Fact]
    public void RecordSwitch_KeepsTheEffectiveTimeOfAnEarlierSwitchOfTheSameFile()
    {
        // SQL Server で動いた区間（反映の時刻以降）は移行先にライブ受信がある。やり直した切替で照合できない区間の
        // 始まりを後ろへずらすと、縮退中に旧ファイルへ書かれたログの欠落がその区間のライブ受信に隠れる。
        var path = Path.Combine(_temp.Path, "yagura.db");
        var first = new PromotionSwitchBoundary(
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), path, null, null, 10,
            EffectiveSinceUtc: new DateTime(2026, 7, 1, 0, 5, 0, DateTimeKind.Utc));
        PromotionSwitchBoundaryStore.Write(_temp.Path, first);

        var written = PromotionSwitchBoundaryStore.RecordSwitch(
            _temp.Path,
            new PromotionSwitchBoundary(new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc), path, null, null, 20),
            reducedFromSqlServer: false);

        Assert.False(written);
        Assert.Equal(first, PromotionSwitchBoundaryStore.TryRead(_temp.Path));
    }

    [Fact]
    public void RecordSwitch_WhileReducedFromSqlServer_IsNotARealSwitch_AndDoesNotWrite()
    {
        var path = Path.Combine(_temp.Path, "yagura.db");
        var later = new PromotionSwitchBoundary(new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc), path, null, null, 20);

        Assert.False(PromotionSwitchBoundaryStore.RecordSwitch(_temp.Path, later, reducedFromSqlServer: true));
        Assert.Null(PromotionSwitchBoundaryStore.TryRead(_temp.Path));

        var notYetEffective = new PromotionSwitchBoundary(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), path, null, null, 10);
        PromotionSwitchBoundaryStore.Write(_temp.Path, notYetEffective);
        Assert.False(PromotionSwitchBoundaryStore.RecordSwitch(_temp.Path, later, reducedFromSqlServer: true));
        Assert.Equal(notYetEffective, PromotionSwitchBoundaryStore.TryRead(_temp.Path));
    }

    [Fact]
    public void RecordSwitch_ReplacesARecordThatNeverTookEffect()
    {
        // 再起動の前にウィザードをやり直した場合は、最後の切替が境界になる（SQL Server はまだ受信していない）。
        var path = Path.Combine(_temp.Path, "yagura.db");
        PromotionSwitchBoundaryStore.Write(_temp.Path,
            new PromotionSwitchBoundary(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), path, null, null, 10));
        var later = new PromotionSwitchBoundary(new DateTime(2026, 7, 1, 1, 0, 0, DateTimeKind.Utc), path, null, null, 12);

        Assert.True(PromotionSwitchBoundaryStore.RecordSwitch(_temp.Path, later, reducedFromSqlServer: false));
        Assert.Equal(later, PromotionSwitchBoundaryStore.TryRead(_temp.Path));
    }

    // ---- 旧ファイルの読み取りの失敗の分類 ----

    [Theory]
    [InlineData(11, OldDatabaseReadFailure.Corrupt)]
    [InlineData(26, OldDatabaseReadFailure.Corrupt)]
    [InlineData(5, OldDatabaseReadFailure.InUse)]
    [InlineData(6, OldDatabaseReadFailure.InUse)]
    [InlineData(10, OldDatabaseReadFailure.Unreadable)]
    [InlineData(13, OldDatabaseReadFailure.Unreadable)]
    [InlineData(14, OldDatabaseReadFailure.Unreadable)]
    [InlineData(8, OldDatabaseReadFailure.Unreadable)]
    [InlineData(266, OldDatabaseReadFailure.Unreadable)]
    [InlineData(261, OldDatabaseReadFailure.InUse)]
    public void ClassifyReadFailure_CallsOnlyCorruptionCorrupt(int code, OldDatabaseReadFailure expected)
    {
        Assert.Equal(expected, OldSqliteDatabase.ClassifyReadFailure(new SqliteException("test", code)));
        Assert.Equal(expected, OldSqliteDatabase.ClassifyReadFailure(new Yagura.Storage.LogStoreWriteException(
            Yagura.Storage.LogStoreFailureKind.Permanent, "wrapped (test)", new SqliteException("test", code))));
    }

    [Fact]
    public void ClassifyReadFailure_IgnoresFailuresThatAreNotTheOldFiles()
    {
        Assert.Null(OldSqliteDatabase.ClassifyReadFailure(new InvalidOperationException("target (test)")));
        Assert.Null(OldSqliteDatabase.ClassifyReadFailure(new Yagura.Storage.LogStoreWriteException(
            Yagura.Storage.LogStoreFailureKind.Transient, "sql server (test)", new TimeoutException())));
    }

    // ---- 旧ファイルの読み取り ----

    [Fact]
    public async Task OldDatabase_IsNotCreatedWhenMissing()
    {
        var missing = Path.Combine(_temp.Path, "missing.db");

        await Assert.ThrowsAsync<SqliteException>(() => OldSqliteDatabase.ReadSummaryAsync(missing, CancellationToken.None));

        Assert.False(File.Exists(missing));
    }

    [Fact]
    public async Task LongStatement_IsInterruptedByCancellation()
    {
        // 停止要求に「単一の文の途中」で応じる: 終わらない文を sqlite3_interrupt で止める。
        var path = Path.Combine(_temp.Path, "interrupt.db");
        await DisposalFixtures.SeedOldAsync(path, 1);
        await using var connection = await OldSqliteDatabase.OpenAsync(path, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT COUNT(*) FROM c;";
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        using var registration = OldSqliteDatabase.RegisterInterrupt(connection, cts.Token);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var exception = await Record.ExceptionAsync(() => Task.Run(() => command.ExecuteScalar()));
        stopwatch.Stop();

        Assert.NotNull(exception);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"取り消しに {stopwatch.Elapsed} かかった。");
    }

    [Fact]
    public async Task Checkpoint_OnAnIdleDatabase_Completes()
    {
        var path = Path.Combine(_temp.Path, "checkpoint.db");
        await DisposalFixtures.SeedOldAsync(path, 10);

        Assert.Equal(OldDatabaseCheckpointOutcome.Completed, await OldSqliteDatabase.CheckpointAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task Checkpoint_WhileAnotherConnectionHoldsAReadTransaction_IsBusy()
    {
        var path = Path.Combine(_temp.Path, "busy.db");
        await DisposalFixtures.SeedOldAsync(path, 10);
        await DisposalFixtures.ExecuteAsync(path, "INSERT INTO SystemEvents (Kind, StartAt, EndAt, Approximate) VALUES ('k', 'a', 'b', 0);");

        await using var reader = new SqliteConnection(OldSqliteDatabase.ConnectionString(path));
        await reader.OpenAsync();
        // 遅延トランザクション（BEGIN DEFERRED）にする——既定の BEGIN IMMEDIATE は書き込みロックを取り、後続の書き込みを止める。
        await using var transaction = reader.BeginTransaction(deferred: true);
        await using (var command = reader.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "SELECT COUNT(*) FROM LogRecords;";
            await command.ExecuteScalarAsync();
        }

        // 読み取りトランザクションが WAL を参照している間に書き込みを足すと、TRUNCATE の反映は完了しない。
        await using (var writer = new SqliteConnection(OldSqliteDatabase.ConnectionString(path)))
        {
            await writer.OpenAsync();
            await using var insert = writer.CreateCommand();
            insert.CommandText = "INSERT INTO SystemEvents (Kind, StartAt, EndAt, Approximate) VALUES ('k2', 'a', 'b', 0);";
            await insert.ExecuteNonQueryAsync();
        }

        Assert.Equal(OldDatabaseCheckpointOutcome.Busy, await OldSqliteDatabase.CheckpointAsync(path, CancellationToken.None));
    }
}
