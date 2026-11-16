using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Administration.OldDatabaseDisposal;
using Yagura.Storage;
using Yagura.Storage.Sqlite;

namespace Yagura.Host.Tests.Administration.OldDatabaseDisposal;

/// <summary>監査記録を保持する（背景の処理からも書かれるためスレッド安全）。</summary>
internal sealed class ConcurrentAuditRecorder : IAuditRecorder
{
    private readonly ConcurrentQueue<AuditEvent> _events = new();

    public IReadOnlyList<AuditEvent> Events => _events.ToArray();

    public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        _events.Enqueue(auditEvent);
        return Task.CompletedTask;
    }
}

/// <summary>
/// ACL を持たない環境でも書き出して保管の経路を通すための <see cref="IArchivePlatform"/>。作業中の ACL・最終の
/// ACL は「製品が作った・確定した」フォルダの集合で表す。
/// </summary>
internal sealed class FakeArchivePlatform : IArchivePlatform
{
    private readonly ConcurrentDictionary<string, bool> _workInProgress = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _finalized = new(StringComparer.Ordinal);

    public bool AclSupported { get; set; } = true;

    public string CurrentIdentityName { get; set; } = @"NT SERVICE\Yagura";

    public ArchiveDriveKind DriveKind { get; set; } = ArchiveDriveKind.Fixed;

    public bool PreservesAcl { get; set; } = true;

    public long AvailableFreeBytes { get; set; } = 100L * 1024 * 1024 * 1024;

    /// <summary>ボリュームの識別（既定はすべて同じボリューム）。</summary>
    public Func<string, string> VolumeRootOf { get; set; } = _ => "VOL0";

    /// <summary>フォルダを作れない親フォルダ。</summary>
    public HashSet<string> DeniedParents { get; } = new(StringComparer.Ordinal);

    public Exception? ApplyFinalAclFailure { get; set; }

    public bool FinalAclVerifies { get; set; } = true;

    public List<string> CreatedDirectories { get; } = [];

    public bool IsLocalAbsolutePath(string path) =>
        Path.IsPathFullyQualified(path) && !IsNetworkOrDevicePath(path);

    public bool IsNetworkOrDevicePath(string path) => ArchivePathRules.IsNetworkOrDevicePath(path);

    public ArchiveVolumeInfo? GetVolume(string path) =>
        new(VolumeRootOf(path), DriveKind, PreservesAcl ? "NTFS" : "FAT32", PreservesAcl, AvailableFreeBytes);

    public string ResolveFinalPath(string path) => ArchivePathRules.ResolveFinalPath(path);

    public WorkDirectoryCreation CreateProtectedWorkDirectory(string path, out string? failureDetail)
    {
        failureDetail = null;
        var parent = Path.GetDirectoryName(path)!;
        if (DeniedParents.Contains(parent))
        {
            failureDetail = "Access denied";
            return WorkDirectoryCreation.AccessDenied;
        }

        if (Directory.Exists(path) || File.Exists(path))
        {
            return WorkDirectoryCreation.AlreadyExists;
        }

        Directory.CreateDirectory(path);
        MarkWorkInProgress(path);
        lock (CreatedDirectories)
        {
            CreatedDirectories.Add(path);
        }

        return WorkDirectoryCreation.Created;
    }

    /// <summary>テストが「製品の作業中の ACL を持つフォルダ」を仕込む。</summary>
    public void MarkWorkInProgress(string path) => _workInProgress[path] = true;

    public bool HasWorkInProgressAcl(string directory) =>
        _workInProgress.ContainsKey(directory) && !_finalized.ContainsKey(directory);

    public void ApplyFinalAcl(string workDirectory, string archiveFile)
    {
        if (ApplyFinalAclFailure is { } failure)
        {
            throw failure;
        }

        _finalized[workDirectory] = true;
        _finalized[archiveFile] = true;
    }

    public bool HasFinalAcl(string workDirectory, string archiveFile) =>
        FinalAclVerifies && _finalized.ContainsKey(workDirectory) && _finalized.ContainsKey(archiveFile);

    public bool IsFinalized(string path) => _finalized.ContainsKey(path);
}

