using Microsoft.Extensions.Time.Testing;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Observability.Auditing;

namespace Yagura.Host.Tests.Observability.Auditing;

/// <summary>
/// <see cref="AuditRetentionScheduler"/> の単体テスト（security.md §4.2 SEC-2。Issue #261）。
/// 削除判定（<see cref="AuditRetentionScheduler.DeleteExpiredOnceAsync"/>）を直接呼び出して
/// 決定的に検証する。時刻は <see cref="FakeTimeProvider"/>、ファイルの古さは
/// <see cref="File.SetLastWriteTimeUtc(string, DateTime)"/> で制御する。
/// </summary>
public sealed class AuditRetentionSchedulerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2027, 7, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-audit-retention-test-{Guid.NewGuid():N}");
    private readonly string _auditDirectory;
    private readonly FakeTimeProvider _timeProvider = new(Now);
    private readonly RecordingAuditRecorder _recorder = new();

    public AuditRetentionSchedulerTests()
    {
        _auditDirectory = Path.Combine(_dataRoot, FileAuditRecorder.DirectoryName);
        Directory.CreateDirectory(_auditDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            try
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
            catch (IOException)
            {
                // ベストエフォート（他テストと同じ判断）。
            }
        }
    }

    /// <summary>記録された監査事象を保持するだけのフェイク。</summary>
    private sealed class RecordingAuditRecorder : IAuditRecorder
    {
        public List<AuditEvent> Recorded { get; } = [];

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Recorded.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private AuditRetentionScheduler CreateScheduler(int? retentionDays) => new(
        _dataRoot,
        retentionDays,
        executionTimeOfDay: new TimeOnly(3, 0),
        _recorder,
        _timeProvider);

    /// <summary>監査ファイルを作成し、最終書き込み時刻を基準時刻から指定日数だけ過去に設定する。</summary>
    private string CreateAuditFile(string fileName, double ageDays)
    {
        var path = Path.Combine(_auditDirectory, fileName);
        File.WriteAllText(path, """{"Kind":"ConfigurationSaved"}""" + "\n");
        File.SetLastWriteTimeUtc(path, (Now - TimeSpan.FromDays(ageDays)).UtcDateTime);
        return path;
    }

    [Fact]
    public async Task DeleteExpiredOnce_DeletesOnlyFilesOlderThanRetention()
    {
        var expired = CreateAuditFile("audit-20260701.jsonl", ageDays: 366);
        var withinRetention = CreateAuditFile("audit-20270601.jsonl", ageDays: 45);
        var fresh = CreateAuditFile("audit-20270716.jsonl", ageDays: 0);

        var deleted = await CreateScheduler(retentionDays: 365).DeleteExpiredOnceAsync(CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(expired), "保持期間を超過したファイルは削除されること。");
        Assert.True(File.Exists(withinRetention), "保持期間内のファイルは残ること。");
        Assert.True(File.Exists(fresh), "当日のファイルは残ること。");
    }

    [Fact]
    public async Task DeleteExpiredOnce_LegacySingleFile_IsDeletedByLastWriteTime()
    {
        // 日次ローテーション導入前の単一ファイル（audit.jsonl）も、最終書き込み時刻が
        // 保持期間を超過すれば削除される（最終書き込み = ファイル内の最新事象の時刻であり、
        // それが期限切れなら中身はすべて期限切れ——AuditRetentionScheduler の remarks 参照）。
        var legacyExpired = CreateAuditFile(FileAuditRecorder.LegacyFileName, ageDays: 400);

        var deleted = await CreateScheduler(retentionDays: 365).DeleteExpiredOnceAsync(CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(legacyExpired));
    }

    [Fact]
    public async Task DeleteExpiredOnce_LegacyFileWithRecentWrite_IsKept()
    {
        // 最終書き込みが保持期間内の旧単一ファイルは、中身に期限内の事象を含み得るため残す。
        var legacyRecent = CreateAuditFile(FileAuditRecorder.LegacyFileName, ageDays: 100);

        var deleted = await CreateScheduler(retentionDays: 365).DeleteExpiredOnceAsync(CancellationToken.None);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(legacyRecent));
    }

    [Fact]
    public async Task DeleteExpiredOnce_RetentionDisabled_DeletesNothing()
    {
        // Audit:RetentionDays の不正値フォールバック（null = 削除しない）では何も消さない。
        var veryOld = CreateAuditFile("audit-20200101.jsonl", ageDays: 2000);

        var deleted = await CreateScheduler(retentionDays: null).DeleteExpiredOnceAsync(CancellationToken.None);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(veryOld));
        Assert.Empty(_recorder.Recorded);
    }

    [Fact]
    public async Task DeleteExpiredOnce_RecordsAuditEventWithDetail_WhenFilesWereDeleted()
    {
        // 証跡の削除自体を証跡に残す（2015。Issue #261）。
        CreateAuditFile("audit-20260601.jsonl", ageDays: 410);
        CreateAuditFile("audit-20260602.jsonl", ageDays: 409);

        var deleted = await CreateScheduler(retentionDays: 365).DeleteExpiredOnceAsync(CancellationToken.None);

        Assert.Equal(2, deleted);
        var recorded = Assert.Single(_recorder.Recorded);
        Assert.Equal(AuditEventKind.AuditRetentionApplied, recorded.Kind);
        Assert.Contains("deleted=2", recorded.Detail);
        Assert.Contains("retentionDays=365", recorded.Detail);
        Assert.Contains("audit-20260601.jsonl", recorded.Detail);
        Assert.Contains("audit-20260602.jsonl", recorded.Detail);
    }

    [Fact]
    public async Task DeleteExpiredOnce_NothingExpired_DoesNotRecordAuditEvent()
    {
        // 0 件の実行は記録しない（毎日のノイズ行で監査記録を埋めない——remarks「削除の証跡」）。
        CreateAuditFile("audit-20270715.jsonl", ageDays: 1);

        var deleted = await CreateScheduler(retentionDays: 365).DeleteExpiredOnceAsync(CancellationToken.None);

        Assert.Equal(0, deleted);
        Assert.Empty(_recorder.Recorded);
    }

    [Fact]
    public async Task DeleteExpiredOnce_AuditDirectoryMissing_ReturnsZeroWithoutError()
    {
        Directory.Delete(_auditDirectory, recursive: true);

        var deleted = await CreateScheduler(retentionDays: 365).DeleteExpiredOnceAsync(CancellationToken.None);

        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task DeleteExpiredOnce_LockedFile_SkipsItAndDeletesOthers()
    {
        // 1 ファイルの削除失敗（使用中等）が他ファイルの削除を妨げない。失敗分は次回の
        // 定期実行で再試行される（ファイル単位の独立性）。
        var lockedPath = CreateAuditFile("audit-20260601.jsonl", ageDays: 410);
        var deletablePath = CreateAuditFile("audit-20260602.jsonl", ageDays: 409);

        using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var deleted = await CreateScheduler(retentionDays: 365).DeleteExpiredOnceAsync(CancellationToken.None);

            Assert.Equal(1, deleted);
            Assert.True(File.Exists(lockedPath), "使用中のファイルは残る（次回再試行）。");
            Assert.False(File.Exists(deletablePath), "他の期限切れファイルは削除される。");
        }
    }

    [Fact]
    public async Task DeleteExpiredOnce_NonAuditFilesInDirectory_AreNeverTouched()
    {
        // 監査ファイルの命名パターン（audit*.jsonl）に一致しないファイルは、どれほど古くても
        // 削除対象にしない（誤爆防止）。
        var unrelated = Path.Combine(_auditDirectory, "notes.txt");
        File.WriteAllText(unrelated, "keep me");
        File.SetLastWriteTimeUtc(unrelated, (Now - TimeSpan.FromDays(2000)).UtcDateTime);

        var deleted = await CreateScheduler(retentionDays: 365).DeleteExpiredOnceAsync(CancellationToken.None);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task HostedService_StartupRun_DeletesExpiredFiles()
    {
        // IHostedService としての起動時 1 回実行（実質のキャッチアップ）が動くこと。
        var expired = CreateAuditFile("audit-20260601.jsonl", ageDays: 410);
        var scheduler = CreateScheduler(retentionDays: 365);

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            // 起動時実行はバックグラウンドで走るため、完了を短時間ポーリングで待つ
            // （FakeTimeProvider は Task.Delay の待機のみを制御し、実行自体は実スレッド）。
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (File.Exists(expired) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.False(File.Exists(expired), "起動時実行で期限切れファイルが削除されること。");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }
}
