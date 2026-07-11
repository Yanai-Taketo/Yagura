using Microsoft.Data.Sqlite;
using Yagura.Storage.Administration;
using Yagura.Storage.Administration.Sqlite;

namespace Yagura.Storage.Tests.Administration;

/// <summary>
/// <see cref="SqliteAdminAccountStore"/> の単体テスト（ADR-0010 決定 3。ADR-0011 決定 8 の
/// スキーマ簡素化——<c>FailedAttemptCount</c>/<c>LockoutUntilUtc</c> 列削除——を反映）。
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
        Assert.Null(byExactCase.LastLoginAtUtc);

        var byDifferentCase = await _store.FindByUsernameAsync("admin1");
        Assert.NotNull(byDifferentCase);
        Assert.Equal("Admin1", byDifferentCase!.Username);

        var sole = await _store.GetSoleAccountAsync();
        Assert.NotNull(sole);
        Assert.Equal("Admin1", sole!.Username);
    }

    [Fact]
    public async Task UpsertAsync_ExistingUsername_ReplacesHash()
    {
        await _store.UpsertAsync("admin1", "hash-old");
        await _store.UpsertAsync("admin1", "hash-new");

        var reset = await _store.FindByUsernameAsync("admin1");
        Assert.Equal("hash-new", reset!.PasswordHash);
    }

    [Fact]
    public async Task FindByUsernameAsync_UnknownUsername_ReturnsNull()
    {
        Assert.Null(await _store.FindByUsernameAsync("nobody"));
    }

    [Fact]
    public async Task RecordSuccessfulLoginAsync_SetsLastLogin()
    {
        await _store.UpsertAsync("admin1", "hash-1");

        var now = DateTimeOffset.UtcNow;
        await _store.RecordSuccessfulLoginAsync("admin1", now);

        var account = await _store.FindByUsernameAsync("admin1");
        Assert.NotNull(account!.LastLoginAtUtc);
    }

    [Fact]
    public async Task Schema_DoesNotContainLegacyLockoutColumns()
    {
        // ADR-0011 決定 8: FailedAttemptCount/LockoutUntilUtc は削除済み——判定はインメモリの
        // AdminAuthFailureDefense に一本化し、DB 上に古い判定用状態を残さない。
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(AdminAccounts);";
        await using var reader = await command.ExecuteReaderAsync();

        var columns = new List<string>();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        Assert.DoesNotContain("FailedAttemptCount", columns);
        Assert.DoesNotContain("LockoutUntilUtc", columns);
        Assert.Contains("Username", columns);
        Assert.Contains("PasswordHash", columns);
        Assert.Contains("LastLoginAtUtc", columns);
    }
}

/// <summary>
/// v1（PR #217 まで。<c>FailedAttemptCount</c>/<c>LockoutUntilUtc</c> 列を持つ形）から v2
/// （ADR-0011 決定 8）への削除マイグレーションの単体テスト。既存データベースファイルを
/// v1 形状で直接構築し、<see cref="SqliteAdminAccountStore.InitializeAsync"/> が正しく移行することを
/// 確認する（委任事項 2）。
/// </summary>
public sealed class SqliteAdminAccountStoreMigrationTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"yagura-admin-accounts-migration-{Guid.NewGuid():N}.db");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task InitializeAsync_ExistingV1Database_DropsLegacyColumns_PreservingData()
    {
        await CreateLegacyV1DatabaseAsync();

        var store = new SqliteAdminAccountStore(_databasePath);
        try
        {
            await store.InitializeAsync();

            // データ（ユーザー名・パスワードハッシュ）は保持されたまま列だけが削除されること。
            var account = await store.FindByUsernameAsync("admin1");
            Assert.NotNull(account);
            Assert.Equal("admin1", account!.Username);
            Assert.Equal("legacy-hash", account.PasswordHash);

            await AssertLegacyColumnsAbsentAsync();
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task InitializeAsync_ExistingV1Database_MigrationIsIdempotent()
    {
        await CreateLegacyV1DatabaseAsync();

        var store = new SqliteAdminAccountStore(_databasePath);
        try
        {
            await store.InitializeAsync();
            // 2 回目以降の初期化（例: サービス再起動）でも例外を投げず、同じ収束状態を保つ。
            await store.InitializeAsync();
            await store.InitializeAsync();

            var account = await store.FindByUsernameAsync("admin1");
            Assert.NotNull(account);

            await AssertLegacyColumnsAbsentAsync();
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    private async Task CreateLegacyV1DatabaseAsync()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using (var create = connection.CreateCommand())
        {
            // PR #217 までの v1 形状（バージョン管理表は存在しない）。
            create.CommandText =
                """
                CREATE TABLE AdminAccounts (
                    UsernameNormalized TEXT PRIMARY KEY,
                    Username TEXT NOT NULL,
                    PasswordHash TEXT NOT NULL,
                    FailedAttemptCount INTEGER NOT NULL DEFAULT 0,
                    LockoutUntilUtc TEXT NULL,
                    LastLoginAtUtc TEXT NULL
                );
                """;
            await create.ExecuteNonQueryAsync();
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO AdminAccounts (UsernameNormalized, Username, PasswordHash, FailedAttemptCount, LockoutUntilUtc, LastLoginAtUtc)
            VALUES ('admin1', 'admin1', 'legacy-hash', 3, '2026-07-11T00:00:00.0000000Z', NULL);
            """;
        await insert.ExecuteNonQueryAsync();
    }

    private async Task AssertLegacyColumnsAbsentAsync()
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(AdminAccounts);";
        await using var reader = await command.ExecuteReaderAsync();

        var columns = new List<string>();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        Assert.DoesNotContain("FailedAttemptCount", columns);
        Assert.DoesNotContain("LockoutUntilUtc", columns);
    }
}
