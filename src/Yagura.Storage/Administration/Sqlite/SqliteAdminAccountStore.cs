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

        // 原子的インクリメント（PR #217 レビュー指摘への対応）: read-modify-write（SELECT →
        // アプリ側計算 → UPDATE）は並行するログイン失敗（複数送信元からの同時試行）で
        // lost update を起こし、ロックアウト閾値到達が遅れる——ADR-0010 委任事項 4 の
        // 「分散送信元からの低速ブルートフォース対策」を弱める。単文の
        // UPDATE ... SET FailedAttemptCount = FailedAttemptCount + 1 ... RETURNING により
        // インクリメント・ロックアウト設定・結果取得を単一の原子的な文で行う
        // （SQLite の RETURNING 句は 3.35.0+。同梱の e_sqlite3 は 3.50 系——Directory.Packages.props）。
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE AdminAccounts SET
                FailedAttemptCount = FailedAttemptCount + 1,
                LockoutUntilUtc = CASE
                    WHEN FailedAttemptCount + 1 >= $threshold THEN $lockoutUntil
                    ELSE LockoutUntilUtc
                END
            WHERE UsernameNormalized = $normalized
            RETURNING FailedAttemptCount, LockoutUntilUtc;
            """;
        command.Parameters.AddWithValue("$threshold", lockoutThreshold);
        command.Parameters.AddWithValue("$lockoutUntil", atUtc.Add(lockoutDuration).UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$normalized", Normalize(username));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // アカウント不在（呼び出し元 AppAdminAuthenticationService は実在アカウントに
            // 対してのみ呼ぶ契約だが、防御的に空結果を返す）。
            return new AdminAccountLoginFailureResult(0, LockedOutNow: false, LockoutUntilUtc: null);
        }

        var failedCount = reader.GetInt32(0);
        DateTimeOffset? lockoutUntil = reader.IsDBNull(1)
            ? null
            : DateTimeOffset.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind);

        return new AdminAccountLoginFailureResult(
            failedCount,
            LockedOutNow: failedCount >= lockoutThreshold,
            LockoutUntilUtc: lockoutUntil);
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
