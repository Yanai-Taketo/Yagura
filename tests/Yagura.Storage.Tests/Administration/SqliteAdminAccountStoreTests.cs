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
    /// <summary>作成・変更時刻（ADR-0021 スキーマ v3）を決定的に検証するための固定時刻。</summary>
    private static readonly DateTimeOffset TestNow = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

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
        await _store.UpsertAsync("Admin1", "hash-1", TestNow);

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
        await _store.UpsertAsync("admin1", "hash-old", TestNow);
        await _store.UpsertAsync("admin1", "hash-new", TestNow.AddMinutes(5));

        var reset = await _store.FindByUsernameAsync("admin1");
        Assert.Equal("hash-new", reset!.PasswordHash);
    }

    [Fact]
    public async Task UpsertAsync_RecordsCreatedAndUpdatedTimestamps()
    {
        // ADR-0021 決定 1 の切替時点検が使う値: 新規作成で両方、更新で UpdatedAtUtc のみが動く
        // （作成時刻は保存され続ける——「いつ作られ、いつ最後に変えられたか」を提示するため）。
        await _store.UpsertAsync("admin1", "hash-old", TestNow);

        var created = await _store.FindByUsernameAsync("admin1");
        Assert.Equal(TestNow, created!.CreatedAtUtc);
        Assert.Equal(TestNow, created.UpdatedAtUtc);

        await _store.UpsertAsync("admin1", "hash-new", TestNow.AddMinutes(5));

        var updated = await _store.FindByUsernameAsync("admin1");
        Assert.Equal(TestNow, updated!.CreatedAtUtc);
        Assert.Equal(TestNow.AddMinutes(5), updated.UpdatedAtUtc);
    }

    [Fact]
    public async Task FindByUsernameAsync_UnknownUsername_ReturnsNull()
    {
        Assert.Null(await _store.FindByUsernameAsync("nobody"));
    }

    [Fact]
    public async Task RecordSuccessfulLoginAsync_SetsLastLogin()
    {
        await _store.UpsertAsync("admin1", "hash-1", TestNow);

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
        // ADR-0021 スキーマ v3: 新規データベースの CREATE TABLE も最新形状であること
        // （入れ忘れると新規 DB だけ列が無いまま版 3 が記録され、移行もスキップされる）。
        Assert.Contains("CreatedAtUtc", columns);
        Assert.Contains("UpdatedAtUtc", columns);
    }
}

/// <summary>
/// 既存データベースのスキーマ移行の単体テスト。v1（PR #217 まで。
/// <c>FailedAttemptCount</c>/<c>LockoutUntilUtc</c> を持つ形）→ v2（ADR-0011 決定 8 の列削除）
/// → v3（ADR-0021 決定 1 の時刻列追加）を、それぞれの形状のデータベースファイルを直接構築して
/// <see cref="SqliteAdminAccountStore.InitializeAsync"/> が正しく移行することを確認する。
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

    [Fact]
    public async Task InitializeAsync_ExistingV1Database_AlsoAppliesV3TimestampColumns()
    {
        // 版管理表を持たない v1 から一気に v3 まで上げる経路（移行ステップが昇順に 2 段
        // 適用されること——ADR-0021 で ApplyMigrations 形式へ再構成した本体の回帰）。
        await CreateLegacyV1DatabaseAsync();

        var store = new SqliteAdminAccountStore(_databasePath);
        try
        {
            await store.InitializeAsync();

            await AssertLegacyColumnsAbsentAsync();
            var columns = await ReadColumnNamesAsync();
            Assert.Contains("CreatedAtUtc", columns);
            Assert.Contains("UpdatedAtUtc", columns);

            // 既存行はバックフィルしない（移行時刻で埋めると「移行日に作られた」という嘘になる）。
            var account = await store.FindByUsernameAsync("admin1");
            Assert.NotNull(account);
            Assert.Null(account!.CreatedAtUtc);
            Assert.Null(account.UpdatedAtUtc);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task InitializeAsync_ExistingV2Database_AddsTimestampColumns_PreservingData()
    {
        await CreateV2DatabaseAsync();

        var store = new SqliteAdminAccountStore(_databasePath);
        try
        {
            await store.InitializeAsync();

            var columns = await ReadColumnNamesAsync();
            Assert.Contains("CreatedAtUtc", columns);
            Assert.Contains("UpdatedAtUtc", columns);

            var account = await store.FindByUsernameAsync("admin1");
            Assert.NotNull(account);
            Assert.Equal("v2-hash", account!.PasswordHash);
            Assert.Null(account.CreatedAtUtc);
            Assert.Null(account.UpdatedAtUtc);

            // 移行後の更新では UpdatedAtUtc が入る（CreatedAtUtc は不明のまま——
            // 既存行の作成時刻は復元できないため）。
            var now = new DateTimeOffset(2026, 7, 26, 15, 0, 0, TimeSpan.Zero);
            await store.UpsertAsync("admin1", "v3-hash", now);

            var updated = await store.FindByUsernameAsync("admin1");
            Assert.Equal(now, updated!.UpdatedAtUtc);
            Assert.Null(updated.CreatedAtUtc);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task InitializeAsync_ExistingV2Database_MigrationIsIdempotent()
    {
        await CreateV2DatabaseAsync();

        var store = new SqliteAdminAccountStore(_databasePath);
        try
        {
            await store.InitializeAsync();
            await store.InitializeAsync();
            await store.InitializeAsync();

            var columns = await ReadColumnNamesAsync();
            Assert.Equal(1, columns.Count(c => string.Equals(c, "CreatedAtUtc", StringComparison.Ordinal)));
            Assert.NotNull(await store.FindByUsernameAsync("admin1"));
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    private async Task CreateV2DatabaseAsync()
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
            // ADR-0011 決定 8 適用後・ADR-0021 適用前の v2 形状（版管理表に 2 を記録済み）。
            create.CommandText =
                """
                CREATE TABLE AdminAccounts (
                    UsernameNormalized TEXT PRIMARY KEY,
                    Username TEXT NOT NULL,
                    PasswordHash TEXT NOT NULL,
                    LastLoginAtUtc TEXT NULL
                );

                CREATE TABLE AdminAccountsSchemaVersion (
                    Id INTEGER PRIMARY KEY CHECK (Id = 1),
                    Version INTEGER NOT NULL
                );

                INSERT INTO AdminAccountsSchemaVersion (Id, Version) VALUES (1, 2);

                INSERT INTO AdminAccounts (UsernameNormalized, Username, PasswordHash, LastLoginAtUtc)
                VALUES ('admin1', 'admin1', 'v2-hash', NULL);
                """;
            await create.ExecuteNonQueryAsync();
        }
    }

    private async Task<List<string>> ReadColumnNamesAsync()
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

        return columns;
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
