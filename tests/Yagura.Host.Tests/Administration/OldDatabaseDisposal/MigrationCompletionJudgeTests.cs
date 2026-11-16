using Microsoft.Data.Sqlite;
using Yagura.Abstractions.Administration;
using Yagura.Host.Administration;
using Yagura.Host.Administration.OldDatabaseDisposal;
using Yagura.Storage;
using Yagura.Storage.Sqlite;
using Yagura.TestSupport;

namespace Yagura.Host.Tests.Administration.OldDatabaseDisposal;

/// <summary>
/// 処分の直前の移行の完了判定（database.md §6.3「移行の完了判定と明示確認」の規則 1〜4 とシステムイベント）。
/// </summary>
public sealed class MigrationCompletionJudgeTests : IAsyncLifetime
{
    private readonly TestTempDirectory _temp = new("judge-test");
    private string _oldPath = null!;
    private SqliteLogStore _target = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_temp.Path);
        _oldPath = Path.Combine(_temp.Path, "old.db");
        _target = new SqliteLogStore(Path.Combine(_temp.Path, "target.db"));
        await _target.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _target.DisposeAsync();
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private static PromotionSwitchBoundary BoundaryAt(DateTimeOffset effectiveSince) =>
        new(effectiveSince.UtcDateTime, "old.db", null, null, null, effectiveSince.UtcDateTime);

    private async Task<OldDatabaseMigrationJudgment> JudgeAsync(
        LogMigrationCheckpointState? checkpoint,
        PromotionSwitchBoundary? boundary,
        ILogStore? target = null,
        DateTimeOffset? retentionCutoff = null)
    {
        var summary = await OldSqliteDatabase.ReadSummaryAsync(_oldPath, CancellationToken.None);
        return await MigrationCompletionJudge.JudgeAsync(
            _oldPath, summary, checkpoint, boundary, target ?? _target, retentionCutoff, CancellationToken.None);
    }

    private async Task<LogMigrationCheckpointState> CompletedCheckpointAsync(int migrated)
    {
        var position = await DisposalFixtures.LatestPositionAsync(_oldPath);
        return new LogMigrationCheckpointState(true, position, migrated);
    }

    [Fact]
    public async Task FullyMigrated_IsMigrated()
    {
        var records = await DisposalFixtures.SeedOldAsync(_oldPath, 180);
        await DisposalFixtures.WriteAsync(_target, records);

        var judgment = await JudgeAsync(await CompletedCheckpointAsync(180), BoundaryAt(DisposalFixtures.BaseTime.AddDays(1)));

        Assert.True(judgment.Migrated, string.Join(",", judgment.Gaps));
        Assert.False(judgment.RequiresConfirmation);
        Assert.Equal(0, judgment.UnmigratedRecordCount);
    }

    [Fact]
    public async Task NoCheckpoint_IsNotCompleted_AndEveryRecordIsUnmigrated()
    {
        var records = await DisposalFixtures.SeedOldAsync(_oldPath, 40);

        var judgment = await JudgeAsync(null, BoundaryAt(DisposalFixtures.BaseTime.AddDays(1)));

        Assert.False(judgment.Migrated);
        Assert.Contains(OldDatabaseMigrationGap.NotCompleted, judgment.Gaps);
        Assert.True(judgment.RequiresConfirmation);
        Assert.Equal(40, judgment.UnmigratedRecordCount);
        Assert.Equal(records[0].ReceivedAt, judgment.UnmigratedOldest);
        Assert.Equal(records[^1].ReceivedAt, judgment.UnmigratedNewest);
    }

    [Fact]
    public async Task CompletedWithoutPosition_WithRecords_IsNotMigrated()
    {
        await DisposalFixtures.SeedOldAsync(_oldPath, 5);

        var judgment = await JudgeAsync(
            new LogMigrationCheckpointState(true, null, 0),
            BoundaryAt(DisposalFixtures.BaseTime.AddDays(1)));

        Assert.Contains(OldDatabaseMigrationGap.CompletedWithoutPosition, judgment.Gaps);
        Assert.Equal(5, judgment.UnmigratedRecordCount);
    }

    [Fact]
    public async Task RecordsAddedAfterCompletion_AreUnmigrated()
    {
        var records = await DisposalFixtures.SeedOldAsync(_oldPath, 60);
        await DisposalFixtures.WriteAsync(_target, records);
        var checkpoint = await CompletedCheckpointAsync(60);
        var added = await DisposalFixtures.SeedOldAsync(_oldPath, 7, DisposalFixtures.BaseTime.AddHours(2), prefix: "added");

        var judgment = await JudgeAsync(checkpoint, BoundaryAt(DisposalFixtures.BaseTime.AddDays(1)));

        Assert.Contains(OldDatabaseMigrationGap.RecordsAddedAfterCompletion, judgment.Gaps);
        Assert.Equal(7, judgment.UnmigratedRecordCount);
        Assert.Equal(added[0].ReceivedAt, judgment.UnmigratedOldest);
    }

    [Fact]
    public async Task HourlyReconciliation_FindsTheHourWhereTheTargetHasFewerRecords()
    {
        // 総数は足りていても（別の時間帯の重複・ライブ受信）、時間帯ごとに見ると欠けている——総数の比較では隠れる欠落。
        var records = await DisposalFixtures.SeedOldAsync(_oldPath, 180); // 00:00〜02:59 の 3 時間
        var secondHour = records.Where(r => r.ReceivedAt.Hour == 1).ToList();
        await DisposalFixtures.WriteAsync(_target, records.Except(secondHour.Take(5)));
        await DisposalFixtures.WriteAsync(_target, records.Where(r => r.ReceivedAt.Hour == 0).Take(10));

        var judgment = await JudgeAsync(await CompletedCheckpointAsync(180), BoundaryAt(DisposalFixtures.BaseTime.AddDays(1)));

        Assert.Contains(OldDatabaseMigrationGap.HourlyReconciliationFailed, judgment.Gaps);
        Assert.Equal(secondHour.Count, judgment.UnmigratedRecordCount);
        Assert.Equal(secondHour[0].ReceivedAt, judgment.UnmigratedOldest);
        Assert.Equal(secondHour[^1].ReceivedAt, judgment.UnmigratedNewest);
    }

    [Fact]
    public async Task RecordsAfterTheSwitch_AreUnreconcilable_AndReportedSeparately()
    {
        // 切替 → 縮退運転 → 復帰: 切替の反映より後の時刻のログが旧ファイルにある。移行先にはライブ受信が混ざるため件数で照合できない。
        var records = await DisposalFixtures.SeedOldAsync(_oldPath, 120);
        await DisposalFixtures.WriteAsync(_target, records);
        var checkpoint = await CompletedCheckpointAsync(120);
        var boundary = BoundaryAt(DisposalFixtures.BaseTime.AddMinutes(100));

        var judgment = await JudgeAsync(checkpoint, boundary);

        Assert.Contains(OldDatabaseMigrationGap.RecordsInUnreconcilableInterval, judgment.Gaps);
        Assert.Equal(19, judgment.RecordsAfterSwitchCount);
        Assert.Equal(19, judgment.UnmigratedRecordCount);
        Assert.Equal(DisposalFixtures.BaseTime.AddMinutes(101), judgment.RecordsAfterSwitchOldest);
    }

    [Fact]
    public async Task UnknownBoundary_TreatsTheWholeRangeAsUnreconcilable()
    {
        var records = await DisposalFixtures.SeedOldAsync(_oldPath, 30);
        await DisposalFixtures.WriteAsync(_target, records);

        var judgment = await JudgeAsync(await CompletedCheckpointAsync(30), boundary: null);

        Assert.Contains(OldDatabaseMigrationGap.SwitchBoundaryUnknown, judgment.Gaps);
        Assert.Equal(30, judgment.UnmigratedRecordCount);
        Assert.True(judgment.RequiresConfirmation);
    }

    [Fact]
    public async Task SystemEventsMissingInTheTarget_AreReported()
    {
        var records = await DisposalFixtures.SeedOldAsync(_oldPath, 10);
        await DisposalFixtures.WriteAsync(_target, records);
        await using (var old = new SqliteLogStore(_oldPath))
        {
            await old.WriteSystemEventAsync(new SystemEvent(SystemEventKinds.DowntimeNormalStop,
                DisposalFixtures.BaseTime.AddMinutes(-30), DisposalFixtures.BaseTime.AddMinutes(-20), false));
            await old.WriteSystemEventAsync(new SystemEvent(SystemEventKinds.DowntimeCrashApproximate,
                DisposalFixtures.BaseTime.AddMinutes(-10), DisposalFixtures.BaseTime.AddMinutes(-5), true));
        }

        await _target.WriteSystemEventAsync(new SystemEvent(SystemEventKinds.DowntimeNormalStop,
            DisposalFixtures.BaseTime.AddMinutes(-30), DisposalFixtures.BaseTime.AddMinutes(-20), false));

        var judgment = await JudgeAsync(await CompletedCheckpointAsync(10), BoundaryAt(DisposalFixtures.BaseTime.AddDays(1)));

        Assert.Contains(OldDatabaseMigrationGap.SystemEventsNotMigrated, judgment.Gaps);
        Assert.Equal(1, judgment.UnmigratedSystemEventCount);
        Assert.Equal(DisposalFixtures.BaseTime.AddMinutes(-10), judgment.UnmigratedSystemEventOldest);
        Assert.True(judgment.RequiresConfirmation);
    }

    [Fact]
    public async Task HoursBeyondRetention_AreNotReconciled()
    {
        // 移行先では保存期間による削除が進む。保存期間を過ぎた時間帯の不足は「未移行」と呼ばない。
        var records = await DisposalFixtures.SeedOldAsync(_oldPath, 180);
        await DisposalFixtures.WriteAsync(_target, records.Where(r => r.ReceivedAt.Hour == 2));

        var judgment = await JudgeAsync(
            await CompletedCheckpointAsync(180),
            BoundaryAt(DisposalFixtures.BaseTime.AddDays(1)),
            retentionCutoff: DisposalFixtures.BaseTime.AddHours(2));

        Assert.True(judgment.Migrated, string.Join(",", judgment.Gaps));
    }

    [Fact]
    public async Task TargetWithoutHourlyCounts_CannotReconcile()
    {
        var records = await DisposalFixtures.SeedOldAsync(_oldPath, 10);
        await DisposalFixtures.WriteAsync(_target, records);

        var judgment = await JudgeAsync(
            await CompletedCheckpointAsync(10),
            BoundaryAt(DisposalFixtures.BaseTime.AddDays(1)),
            target: new StoreWithoutHourlyCounts(_target));

        Assert.Contains(OldDatabaseMigrationGap.TargetCannotReconcile, judgment.Gaps);
        Assert.Equal(10, judgment.UnmigratedRecordCount);
    }

    [Fact]
    public async Task EmptyOldFile_NeedsNoConfirmation()
    {
        await DisposalFixtures.SeedOldAsync(_oldPath, 0);

        var judgment = await JudgeAsync(null, null);

        Assert.False(judgment.RequiresConfirmation);
        Assert.Equal(0, judgment.UnmigratedRecordCount);
    }

    /// <summary>時間帯ごとの集計に対応しない移行先（照合できない場合の安全側を確かめる）。</summary>
    private sealed class StoreWithoutHourlyCounts(SqliteLogStore inner) : ILogStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => inner.InitializeAsync(cancellationToken);
        public Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken = default) => inner.WriteBatchAsync(records, cancellationToken);
        public Task<IReadOnlyList<LogRecordSummary>> QueryLatestAsync(int limit, TimeSpan timeout, CancellationToken cancellationToken = default) => inner.QueryLatestAsync(limit, timeout, cancellationToken);
        public Task<IReadOnlyList<LogRecordSummary>> QueryAsync(LogQuery query, CancellationToken cancellationToken = default) => inner.QueryAsync(query, cancellationToken);
        public Task WriteSystemEventAsync(SystemEvent systemEvent, CancellationToken cancellationToken = default) => inner.WriteSystemEventAsync(systemEvent, cancellationToken);
        public Task<DeleteOlderThanResult> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) => inner.DeleteOlderThanAsync(cutoff, cancellationToken);
        public Task<LogStoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) => inner.GetStatisticsAsync(cancellationToken);
        public Task<LogRecord?> FindByIdAsync(long id, TimeSpan timeout, CancellationToken cancellationToken = default) => inner.FindByIdAsync(id, timeout, cancellationToken);
        public Task<IReadOnlyList<SystemEvent>> QuerySystemEventsAsync(DateTimeOffset? from, DateTimeOffset? to, int limit, TimeSpan timeout, string? kind = null, CancellationToken cancellationToken = default) => inner.QuerySystemEventsAsync(from, to, limit, timeout, kind, cancellationToken);
        public Task<IReadOnlyList<SourceActivity>> QuerySourceActivityAsync(int limit, TimeSpan timeout, CancellationToken cancellationToken = default) => inner.QuerySourceActivityAsync(limit, timeout, cancellationToken);
        public Task<IReadOnlyList<SeverityCount>> QuerySeverityDistributionAsync(DateTimeOffset from, DateTimeOffset to, TimeSpan timeout, CancellationToken cancellationToken = default) => inner.QuerySeverityDistributionAsync(from, to, timeout, cancellationToken);
        public Task<IReadOnlyList<SourceActivity>> QueryTopTalkersAsync(DateTimeOffset from, DateTimeOffset to, int limit, TimeSpan timeout, CancellationToken cancellationToken = default) => inner.QueryTopTalkersAsync(from, to, limit, timeout, cancellationToken);
    }
}
