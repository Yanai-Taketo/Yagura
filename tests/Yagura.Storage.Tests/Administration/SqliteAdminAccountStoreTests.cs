using Yagura.Storage.Administration;
using Yagura.Storage.Administration.Sqlite;

namespace Yagura.Storage.Tests.Administration;

/// <summary>
/// <see cref="SqliteAdminAccountStore"/> の単体テスト（ADR-0010 決定 3。Phase 1）。
/// </summary>
public sealed class SqliteAdminAccountStoreTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"yagura-admin-accounts-{Guid.NewGuid():N}.db");
    private SqliteAdminAccountStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new SqliteAdminAccountStore(_databasePath);
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();

        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        // 冪等性(ILogStore と同じ規約): 2 回目の初期化も例外を投げない。
        await _store.InitializeAsync();
    }

    [Fact]
    public async Task HasAnyAccountAsync_NoAccounts_ReturnsFalse()
    {
        Assert.False(await _store.HasAnyAccountAsync());
        Assert.Null(await _store.GetSoleAccountAsync());
    }

    [Fact]
    public async Task UpsertAsync_ThenFindByUsername_ReturnsAccount_CaseInsensitive()
    {
        await _store.UpsertAsync("Admin1", "hash-1");

        Assert.True(await _store.HasAnyAccountAsync());

        var byExactCase = await _store.FindByUsernameAsync("Admin1");
        Assert.NotNull(byExactCase);
        Assert.Equal("Admin1", byExactCase!.Username);
        Assert.Equal("hash-1", byExactCase.PasswordHash);
        Assert.Equal(0, byExactCase.FailedAttemptCount);
        Assert.Null(byExactCase.LockoutUntilUtc);

        var byDifferentCase = await _store.FindByUsernameAsync("admin1");
        Assert.NotNull(byDifferentCase);
        Assert.Equal("Admin1", byDifferentCase!.Username);

        var sole = await _store.GetSoleAccountAsync();
        Assert.NotNull(sole);
        Assert.Equal("Admin1", sole!.Username);
    }

    [Fact]
    public async Task UpsertAsync_ExistingUsername_ReplacesHashAndResetsLockout()
    {
        await _store.UpsertAsync("admin1", "hash-old");
        await _store.RecordFailedLoginAsync("admin1", DateTimeOffset.UtcNow, lockoutThreshold: 1, lockoutDuration: TimeSpan.FromMinutes(5));

        var locked = await _store.FindByUsernameAsync("admin1");
        Assert.NotNull(locked!.LockoutUntilUtc);

        await _store.UpsertAsync("admin1", "hash-new");

        var reset = await _store.FindByUsernameAsync("admin1");
        Assert.Equal("hash-new", reset!.PasswordHash);
        Assert.Equal(0, reset.FailedAttemptCount);
        Assert.Null(reset.LockoutUntilUtc);
    }

    [Fact]
    public async Task FindByUsernameAsync_UnknownUsername_ReturnsNull()
    {
        Assert.Null(await _store.FindByUsernameAsync("nobody"));
    }

    [Fact]
    public async Task RecordSuccessfulLoginAsync_ResetsFailedAttemptCountAndSetsLastLogin()
    {
        await _store.UpsertAsync("admin1", "hash-1");
        await _store.RecordFailedLoginAsync("admin1", DateTimeOffset.UtcNow, lockoutThreshold: 5, lockoutDuration: TimeSpan.FromMinutes(5));

        var now = DateTimeOffset.UtcNow;
        await _store.RecordSuccessfulLoginAsync("admin1", now);

        var account = await _store.FindByUsernameAsync("admin1");
        Assert.Equal(0, account!.FailedAttemptCount);
        Assert.Null(account.LockoutUntilUtc);
        Assert.NotNull(account.LastLoginAtUtc);
    }

    [Fact]
    public async Task RecordFailedLoginAsync_BelowThreshold_IncrementsWithoutLockout()
    {
        await _store.UpsertAsync("admin1", "hash-1");

        var result = await _store.RecordFailedLoginAsync(
            "admin1", DateTimeOffset.UtcNow, lockoutThreshold: 3, lockoutDuration: TimeSpan.FromMinutes(5));

        Assert.Equal(1, result.FailedAttemptCount);
        Assert.False(result.LockedOutNow);
        Assert.Null(result.LockoutUntilUtc);
    }

    [Fact]
    public async Task RecordFailedLoginAsync_ReachesThreshold_LocksOut()
    {
        await _store.UpsertAsync("admin1", "hash-1");
        var baseline = DateTimeOffset.UtcNow;

        await _store.RecordFailedLoginAsync("admin1", baseline, lockoutThreshold: 2, lockoutDuration: TimeSpan.FromMinutes(15));
        var second = await _store.RecordFailedLoginAsync("admin1", baseline, lockoutThreshold: 2, lockoutDuration: TimeSpan.FromMinutes(15));

        Assert.Equal(2, second.FailedAttemptCount);
        Assert.True(second.LockedOutNow);
        Assert.Equal(baseline.AddMinutes(15), second.LockoutUntilUtc);

        var account = await _store.FindByUsernameAsync("admin1");
        Assert.Equal(baseline.AddMinutes(15), account!.LockoutUntilUtc);
    }

    [Fact]
    public async Task RecordFailedLoginAsync_ConcurrentFailures_DoNotLoseUpdates()
    {
        // 原子的インクリメントの並行検証（PR #217 レビュー指摘——read-modify-write の
        // lost update があると、並行するログイン失敗（分散送信元からの同時試行）でカウンタが
        // 実際の失敗回数より小さくなり、ロックアウト閾値到達が遅れる。ADR-0010 委任事項 4）。
        await _store.UpsertAsync("admin1", "hash-1");
        var baseline = DateTimeOffset.UtcNow;
        const int parallelAttempts = 20;

        var results = await Task.WhenAll(Enumerable.Range(0, parallelAttempts)
            .Select(_ => _store.RecordFailedLoginAsync(
                "admin1", baseline, lockoutThreshold: 100, lockoutDuration: TimeSpan.FromMinutes(15))));

        // 各試行が一意な増分後カウンタ値を観測する（重複 = lost update の証拠）。
        var observedCounts = results.Select(r => r.FailedAttemptCount).OrderBy(c => c).ToList();
        Assert.Equal(Enumerable.Range(1, parallelAttempts), observedCounts);

        // 最終カウンタ = 実際の失敗回数。
        var account = await _store.FindByUsernameAsync("admin1");
        Assert.Equal(parallelAttempts, account!.FailedAttemptCount);
    }

    [Fact]
    public async Task RecordFailedLoginAsync_ConcurrentFailuresAcrossThreshold_LockoutIsNotDelayed()
    {
        await _store.UpsertAsync("admin1", "hash-1");
        var baseline = DateTimeOffset.UtcNow;
        const int parallelAttempts = 10;
        const int threshold = 5;

        var results = await Task.WhenAll(Enumerable.Range(0, parallelAttempts)
            .Select(_ => _store.RecordFailedLoginAsync(
                "admin1", baseline, threshold, TimeSpan.FromMinutes(15))));

        // 閾値以上を観測した試行はすべてロックアウト到達として報告される。
        Assert.All(
            results.Where(r => r.FailedAttemptCount >= threshold),
            r => Assert.True(r.LockedOutNow));

        var account = await _store.FindByUsernameAsync("admin1");
        Assert.Equal(parallelAttempts, account!.FailedAttemptCount);
        Assert.NotNull(account.LockoutUntilUtc);
    }
}
