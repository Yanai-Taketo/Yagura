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
    /// 現行のスキーマバージョン。
    /// v1（黙示。PR #217 まで）は <c>FailedAttemptCount</c>/<c>LockoutUntilUtc</c> を持つ形。
    /// v2（ADR-0011 決定 8）: 両列を削除した（ハードロックアウトからバックオフ + レート制限への
    /// supersession。失敗試行の判定はインメモリの <c>AdminAuthFailureDefense</c> に一本化）。
    /// v3（ADR-0021 決定 1）: <c>CreatedAtUtc</c>/<c>UpdatedAtUtc</c> を追加した（アップロード機能の
    /// 切替時点検が「既存アカウントの有無・最終変更時刻」を提示するため。既存行は NULL = 不明）。
    /// <see cref="Yagura.Storage.Sqlite.SqliteLogStore.CurrentSchemaVersion"/>
    /// と同じ「バージョン表 + 移行ステップ」の作法を、<c>AdminAccounts</c> 専用の版管理テーブル
    /// （<c>AdminAccountsSchemaVersion</c>）で独立に持つ——<c>LogRecords</c> の
    /// <c>SchemaVersion</c> テーブルとは意味の異なる版数のため、同一テーブルへ相乗りしない。
    /// <b>SQL Server 実装（<c>SqlServerAdminAccountStore.CurrentSchemaVersion</c>）と同じ値を保つこと</b>
    /// ——版数の意味は provider 間で共通であり、片方だけ上げると片側だけ移行が走る。
    /// </summary>
    internal const int CurrentSchemaVersion = 3;

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
    /// スキーマを初期化する（冪等）。新規データベースは最新形状（v3）で直接作成する。既存
    /// データベースには記録済み版から現行版までの移行ステップを順に適用する
    /// （<see cref="ApplyMigrationsAsync"/>）——PR #217 以前の v1 形状（ロックアウト 2 列を持つ）から
    /// 上げる場合は v1 → v2（列削除。ADR-0011 決定 8）と v2 → v3（時刻列追加。ADR-0021 決定 1）が
    /// 順に走る。<c>AdminAccountsSchemaVersion</c> 表が存在しない場合でも、<c>AdminAccounts</c> 表が
    /// 既に存在すれば「バージョン管理導入前の v1 データベース」と判定して同じ移行を適用する
    /// （バージョン表の導入は PR #217 が初めてのため、「バージョン表なし」は「新規」と
    /// 「v1 からの無版数アップグレード」の両方を意味し得る——両者を <c>AdminAccounts</c> 表の
    /// 実在有無で区別する）。
    /// </summary>
    /// <remarks>
    /// <b>移行はトランザクションで囲まない</b>（<c>SqliteLogStore</c> との差異）: 各ステップは
    /// 実在確認（<c>PRAGMA table_info</c>）で冪等化してあり、途中失敗（「列は足したが版は据え置き」）は
    /// 再実行で収束する。版の記録は全ステップ成功後に 1 回だけ行う——先に版を上げてから失敗すると
    /// 未適用の移行が永久にスキップされるため。
    /// </remarks>
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
            // 本 DDL は常に最新形状（v3）でなければならない——新列を入れ忘れると、新規データベース
            // だけ列が無いまま版 3 が記録され、移行ステップもスキップされて永久に修復されない。
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS AdminAccounts (
                    UsernameNormalized TEXT PRIMARY KEY,
                    Username TEXT NOT NULL,
                    PasswordHash TEXT NOT NULL,
                    LastLoginAtUtc TEXT NULL,
                    CreatedAtUtc TEXT NULL,
                    UpdatedAtUtc TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS AdminAccountsSchemaVersion (
                    Id INTEGER PRIMARY KEY CHECK (Id = 1),
                    Version INTEGER NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var recordedVersion = await ReadSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);

        // 版表なし + 表が既存 = バージョン管理導入前（v1 相当）。版表なし + 表も新規 = 上の
        // CREATE TABLE が最新形状で作ったため移行不要（fromVersion = CurrentSchemaVersion 扱い）。
        var fromVersion = recordedVersion ?? (adminAccountsExistedBefore ? 1 : CurrentSchemaVersion);

        await ApplyMigrationsAsync(connection, fromVersion, cancellationToken).ConfigureAwait(false);

        // いずれの経路でもバージョンを記録して収束させる（冪等性）。
        await RecordSchemaVersionAsync(connection, CurrentSchemaVersion, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 記録済み版から現行版までの移行ステップを昇順に適用する。各ステップは実在確認により
    /// 冪等であり、想定外の版（新しすぎる値）では何もしない。
    /// </summary>
    private static async Task ApplyMigrationsAsync(SqliteConnection connection, int fromVersion, CancellationToken cancellationToken)
    {
        if (fromVersion < 2)
        {
            await DropLegacyLockoutColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        if (fromVersion < 3)
        {
            await AddAccountTimestampColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        }
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

    /// <summary>
    /// v2 → v3 移行（ADR-0021 決定 1）: <c>CreatedAtUtc</c>/<c>UpdatedAtUtc</c> 列を追加する。
    /// <b>既存行はバックフィルしない</b>——v3 より前に作成・更新されたアカウントの実際の時刻は
    /// どこにも残っておらず、移行時刻で埋めると「移行した日に作られた/変更された」という嘘になる。
    /// NULL のまま残し、UI 側が「不明」と表示する（<see cref="IAdminAccountStore"/> の remarks）。
    /// 列が既に存在する場合（再実行）は <c>PRAGMA table_info</c> で確認してスキップし、冪等性を保つ。
    /// </summary>
    private static async Task AddAccountTimestampColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existingColumns = await GetColumnNamesAsync(connection, "AdminAccounts", cancellationToken).ConfigureAwait(false);

        foreach (var columnName in new[] { "CreatedAtUtc", "UpdatedAtUtc" })
        {
            if (existingColumns.Contains(columnName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER TABLE AdminAccounts ADD COLUMN {columnName} TEXT NULL;";
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
        command.CommandText = "SELECT Username, PasswordHash, LastLoginAtUtc, CreatedAtUtc, UpdatedAtUtc FROM AdminAccounts LIMIT 1;";

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
            "SELECT Username, PasswordHash, LastLoginAtUtc, CreatedAtUtc, UpdatedAtUtc FROM AdminAccounts WHERE UsernameNormalized = $normalized;";
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
        LastLoginAtUtc: ReadTimestamp(reader, 2),
        CreatedAtUtc: ReadTimestamp(reader, 3),
        UpdatedAtUtc: ReadTimestamp(reader, 4));

    private static DateTimeOffset? ReadTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), null, DateTimeStyles.RoundtripKind);

    public async Task UpsertAsync(
        string username, string passwordHash, DateTimeOffset atUtc, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        // 新規は作成時刻・更新時刻の両方、既存の更新は更新時刻のみ（作成時刻は保存する——
        // 切替時点検が「いつ作られ、いつ最後に変えられたか」を提示するため）。
        command.CommandText =
            """
            INSERT INTO AdminAccounts (UsernameNormalized, Username, PasswordHash, LastLoginAtUtc, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($normalized, $username, $hash, NULL, $at, $at)
            ON CONFLICT(UsernameNormalized) DO UPDATE SET
                Username = excluded.Username,
                PasswordHash = excluded.PasswordHash,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$normalized", Normalize(username));
        command.Parameters.AddWithValue("$username", username);
        command.Parameters.AddWithValue("$hash", passwordHash);
        command.Parameters.AddWithValue("$at", atUtc.UtcDateTime.ToString("O"));
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