/// <summary>削除の順序を記録し、失敗・共有違反・待機を差し込める削除。</summary>
internal sealed class ScriptedFileDeleter : IOldDatabaseFileDeleter
{
    private readonly ConcurrentQueue<string> _deleted = new();

    public IReadOnlyList<string> Deleted => _deleted.ToArray();

    /// <summary>この名前で終わるファイルの削除を失敗させる（共有違反ではない失敗）。</summary>
    public string? FailSuffix { get; set; }

    /// <summary>この名前で終わるファイルの削除で共有違反を返す回数。</summary>
    public string? SharingViolationSuffix { get; set; }

    public int SharingViolationTimes { get; set; }

    /// <summary>設定すると、最初の削除の前にこれが完了するまで待つ（削除の段階を観測するため）。</summary>
    public TaskCompletionSource? Gate { get; set; }

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Delete(string path)
    {
        Entered.TrySetResult();
        Gate?.Task.Wait(TimeSpan.FromSeconds(30));

        if (FailSuffix is { } fail && path.EndsWith(fail, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("denied (test)");
        }

        if (SharingViolationSuffix is { } sharing && path.EndsWith(sharing, StringComparison.Ordinal) && SharingViolationTimes > 0)
        {
            SharingViolationTimes--;
            throw new IOException("sharing violation (test)", unchecked((int)0x80070020));
        }

        File.Delete(path);
        _deleted.Enqueue(path);
    }
}

/// <summary>旧ファイル・移行先の準備。</summary>
internal static class DisposalFixtures
{
    public static readonly DateTimeOffset BaseTime = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    public static LogRecord Record(DateTimeOffset receivedAt, string message) => new(
        ReceivedAt: receivedAt,
        SourceAddress: "192.0.2.1",
        SourcePort: 514,
        Protocol: Protocol.Udp,
        ParseStatus: ParseStatus.Parsed,
        Facility: 3,
        Severity: 6,
        Message: message);

    /// <summary>旧ファイルを作り、<paramref name="count"/> 件を 1 分間隔で入れる。</summary>
    public static async Task<List<LogRecord>> SeedOldAsync(string path, int count, DateTimeOffset? start = null, string prefix = "old")
    {
        var records = Enumerable.Range(0, count)
            .Select(i => Record((start ?? BaseTime).AddMinutes(i), $"{prefix}-{i:D5}"))
            .ToList();
        await using var store = new SqliteLogStore(path);
        await store.InitializeAsync();
        foreach (var chunk in records.Chunk(500))
        {
            await store.WriteBatchAsync(chunk);
        }

        return records;
    }

    /// <summary>移行先へ書く（移行の代わり）。</summary>
    public static async Task WriteAsync(SqliteLogStore store, IEnumerable<LogRecord> records)
    {
        foreach (var chunk in records.Chunk(500))
        {
            await store.WriteBatchAsync(chunk.Select(r => r with { Id = null }).ToList());
        }
    }

    /// <summary>移行チェックポイントを書く（<see cref="Yagura.Host.Administration.LogMigrationService"/> と同じ形式）。</summary>
    public static void WriteCheckpoint(string dataRoot, DateTimeOffset? lastReceivedAt, long? lastId, long migrated, bool completed)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            LastReceivedAtUtc = lastReceivedAt?.UtcDateTime,
            LastId = lastId,
            OldestReceivedAtUtc = (DateTime?)null,
            MigratedCount = migrated,
            CompletedAtUtc = completed ? DateTime.UtcNow : (DateTime?)null,
        });
        File.WriteAllText(Path.Combine(dataRoot, Yagura.Host.Administration.LogMigrationService.CheckpointFileName), json);
    }

    /// <summary>旧ファイルの最新レコードの位置。</summary>
    public static async Task<(DateTimeOffset ReceivedAt, long Id)> LatestPositionAsync(string path)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ReceivedAt, Id FROM LogRecords ORDER BY ReceivedAt DESC, Id DESC LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (DateTimeOffset.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind), reader.GetInt64(1));
    }

    /// <summary>旧ファイルに任意の SQL を流す（台帳の表・破損の再現用）。</summary>
    public static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
