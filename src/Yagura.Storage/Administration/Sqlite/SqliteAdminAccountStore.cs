using Microsoft.Data.Sqlite;

namespace Yagura.Storage.Administration.Sqlite;

/// <summary>
/// <see cref="IAdminAccountStore"/> の SQLite 実装（ADR-0010 決定 3。既定 provider）。
/// </summary>
/// <remarks>
/// <see cref="Yagura.Storage.Sqlite.SqliteLogStore"/> と同じ接続の開閉パターン（操作ごとに
/// 開閉。Microsoft.Data.Sqlite の既定の接続プーリングにより実コストは小さい）を踏襲する。
/// スキーマは独立の <c>AdminAccounts</c> テーブルであり、<c>LogRecords</c>/<c>SchemaVersion</c>
/// とは別管理（<see cref="IAdminAccountStore"/> の remarks 参照）。
/// </remarks>
public sealed class SqliteAdminAccountStore : IAdminAccountStore, IAsyncDisposable
{
    private readonly string _connectionString;

    public SqliteAdminAccountStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        // UsernameNormalized（小文字・不変カルチャ）を主キーにすることで、SQLite の既定
        // 照合順序（バイト単位比較）に依存せず、確実に大文字小文字を区別しない照合を実現する
        // （COLLATE NOCASE は ASCII 範囲限定のため、より確実な正規化列を採る）。
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS AdminAccounts (
                UsernameNormalized TEXT PRIMARY KEY,
                Username TEXT NOT NULL,
                PasswordHash TEXT NOT NULL,
                FailedAttemptCount INTEGER NOT NULL DEFAULT 0,
                LockoutUntilUtc TEXT NULL,
                LastLoginAtUtc TEXT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasAnyAccountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM AdminAccounts;";
        var count = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return count > 0;
    }

    public async Task<AdminAccountRecord?> GetSoleAccountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Username, PasswordHash, FailedAttemptCount, LockoutUntilUtc, LastLoginAtUtc " +
            "FROM AdminAccounts LIMIT 1;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new AdminAccountRecord(
            Username: reader.GetString(0),
            PasswordHash: reader.GetString(1),
            FailedAttemptCount: reader.GetInt32(2),
            LockoutUntilUtc: reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
            LastLoginAtUtc: reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    public async Task<AdminAccountRecord?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Username, PasswordHash, FailedAttemptCount, LockoutUntilUtc, LastLoginAtUtc " +
            "FROM AdminAccounts WHERE UsernameNormalized = $normalized;";
        command.Parameters.AddWithValue("$normalized", Normalize(username));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new AdminAccountRecord(
            Username: reader.GetString(0),
            PasswordHash: reader.GetString(1),
            FailedAttemptCount: reader.GetInt32(2),
            LockoutUntilUtc: reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
            LastLoginAtUtc: reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    public async Task UpsertAsync(string username, string passwordHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO AdminAccounts (UsernameNormalized, Username, PasswordHash, FailedAttemptCount, LockoutUntilUtc, LastLoginAtUtc)
            VALUES ($normalized, $username, $hash, 0, NULL, NULL)
            ON CONFLICT(UsernameNormalized) DO UPDATE SET
                Username = excluded.Username,
                PasswordHash = excluded.PasswordHash,
                FailedAttemptCount = 0,
                LockoutUntilUtc = NULL;
            """;
        command.Parameters.AddWithValue("$normalized", Normalize(username));
        command.Parameters.AddWithValue("$username", username);
        command.Parameters.AddWithValue("$hash", passwordHash);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordSuccessfulLoginAsync(string username, DateTimeOffset atUtc, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE AdminAccounts SET FailedAttemptCount = 0, LockoutUntilUtc = NULL, LastLoginAtUtc = $at " +
            "WHERE UsernameNormalized = $normalized;";
        command.Parameters.AddWithValue("$at", atUtc.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$normalized", Normalize(username));
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

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        int failedCount;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT FailedAttemptCount FROM AdminAccounts WHERE UsernameNormalized = $normalized;";
            select.Parameters.AddWithValue("$normalized", Normalize(username));
            var current = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            failedCount = current is null ? 0 : Convert.ToInt32(current) + 1;
        }

        var lockedOutNow = failedCount >= lockoutThreshold;
        DateTimeOffset? lockoutUntil = lockedOutNow ? atUtc.Add(lockoutDuration) : null;

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE AdminAccounts SET FailedAttemptCount = $count, LockoutUntilUtc = $lockoutUntil " +
                "WHERE UsernameNormalized = $normalized;";
            update.Parameters.AddWithValue("$count", failedCount);
            update.Parameters.AddWithValue("$lockoutUntil", (object?)lockoutUntil?.UtcDateTime.ToString("O") ?? DBNull.Value);
            update.Parameters.AddWithValue("$normalized", Normalize(username));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new AdminAccountLoginFailureResult(failedCount, lockedOutNow, lockoutUntil);
    }

    private static string Normalize(string username) => username.Trim().ToLowerInvariant();

    /// <inheritdoc/>
    /// <remarks>
    /// <see cref="Yagura.Storage.Sqlite.SqliteLogStore.DisposeAsync"/> と同じ理由:
    /// Microsoft.Data.Sqlite は既定でネイティブ接続をプールするため、明示的にプールを破棄し、
    /// 呼び出し側がすぐにファイル削除・移動を行うケース（テスト等）を安全にする。
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);

        return ValueTask.CompletedTask;
    }
}
