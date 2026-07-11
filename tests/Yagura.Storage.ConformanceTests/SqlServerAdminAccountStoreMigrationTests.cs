using Microsoft.Data.SqlClient;
using Yagura.Storage.Administration.SqlServer;

namespace Yagura.Storage.ConformanceTests;

/// <summary>
/// SQL Server provider の <c>AdminAccounts</c> スキーマ v1 -&gt; v2 移行の検証
/// （ADR-0011 決定 8。委任事項 2）: <c>FailedAttemptCount</c>/<c>LockoutUntilUtc</c> 列
/// （既定制約込み）を削除するマイグレーションが、既存データを保全したまま冪等に適用されること。
/// </summary>
/// <remarks>
/// <b>スキップ戦略</b>: <see cref="SqlServerSchemaMigrationTests"/> と同一（ローカルに SQL Server /
/// LocalDB が無ければスキップ。CI ではスキップせず fail——偽装 green を防ぐ）。
/// </remarks>
public sealed class SqlServerAdminAccountStoreMigrationTests : IAsyncLifetime
{
    private readonly List<string> _databaseNames = [];

    /// <summary>v1 形状（PR #217 まで。既定制約付き）の DDL の写し。</summary>
    private const string V1SchemaDdl =
        """
        CREATE TABLE dbo.AdminAccounts (
            UsernameNormalized NVARCHAR(256) NOT NULL PRIMARY KEY,
            Username NVARCHAR(256) NOT NULL,
            PasswordHash NVARCHAR(512) NOT NULL,
            FailedAttemptCount INT NOT NULL CONSTRAINT DF_AdminAccounts_FailedAttemptCount DEFAULT (0),
            LockoutUntilUtc DATETIME2(7) NULL,
            LastLoginAtUtc DATETIME2(7) NULL
        );

        INSERT INTO dbo.AdminAccounts (UsernameNormalized, Username, PasswordHash, FailedAttemptCount, LockoutUntilUtc, LastLoginAtUtc)
        VALUES (N'admin1', N'admin1', N'legacy-hash', 3, '2026-07-11T00:00:00', NULL);
        """;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var databaseName in _databaseNames)
        {
            await using var masterConnection = new SqlConnection(SqlServerTestConnection.GetMasterConnectionString());
            await masterConnection.OpenAsync();

            await using var dropCommand = masterConnection.CreateCommand();
            dropCommand.CommandText =
                $"""
                IF DB_ID(N'{databaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{databaseName}];
                END;
                """;
            await dropCommand.ExecuteNonQueryAsync();
        }
    }

    [SkippableFact]
    public async Task InitializeAsync_OnV1Database_DropsLegacyColumnsAndConstraint_PreservingData()
    {
        var connectionString = await CreateV1DatabaseAsync();

        var store = new SqlServerAdminAccountStore(connectionString);
        await store.InitializeAsync();

        // (1) 列が削除されている。
        foreach (var column in new[] { "FailedAttemptCount", "LockoutUntilUtc" })
        {
            var length = await ExecuteScalarAsync(
                connectionString, $"SELECT COL_LENGTH('dbo.AdminAccounts', '{column}');");
            Assert.True(length is null or DBNull, $"列 {column} が削除されていない。");
        }

        // (2) 既定制約も一緒に削除されている（残存すると将来の同名列追加等で衝突し得る）。
        var constraintCount = await ExecuteScalarAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.default_constraints WHERE name = N'DF_AdminAccounts_FailedAttemptCount';");
        Assert.Equal(0, Convert.ToInt32(constraintCount));

        // (3) 既存データ（ユーザー名・パスワードハッシュ）は保全されている。
        var account = await store.FindByUsernameAsync("admin1");
        Assert.NotNull(account);
        Assert.Equal("admin1", account!.Username);
        Assert.Equal("legacy-hash", account.PasswordHash);

        // (4) 版が現行（v2）へ更新されている。
        var version = await ExecuteScalarAsync(
            connectionString, "SELECT Version FROM dbo.AdminAccountsSchemaVersion WHERE Id = 1;");
        Assert.Equal(SqlServerAdminAccountStore.CurrentSchemaVersion, Convert.ToInt32(version));
    }

    [SkippableFact]
    public async Task InitializeAsync_OnV1Database_MigrationIsIdempotent()
    {
        var connectionString = await CreateV1DatabaseAsync();

        var store = new SqlServerAdminAccountStore(connectionString);
        await store.InitializeAsync();

        var exception = await Record.ExceptionAsync(() => store.InitializeAsync());
        Assert.Null(exception);

        exception = await Record.ExceptionAsync(() => store.InitializeAsync());
        Assert.Null(exception);

        var account = await store.FindByUsernameAsync("admin1");
        Assert.NotNull(account);

        var length = await ExecuteScalarAsync(connectionString, "SELECT COL_LENGTH('dbo.AdminAccounts', 'FailedAttemptCount');");
        Assert.True(length is null or DBNull);
    }

    [SkippableFact]
    public async Task InitializeAsync_OnNewDatabase_CreatesV2ShapeDirectly_WithoutLegacyColumns()
    {
        var connectionString = await CreateEmptyDatabaseAsync();

        var store = new SqlServerAdminAccountStore(connectionString);
        await store.InitializeAsync();

        var length = await ExecuteScalarAsync(connectionString, "SELECT COL_LENGTH('dbo.AdminAccounts', 'FailedAttemptCount');");
        Assert.True(length is null or DBNull);

        var version = await ExecuteScalarAsync(
            connectionString, "SELECT Version FROM dbo.AdminAccountsSchemaVersion WHERE Id = 1;");
        Assert.Equal(SqlServerAdminAccountStore.CurrentSchemaVersion, Convert.ToInt32(version));
    }

    private async Task<string> CreateV1DatabaseAsync()
    {
        var connectionString = await CreateEmptyDatabaseAsync();
        await ExecuteAsync(connectionString, V1SchemaDdl);
        return connectionString;
    }

    private async Task<string> CreateEmptyDatabaseAsync()
    {
        var isAvailable = SqlServerTestConnection.IsAvailable();

        if (!isAvailable && !IsRunningInCi())
        {
            Skip.If(
                true,
                $"SQL Server に接続できないためスキップします（接続先: {SqlServerTestConnection.DescribeTarget()}）。" +
                $"環境変数 {SqlServerTestConnection.ConnectionStringEnvironmentVariable} に接続文字列を設定するか、" +
                "既定の LocalDB インスタンスを利用可能にしてください。");
        }

        var databaseName = $"yagura_adminaccounts_migration_{Guid.NewGuid():N}";
        _databaseNames.Add(databaseName);

        await using var masterConnection = new SqlConnection(SqlServerTestConnection.GetMasterConnectionString());
        await masterConnection.OpenAsync();

        await using var createCommand = masterConnection.CreateCommand();
        createCommand.CommandText = $"CREATE DATABASE [{databaseName}];";
        await createCommand.ExecuteNonQueryAsync();

        return SqlServerTestConnection.BuildConnectionString(databaseName);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ExecuteScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static bool IsRunningInCi() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));
}
