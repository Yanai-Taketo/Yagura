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
    /// <summary>
    /// <see cref="Sqlite.SqliteAdminAccountStore.CurrentSchemaVersion"/> と同じ意味の版数
    /// （<b>両者は常に同じ値を保つこと</b>——片方だけ上げると片 provider だけ移行が走る）。
    /// v2 = ロックアウト 2 列の削除（ADR-0011 決定 8）、v3 = <c>CreatedAtUtc</c>/<c>UpdatedAtUtc</c>
    /// の追加（ADR-0021 決定 1 の切替時点検）。
    /// </summary>
    internal const int CurrentSchemaVersion = 3;

    private readonly string _connectionString;

    public SqlServerAdminAccountStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connectionString = connectionString;
    }

    /// <summary>
    /// スキーマを初期化する（冪等）。新規データベースは最新形状（v3）で直接作成し、既存
    /// データベースには記録済み版から現行版までの移行ステップを順に適用する
    /// （<see cref="ApplyMigrationsAsync"/>。<see cref="Sqlite.SqliteAdminAccountStore.InitializeAsync"/> と
    /// 同じ判定方式——版管理表の不在を「新規」と「無版数の v1」のどちらとも解釈し得るため、
    /// <c>dbo.AdminAccounts</c> の実在有無で区別する）。
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var adminAccountsExistedBefore = await ObjectExistsAsync(connection, "dbo.AdminAccounts", "U", cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                IF OBJECT_ID(N'dbo.AdminAccounts', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.AdminAccounts (
                        UsernameNormalized NVARCHAR(256) NOT NULL PRIMARY KEY,
                        Username NVARCHAR(256) NOT NULL,
                        PasswordHash NVARCHAR(512) NOT NULL,
                        LastLoginAtUtc DATETIME2(7) NULL,
                        CreatedAtUtc DATETIME2(7) NULL,
                        UpdatedAtUtc DATETIME2(7) NULL
                    );
                END

                IF OBJECT_ID(N'dbo.AdminAccountsSchemaVersion', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.AdminAccountsSchemaVersion (
                        Id INT NOT NULL PRIMARY KEY CHECK (Id = 1),
                        Version INT NOT NULL
                    );
                END
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var recordedVersion = await ReadSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);

        // 版表なし + 表が既存 = バージョン管理導入前（v1 相当）。版表なし + 表も新規 = 上の DDL が
        // 最新形状で作ったため移行不要。
        var fromVersion = recordedVersion ?? (adminAccountsExistedBefore ? 1 : CurrentSchemaVersion);

        await ApplyMigrationsAsync(connection, fromVersion, cancellationToken).ConfigureAwait(false);

        // 版の記録は全ステップ成功後に 1 回（先に上げて失敗すると未適用の移行が永久にスキップされる）。
        await RecordSchemaVersionAsync(connection, CurrentSchemaVersion, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 記録済み版から現行版までの移行ステップを昇順に適用する（各ステップは実在確認により冪等。
    /// <see cref="Sqlite.SqliteAdminAccountStore"/> と同じ構造・同じ順序を保つこと）。
    /// </summary>
    private static async Task ApplyMigrationsAsync(SqlConnection connection, int fromVersion, CancellationToken cancellationToken)
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

    private static async Task<bool> ObjectExistsAsync(SqlConnection connection, string objectName, string objectType, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN OBJECT_ID(@objectName, @objectType) IS NULL THEN 0 ELSE 1 END;";
        command.Parameters.AddWithValue("@objectName", objectName);
        command.Parameters.AddWithValue("@objectType", objectType);
        var result = (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return result == 1;
    }

    private static async Task<int?> ReadSchemaVersionAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version FROM dbo.AdminAccountsSchemaVersion WHERE Id = 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : (int)result;
    }

    private static async Task RecordSchemaVersionAsync(SqlConnection connection, int version, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            MERGE dbo.AdminAccountsSchemaVersion AS target
            USING (SELECT 1 AS Id) AS source
            ON target.Id = source.Id
            WHEN MATCHED THEN UPDATE SET Version = @version
            WHEN NOT MATCHED THEN INSERT (Id, Version) VALUES (1, @version);
            """;
        command.Parameters.AddWithValue("@version", version);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// v1 → v2 移行（ADR-0011 決定 8）: <c>FailedAttemptCount</c>/<c>LockoutUntilUtc</c> 列を
    /// 削除する。<c>FailedAttemptCount</c> には既定制約 <c>DF_AdminAccounts_FailedAttemptCount</c>
    /// が付いているため、列削除の前に既定制約を先に削除する必要がある（SQL Server の制約:
    /// デフォルト制約が付いた列は制約を残したまま DROP COLUMN できない）。列・制約とも
    /// <c>sys.columns</c>/<c>sys.default_constraints</c> で実在確認してから削除し、
    /// 既に適用済みの再実行に対しても冪等に振る舞う。
    /// </summary>
    private static async Task DropLegacyLockoutColumnsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using (var dropConstraint = connection.CreateCommand())
        {
            dropConstraint.CommandText =
                """
                DECLARE @constraintName SYSNAME;
                SELECT @constraintName = dc.name
                FROM sys.default_constraints dc
                JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
                WHERE dc.parent_object_id = OBJECT_ID(N'dbo.AdminAccounts') AND c.name = 'FailedAttemptCount';

                IF @constraintName IS NOT NULL
                BEGIN
                    EXEC('ALTER TABLE dbo.AdminAccounts DROP CONSTRAINT [' + @constraintName + ']');
                END
                """;
            await dropConstraint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var columnName in new[] { "FailedAttemptCount", "LockoutUntilUtc" })
        {
            await using var dropColumn = connection.CreateCommand();
            dropColumn.CommandText =
                $"""
                IF COL_LENGTH('dbo.AdminAccounts', '{columnName}') IS NOT NULL
                BEGIN
                    ALTER TABLE dbo.AdminAccounts DROP COLUMN {columnName};
                END
                """;
            await dropColumn.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// v2 → v3 移行（ADR-0021 決定 1）: <c>CreatedAtUtc</c>/<c>UpdatedAtUtc</c> 列を追加する。
    /// <b>既存行はバックフィルしない</b>（NULL = 不明。理由は
    /// <see cref="Sqlite.SqliteAdminAccountStore"/> の同名メソッド参照）。**既定制約は付けない**——
    /// v1 → v2 で <c>DF_AdminAccounts_FailedAttemptCount</c> の先行削除が必要になった教訓
    /// （既定制約付きの列は制約を残したまま DROP COLUMN できない）を繰り返さないため。
    /// <c>COL_LENGTH</c> の実在確認により再実行に対して冪等。
    /// </summary>
    private static async Task AddAccountTimestampColumnsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        foreach (var columnName in new[] { "CreatedAtUtc", "UpdatedAtUtc" })
        {
            await using var addColumn = connection.CreateCommand();
            addColumn.CommandText =
                $"""
                IF COL_LENGTH('dbo.AdminAccounts', '{columnName}') IS NULL
                BEGIN
                    ALTER TABLE dbo.AdminAccounts ADD {columnName} DATETIME2(7) NULL;
                END
                """;
            await addColumn.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
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
            "SELECT TOP (1) Username, PasswordHash, LastLoginAtUtc, CreatedAtUtc, UpdatedAtUtc FROM dbo.AdminAccounts;";

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

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Username, PasswordHash, LastLoginAtUtc, CreatedAtUtc, UpdatedAtUtc FROM dbo.AdminAccounts " +
            "WHERE UsernameNormalized = @normalized;";
        command.Parameters.AddWithValue("@normalized", Normalize(username));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadRecord(reader);
    }

    private static AdminAccountRecord ReadRecord(SqlDataReader reader) => new(
        Username: reader.GetString(0),
        PasswordHash: reader.GetString(1),
        LastLoginAtUtc: ReadTimestamp(reader, 2),
        CreatedAtUtc: ReadTimestamp(reader, 3),
        UpdatedAtUtc: ReadTimestamp(reader, 4));

    private static DateTimeOffset? ReadTimestamp(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : new DateTimeOffset(reader.GetDateTime(ordinal), TimeSpan.Zero);

    public async Task UpsertAsync(
        string username, string passwordHash, DateTimeOffset atUtc, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        // 新規は作成時刻・更新時刻の両方、既存の更新は更新時刻のみ（SQLite 実装と同じ意味論）。
        command.CommandText =
            """
            MERGE dbo.AdminAccounts AS target
            USING (SELECT @normalized AS UsernameNormalized) AS source
            ON target.UsernameNormalized = source.UsernameNormalized
            WHEN MATCHED THEN UPDATE SET
                Username = @username,
                PasswordHash = @hash,
                UpdatedAtUtc = @at
            WHEN NOT MATCHED THEN INSERT (UsernameNormalized, Username, PasswordHash, LastLoginAtUtc, CreatedAtUtc, UpdatedAtUtc)
                VALUES (@normalized, @username, @hash, NULL, @at, @at);
            """;
        command.Parameters.AddWithValue("@normalized", Normalize(username));
        command.Parameters.AddWithValue("@username", username);
        command.Parameters.AddWithValue("@hash", passwordHash);
        command.Parameters.AddWithValue("@at", atUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordSuccessfulLoginAsync(string username, DateTimeOffset atUtc, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE dbo.AdminAccounts SET LastLoginAtUtc = @at WHERE UsernameNormalized = @normalized;";
        command.Parameters.AddWithValue("@at", atUtc.UtcDateTime);
        command.Parameters.AddWithValue("@normalized", Normalize(username));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Normalize(string username) => username.Trim().ToLowerInvariant();
}
