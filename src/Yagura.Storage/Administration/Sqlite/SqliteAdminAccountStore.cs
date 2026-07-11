using System.Globalization;
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
    /// <summary>
    /// 現行のスキーマバージョン（ADR-0011 決定 8）。v2: <c>FailedAttemptCount</c>/
    /// <c>LockoutUntilUtc</c> 列を削除した（ハードロックアウトからバックオフ + レート制限への
    /// supersession。失敗試行の判定はインメモリの <c>AdminAuthFailureDefense</c> に一本化）。
    /// v1（黙示。PR #217 まで）は両列を持つ形。<see cref="Yagura.Storage.Sqlite.SqliteLogStore.CurrentSchemaVersion"/>
    /// と同じ「バージョン表 + 移行ステップ」の作法を、<c>AdminAccounts</c> 専用の版管理テーブル
    /// （<c>AdminAccountsSchemaVersion</c>）で独立に持つ——<c>LogRecords</c> の
    /// <c>SchemaVersion</c> テーブルとは意味の異なる版数のため、同一テーブルへ相乗りしない。
    /// </summary>
    internal const int CurrentSchemaVersion = 2;

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

    /// <summary>
    /// スキーマを初期化する（冪等）。新規データベースは v2 形状（<c>FailedAttemptCount</c>/
    /// <c>LockoutUntilUtc</c> なし）で直接作成する。PR #217 以前の v1 形状（両列を持つ）から
    /// アップグレードする既存データベースには削除マイグレーションを適用する（ADR-0011 決定 8。
    /// 委任事項 2）——<c>AdminAccountsSchemaVersion</c> 表が存在しない場合でも、
    /// <c>AdminAccounts</c> 表が既に存在すれば「バージョン管理導入前の v1 データベース」と
    /// 判定し、同じ移行を適用する（バージョン表の導入自体が本 PR で初めてのため、
    /// 「バージョン表なし」は「新規」と「v1 からの無版数アップグレード」の両方を意味し得る——
    /// 両者を <c>AdminAccounts</c> 表の実在有無で区別する）。
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var adminAccountsExistedBefore = await TableExistsAsync(connection, "AdminAccounts", cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            // UsernameNormalized（小文字・不変カルチャ）を主キーにすることで、SQLite の既定
            // 照合順序（バイト単位比較）に依存せず、確実に大文字小文字を区別しない照合を実現する
            // （COLLATE NOCASE は ASCII 範囲限定のため、より確実な正規化列を採る）。
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS AdminAccounts (
                    UsernameNormalized TEXT PRIMARY KEY,
                    Username TEXT NOT NULL,
                    PasswordHash TEXT NOT NULL,
                    LastLoginAtUtc TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS AdminAccountsSchemaVersion (
                    Id INTEGER PRIMARY KEY CHECK (Id = 1),
                    Version INTEGER NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var recordedVersion = await ReadSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);

        if (recordedVersion is null && adminAccountsExistedBefore)
        {
            // バージョン表導入前の既存データベース（v1 形状の可能性がある）。
            await DropLegacyLockoutColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        else if (recordedVersion is { } version && version < CurrentSchemaVersion)
        {
            await DropLegacyLockoutColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        // recordedVersion == CurrentSchemaVersion の場合、または新規作成（recordedVersion is null
        // かつ adminAccountsExistedBefore が false）の場合は追加の移行不要——上の CREATE TABLE が
        // 既に v2 形状で作成済み。いずれの分岐でもバージョンを記録して収束させる（冪等性）。
        await RecordSchemaVersionAsync(connection, CurrentSchemaVersion, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        var count = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return count > 0;
    }

    private static async Task<int?> ReadSchemaVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version FROM AdminAccountsSchemaVersion WHERE Id = 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task RecordSchemaVersionAsync(SqliteConnection connection, int version, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO AdminAccountsSchemaVersion (Id, Version) VALUES (1, $version)
            ON CONFLICT(Id) DO UPDATE SET Version = excluded.Version;
            """;
        command.Parameters.AddWithValue("$version", version);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// v1 → v2 移行（ADR-0011 決定 8）: <c>FailedAttemptCount</c>/<c>LockoutUntilUtc</c> 列を
    /// 削除する。SQLite の <c>ALTER TABLE ... DROP COLUMN</c>（3.35.0+。同梱の e_sqlite3 は
    /// 3.50 系）を使う。列が既に存在しない場合（新規 v2 データベースに誤って呼ばれた場合の防御、
    /// または一度適用済みの再実行）は <c>PRAGMA table_info</c> で確認してスキップし、冪等性を保つ。
    /// </summary>
    private static async Task DropLegacyLockoutColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existingColumns = await GetColumnNamesAsync(connection, "AdminAccounts", cancellationToken).ConfigureAwait(false);

        foreach (var columnName in new[] { "FailedAttemptCount", "LockoutUntilUtc" })
        {
            if (!existingColumns.Contains(columnName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER TABLE AdminAccounts DROP COLUMN {columnName};";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<List<string>> GetColumnNamesAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // PRAGMA table_info の列順: cid, name, type, notnull, dflt_value, pk。
            columns.Add(reader.GetString(1));
        }

        return columns;
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
        command.CommandText = "SELECT Username, PasswordHash, LastLoginAtUtc FROM AdminAccounts LIMIT 1;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadRecord(reader);
    }

    public async Task<AdminAccountRecord?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Username, PasswordHash, LastLoginAtUtc FROM AdminAccounts WHERE UsernameNormalized = $normalized;";
        command.Parameters.AddWithValue("$normalized", Normalize(username));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadRecord(reader);
    }

    private static AdminAccountRecord ReadRecord(SqliteDataReader reader) => new(
        Username: reader.GetString(0),
        PasswordHash: reader.GetString(1),
        LastLoginAtUtc: reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2), null, DateTimeStyles.RoundtripKind));

    public async Task UpsertAsync(string username, string passwordHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO AdminAccounts (UsernameNormalized, Username, PasswordHash, LastLoginAtUtc)
            VALUES ($normalized, $username, $hash, NULL)
            ON CONFLICT(UsernameNormalized) DO UPDATE SET
                Username = excluded.Username,
                PasswordHash = excluded.PasswordHash;
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
        command.CommandText = "UPDATE AdminAccounts SET LastLoginAtUtc = $at WHERE UsernameNormalized = $normalized;";
        command.Parameters.AddWithValue("$at", atUtc.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$normalized", Normalize(username));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
