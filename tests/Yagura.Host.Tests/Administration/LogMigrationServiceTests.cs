using Microsoft.Extensions.Logging.Testing;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Administration;
using Yagura.Storage;
using Yagura.Storage.Sqlite;

namespace Yagura.Host.Tests.Administration;

/// <summary>
/// <see cref="LogMigrationService"/> の単体テスト（database.md §6.2。DB-5。Issue #266）。
/// 移行元・移行先とも SQLite（<see cref="ILogStore"/> + <see cref="IBulkLogReader"/> の
/// provider 非依存契約のみを使う）で、§6.2 の固定要件を決定的に検証する。
/// </summary>
public sealed class LogMigrationServiceTests : IAsyncLifetime
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-migration-test-{Guid.NewGuid():N}");
    private string _sourcePath = null!;
    private string _targetPath = null!;
    private SqliteLogStore _target = null!;
    private readonly RecordingAuditRecorder _auditRecorder = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        _sourcePath = Path.Combine(_dataRoot, "old.db");
        _targetPath = Path.Combine(_dataRoot, "new.db");

        await using (var source = new SqliteLogStore(_sourcePath))
        {
            await source.InitializeAsync();
        }

        _target = new SqliteLogStore(_targetPath);
        await _target.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _target.DisposeAsync();
        if (Directory.Exists(_dataRoot))
        {
            try
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class RecordingAuditRecorder : IAuditRecorder
    {
        public List<AuditEvent> Recorded { get; } = [];

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Recorded.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private LogMigrationService CreateService(bool promoted = true) => new(
        _dataRoot,
        _sourcePath,
        currentProviderIsSqlServer: promoted,
        _target,
        writeGate: null,
        _auditRecorder,
        new FakeLogger<LogMigrationService>());

    private static LogRecord CreateRecord(int minuteOffset, string message) => new(
        ReceivedAt: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minuteOffset),
        SourceAddress: "192.0.2.1",
        SourcePort: 514,
        Protocol: Protocol.Udp,
        ParseStatus: ParseStatus.Parsed,
        Facility: 3,
        Severity: 6,
        Message: message);

    private async Task SeedSourceAsync(int count)
    {
        await using var source = new SqliteLogStore(_sourcePath);
        var records = Enumerable.Range(0, count).Select(i => CreateRecord(i, $"record-{i:D5}")).ToList();
        foreach (var chunk in records.Chunk(500))
        {
            await source.WriteBatchAsync(chunk);
        }
    }

    [Fact]
    public async Task GetStatus_NotPromoted_ReturnsNotPromoted()
    {
        var status = await CreateService(promoted: false).GetStatusAsync();

        Assert.Equal(LogMigrationAvailability.NotPromoted, status.Availability);
    }

    [Fact]
    public async Task Run_MigratesAllRecords_PreservesReceivedAt_AndPassesValidation()
    {
        await SeedSourceAsync(1234);
        var service = CreateService();

        var result = await service.RunAsync("127.0.0.1", "windows", @"CONTOSO\admin");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1234, result.SourceRecordCount);
        Assert.Equal(1234, result.MigratedCount);
        Assert.True(result.TargetCountInRange >= 1234);

        // 要件④: ReceivedAt を再刻印しない（最古・最新が移行元と一致する）。
        var targetCount = await ((IBulkLogReader)_target).CountAsync(null);
        Assert.Equal(1234, targetCount);
        var latest = await _target.QueryLatestAsync(limit: 1, timeout: TimeSpan.FromSeconds(5));
        Assert.Equal(CreateRecord(1233, "x").ReceivedAt, latest[0].ReceivedAt);

        // 要件⑤: 移行由来の識別——移行範囲のシステムイベントが移行先に残る。
        var events = await _target.QuerySystemEventsAsync(
            null, null, limit: 10, timeout: TimeSpan.FromSeconds(5), kind: SystemEventKinds.MigrationImport);
        var importEvent = Assert.Single(events);
        Assert.Equal(CreateRecord(0, "x").ReceivedAt, importEvent.StartAt);
        Assert.Contains("migrated=1234", importEvent.Details);

        // 管理操作の監査（2018）。
        var audit = Assert.Single(_auditRecorder.Recorded);
        Assert.Equal(AuditEventKind.LogMigrationExecuted, audit.Kind);
        Assert.Contains("検証合格", audit.Detail);

        // 完了後の状態は Completed（旧ファイルの処分——DB-7——への引き継ぎ点）。
        var status = await service.GetStatusAsync();
        Assert.Equal(LogMigrationAvailability.Completed, status.Availability);
    }

    [Fact]
    public async Task Run_CancelledMidway_ResumesFromCheckpoint_AndValidationExplainsDuplicates()
    {
        await SeedSourceAsync(2000);
        var service = CreateService();

        // 進捗 1 回目（500 件 = 1 バッチ）でキャンセル → チェックポイントから再開できる（要件③）。
        using var cts = new CancellationTokenSource();
        var progressCount = 0;
        var progress = new Progress<LogMigrationProgress>(_ =>
        {
            if (Interlocked.Increment(ref progressCount) == 1)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RunAsync(null, null, null, progress, cts.Token));

        var interrupted = await service.GetStatusAsync();
        Assert.Equal(LogMigrationAvailability.Ready, interrupted.Availability);
        Assert.True(interrupted.MigratedCount > 0, "チェックポイントに移行済み件数が残ること。");

        // 再開 → 完走。at-least-once のため移行先は移行元以上（重複許容）で検証合格する（要件②）。
        var result = await service.RunAsync(null, null, null);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2000, result.SourceRecordCount);
        Assert.True(result.TargetCountInRange >= 2000);
    }

    [Fact]
    public async Task Run_EmptySource_CompletesWithoutSystemEvent()
    {
        var service = CreateService();

        var result = await service.RunAsync(null, null, null);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.SourceRecordCount);
        var events = await _target.QuerySystemEventsAsync(
            null, null, limit: 10, timeout: TimeSpan.FromSeconds(5), kind: SystemEventKinds.MigrationImport);
        Assert.Empty(events);
    }

    [Fact]
    public async Task Run_AfterCompleted_Throws()
    {
        await SeedSourceAsync(10);
        var service = CreateService();
        await service.RunAsync(null, null, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(null, null, null));
    }

    [Fact]
    public async Task BulkReader_ReadAllAscending_OrdersAndResumes()
    {
        await SeedSourceAsync(1500);
        await using var source = new SqliteLogStore(_sourcePath);
        var reader = (IBulkLogReader)source;

        var all = new List<LogRecord>();
        await foreach (var record in reader.ReadAllAscendingAsync(null))
        {
            all.Add(record);
        }

        Assert.Equal(1500, all.Count);
        Assert.True(all.Zip(all.Skip(1)).All(p =>
            p.First.ReceivedAt < p.Second.ReceivedAt
            || (p.First.ReceivedAt == p.Second.ReceivedAt && p.First.Id < p.Second.Id)));

        // 再開カーソル: 700 件目の直後から読むと残り 800 件。
        var resumeAfter = new BulkReadCursor(all[699].ReceivedAt, all[699].Id!.Value);
        var resumed = new List<LogRecord>();
        await foreach (var record in reader.ReadAllAscendingAsync(resumeAfter))
        {
            resumed.Add(record);
        }

        Assert.Equal(800, resumed.Count);
        Assert.Equal(all[700].Id, resumed[0].Id);
    }
}
