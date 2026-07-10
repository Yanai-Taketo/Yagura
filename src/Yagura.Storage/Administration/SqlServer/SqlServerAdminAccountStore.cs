using Microsoft.Data.SqlClient;

namespace Yagura.Storage.Administration.SqlServer;

/// <summary>
/// <see cref="IAdminAccountStore"/> の SQL Server 実装（ADR-0010 決定 3。本番昇格後の provider）。
/// </summary>
/// <remarks>
/// <see cref="Yagura.Storage.SqlServer.SqlServerLogStore"/> と同じ「<c>IF OBJECT_ID(...) IS NULL</c>
/// による冪等スキーマ作成」の慣用を踏襲する。ユーザー名の大小文字を区別しない照合は
/// <c>UsernameNormalized</c> 列（アプリ側で小文字正規化）に主キーを置くことで、DB の照合順序
/// 設定に依存せず一貫させる（<see cref="Sqlite.SqliteAdminAccountStore"/> と同じ設計判断）。
/// </remarks>
public sealed class SqlServerAdminAccountStore : IAdminAccountStore
{
    private readonly string _connectionString;

    public SqlServerAdminAccountStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connectionString = connectionString;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            IF OBJECT_ID(N'dbo.AdminAccounts', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.AdminAccounts (
                    UsernameNormalized NVARCHAR(256) NOT NULL PRIMARY KEY,
                    Username NVARCHAR(256) NOT NULL,
                    PasswordHash NVARCHAR(512) NOT NULL,
                    FailedAttemptCount INT NOT NULL CONSTRAINT DF_AdminAccounts_FailedAttemptCount DEFAULT (0),
                    LockoutUntilUtc DATETIME2(7) NULL,
                    LastLoginAtUtc DATETIME2(7) NULL
                );
            END
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasAnyAccountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dbo.AdminAccounts;";
        var count = (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return count > 0;
    }

    public async Task<AdminAccountRecord?> GetSoleAccountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT TOP (1) Username, PasswordHash, FailedAttemptCount, LockoutUntilUtc, LastLoginAtUtc " +
            "FROM dbo.AdminAccounts;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new AdminAccountRecord(
            Username: reader.GetString(0),
            PasswordHash: reader.GetString(1),
            FailedAttemptCount: reader.GetInt32(2),
            LockoutUntilUtc: reader.IsDBNull(3) ? null : new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero),
            LastLoginAtUtc: reader.IsDBNull(4) ? null : new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero));
    }

    public async Task<AdminAccountRecord?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Username, PasswordHash, FailedAttemptCount, LockoutUntilUtc, LastLoginAtUtc " +
            "FROM dbo.AdminAccounts WHERE UsernameNormalized = @normalized;";
        command.Parameters.AddWithValue("@normalized", Normalize(username));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new AdminAccountRecord(
            Username: reader.GetString(0),
            PasswordHash: reader.GetString(1),
            FailedAttemptCount: reader.GetInt32(2),
            LockoutUntilUtc: reader.IsDBNull(3) ? null : new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero),
            LastLoginAtUtc: reader.IsDBNull(4) ? null : new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero));
    }

    public async Task UpsertAsync(string username, string passwordHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            MERGE dbo.AdminAccounts AS target
            USING (SELECT @normalized AS UsernameNormalized) AS source
            ON target.UsernameNormalized = source.UsernameNormalized
            WHEN MATCHED THEN UPDATE SET
                Username = @username,
                PasswordHash = @hash,
                FailedAttemptCount = 0,
                LockoutUntilUtc = NULL
            WHEN NOT MATCHED THEN INSERT (UsernameNormalized, Username, PasswordHash, FailedAttemptCount, LockoutUntilUtc, LastLoginAtUtc)
                VALUES (@normalized, @username, @hash, 0, NULL, NULL);
            """;
        command.Parameters.AddWithValue("@normalized", Normalize(username));
        command.Parameters.AddWithValue("@username", username);
        command.Parameters.AddWithValue("@hash", passwordHash);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordSuccessfulLoginAsync(string username, DateTimeOffset atUtc, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE dbo.AdminAccounts SET FailedAttemptCount = 0, LockoutUntilUtc = NULL, LastLoginAtUtc = @at " +
            "WHERE UsernameNormalized = @normalized;";
        command.Parameters.AddWithValue("@at", atUtc.UtcDateTime);
        command.Parameters.AddWithValue("@normalized", Normalize(username));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AdminAccountLoginFailureResult> RecordFailedLoginAsync(
        string username,
        DateTimeOffset atUtc,
        int lockoutThreshold,
        TimeSpan lockoutDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // 原子的インクリメント（PR #217 レビュー指摘への対応。SQLite 実装と同じ設計判断）:
        // 単文の UPDATE ... SET FailedAttemptCount = FailedAttemptCount + 1 ... OUTPUT により
        // インクリメント・ロックアウト設定・結果取得を単一の原子的な文で行う——並行する
        // ログイン失敗での lost update（ロックアウト閾値到達の遅延）を構造的に防ぐ。
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE dbo.AdminAccounts SET
                FailedAttemptCount = FailedAttemptCount + 1,
                LockoutUntilUtc = CASE
                    WHEN FailedAttemptCount + 1 >= @threshold THEN @lockoutUntil
                    ELSE LockoutUntilUtc
                END
            OUTPUT inserted.FailedAttemptCount, inserted.LockoutUntilUtc
            WHERE UsernameNormalized = @normalized;
            """;
        command.Parameters.AddWithValue("@threshold", lockoutThreshold);
        command.Parameters.AddWithValue("@lockoutUntil", atUtc.Add(lockoutDuration).UtcDateTime);
        command.Parameters.AddWithValue("@normalized", Normalize(username));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // アカウント不在（呼び出し元は実在アカウントに対してのみ呼ぶ契約だが、防御的に空結果を返す）。
            return new AdminAccountLoginFailureResult(0, LockedOutNow: false, LockoutUntilUtc: null);
        }

        var failedCount = reader.GetInt32(0);
        DateTimeOffset? lockoutUntil = reader.IsDBNull(1)
            ? null
            : new DateTimeOffset(reader.GetDateTime(1), TimeSpan.Zero);

        return new AdminAccountLoginFailureResult(
            failedCount,
            LockedOutNow: failedCount >= lockoutThreshold,
            LockoutUntilUtc: lockoutUntil);
    }

    private static string Normalize(string username) => username.Trim().ToLowerInvariant();
}
