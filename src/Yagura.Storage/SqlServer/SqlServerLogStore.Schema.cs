using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Yagura.Storage.SqlServer;

public sealed partial class SqlServerLogStore
{
    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>権限不足の区別可能な報告（database.md §5.2 の本体実装）</b>: スキーマ作成・移行が
    /// 権限不足（<see cref="SqlServerFailureClassifier.IsPermissionFailure"/> が真の
    /// <see cref="SqlException"/>）で失敗した場合、<see cref="SchemaPermissionException"/> を送出する。
    /// <see cref="SchemaPermissionException.RemediationSql"/> には接続文字列から得た
    /// データベース名・ユーザー名（Windows 統合認証時は現在の Windows ID）を埋め込むが、
    /// <b>パスワードは埋め込まない</b>（SQL 認証を選んだ場合は <c>&lt;password&gt;</c> という
    /// 明示のプレースホルダを使う——§5.2「提示 SQL は秘密情報を含まない」）。
    /// </para>
    /// <para>
    /// <b>冪等性</b>: <c>CREATE TABLE ... IF NOT EXISTS</c> 相当（<c>OBJECT_ID</c> 判定 +
    /// 条件付き <c>CREATE TABLE</c>）により、既存スキーマへの再実行は何もしない。
    /// </para>
    /// </remarks>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex) when (SqlServerFailureClassifier.IsPermissionFailure(ex))
        {
            throw BuildSchemaPermissionException(ex);
        }
        catch (SqlException ex)
        {
            throw ex.ToLogStoreWriteException("スキーマ初期化のための接続");
        }

        // database.md §5.2「途中失敗からの安全な再実行」: テーブル/索引作成・版間移行・
        // 版の記録を単一トランザクションにまとめる（SqliteLogStore.InitializeAsync と同じ方式）。
        // ALTER COLUMN を伴う v1 -> v2 移行（大テーブルでの所要時間・ロック挙動は DB-10 で実機検証
        // 済み——database.md §5.4。単一トランザクションのため移行完了まで書き込みはブロックされる
        // ＝無瞬断ではない。実測値は database.md §5.4 参照）もこのトランザクションの範囲に含む
        // ——失敗時は全体がロールバックされ、再実行時は sys.columns / sys.indexes の状態確認により
        // 未完了分のみが適用される。
        await using var transaction = (SqlTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // 並行初期化の直列化: 複数の呼び出しが同時に InitializeAsync へ到達した場合、
            // IF OBJECT_ID 判定と CREATE TABLE / ALTER COLUMN の間に他者が割り込むと
            // 「既に存在する」エラーや競合が起こり得る。sp_getapplock（@LockOwner = 'Transaction' は
            // トランザクション終了時に自動解放される——Microsoft Learn "sp_getapplock (Transact-SQL)"）
            // でスキーマ管理全体を排他し、後着は先着の完了を待ってから
            // 冪等判定（適用済みなら何もしない）に入る。
            await using (var lockCommand = connection.CreateCommand())
            {
                lockCommand.Transaction = transaction;
                // スキーマ管理は対話的検索のようなタイムアウト予算（M-10）を持たない管理経路
                // （database.md §1.2「契約拡張の予約」・対話的検索の防御は管理経路に適用しない）。
                // DB-10 実測（tools/Yagura.Bench SchemaMigrationDdl。1000 万行規模）で、
                // ADO.NET 既定の CommandTimeout（30 秒）のまま大規模データへ ALTER COLUMN を
                // 適用すると "実行タイムアウトの期限が切れました" で移行そのものが失敗することを
                // 確認した——本メソッド内の全コマンドを無制限（0）にし、呼び出し側が渡す
                // cancellationToken にのみ打ち切りを委ねる。
                lockCommand.CommandTimeout = 0;
                lockCommand.CommandText =
                    "EXEC sp_getapplock @Resource = N'Yagura.SchemaInitialization', @LockMode = 'Exclusive', @LockOwner = 'Transaction';";
                await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await EnsureCollationAvailableAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandTimeout = 0; // 理由は sp_getapplock コマンドのコメント参照（DB-10）。
                command.CommandText =
                    $"""
                    IF OBJECT_ID(N'dbo.LogRecords', N'U') IS NULL
                    BEGIN
                        CREATE TABLE dbo.LogRecords (
                            Id BIGINT IDENTITY(1,1) PRIMARY KEY,
                            ReceivedAt DATETIME2(7) NOT NULL,
                            SourceAddress NVARCHAR(255) COLLATE {SearchCollation} NOT NULL,
                            SourcePort INT NOT NULL,
                            Protocol INT NOT NULL,
                            DeviceTimestamp DATETIME2(7) NULL,
                            Facility INT NULL,
                            Severity INT NULL,
                            Hostname NVARCHAR(MAX) COLLATE {SearchCollation} NULL,
                            AppName NVARCHAR(MAX) COLLATE {SearchCollation} NULL,
                            ProcId NVARCHAR(MAX) COLLATE {SearchCollation} NULL,
                            MsgId NVARCHAR(MAX) COLLATE {SearchCollation} NULL,
                            StructuredData NVARCHAR(MAX) COLLATE {SearchCollation} NULL,
                            Message NVARCHAR(MAX) COLLATE {SearchCollation} NULL,
                            Raw VARBINARY(MAX) NULL,
                            ParseStatus INT NOT NULL
                        );
                    END;

                    IF OBJECT_ID(N'dbo.SystemEvents', N'U') IS NULL
                    BEGIN
                        CREATE TABLE dbo.SystemEvents (
                            Id BIGINT IDENTITY(1,1) PRIMARY KEY,
                            Kind NVARCHAR(255) COLLATE {SearchCollation} NOT NULL,
                            StartAt DATETIME2(7) NOT NULL,
                            EndAt DATETIME2(7) NOT NULL,
                            Approximate BIT NOT NULL,
                            Details NVARCHAR(MAX) COLLATE {SearchCollation} NULL
                        );
                    END;

                    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SystemEvents_StartAt' AND object_id = OBJECT_ID(N'dbo.SystemEvents'))
                    BEGIN
                        CREATE INDEX IX_SystemEvents_StartAt ON dbo.SystemEvents (StartAt);
                    END;

                    IF OBJECT_ID(N'dbo.SchemaVersion', N'U') IS NULL
                    BEGIN
                        CREATE TABLE dbo.SchemaVersion (
                            Id INT NOT NULL PRIMARY KEY CHECK (Id = 1),
                            Version INT NOT NULL
                        );
                    END;

                    IF OBJECT_ID(N'dbo.SchemaMigrationHistory', N'U') IS NULL
                    BEGIN
                        CREATE TABLE dbo.SchemaMigrationHistory (
                            Version INT NOT NULL PRIMARY KEY,
                            AppliedAt DATETIME2(7) NOT NULL
                        );
                    END;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // 新規作成（上の DDL が直接 v2 形状で作成済み）なら列の移行は不要で現行版をそのまま
            // 記録し、既存データベースからの移行なら列移行ステップを適用する
            // （SchemaMigrationRunner 参照）。
            await SchemaMigrationRunner.RunAsync(
                () => ReadSchemaVersionAsync(connection, transaction, cancellationToken),
                CurrentSchemaVersion,
                recordFreshVersion: () => RecordSchemaVersionAppliedAsync(connection, transaction, CurrentSchemaVersion, cancellationToken),
                applyMigrationsFrom: fromVersion => ApplyMigrationsAsync(connection, transaction, fromVersion, cancellationToken))
                .ConfigureAwait(false);

            // 索引の作成は列の COLLATE/長さが確定した後でなければならない（SourceAddress を含む
            // 複合索引を先に作ると、後続の ALTER COLUMN が「索引がこの列に依存している」として
            // 失敗し得る——database.md §5.4「当該列に索引がある場合は再構築を伴う」）。新規作成・
            // 移行のどちらの経路でもこの時点で列は確定しているため、ここで一括して確定させる。
            await EnsureLogRecordIndexesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex) when (SqlServerFailureClassifier.Classify(ex) == LogStoreFailureKind.CapacityExhausted)
        {
            // database.md §5.2「原因種別で提示内容を書き分ける」: 容量不足は実行可能な SQL では
            // 解決しない物理的対処が要るため、権限不足（SchemaPermissionException・SQL 提示）とは
            // 別経路で報告する。
            throw BuildCapacityExhaustedSchemaException(ex);
        }
        catch (SqlException ex) when (SqlServerFailureClassifier.IsPermissionFailure(ex))
        {
            throw BuildSchemaPermissionException(ex);
        }
        catch (SqlException ex)
        {
            throw ex.ToLogStoreWriteException("スキーマ初期化");
        }
    }

    /// <summary>
    /// <see cref="SearchCollation"/> が接続先インスタンスに実在することを確認する
    /// （database.md §5.4「配備先インスタンスの sys.fn_helpcollations() に実在することの確認を、
    /// 実装バッチのスキーマ管理（接続検証）に含める」）。
    /// </summary>
    private static async Task EnsureCollationAvailableAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 0; // 理由は sp_getapplock コマンドのコメント参照（DB-10）。
        command.CommandText = "SELECT 1 FROM sys.fn_helpcollations() WHERE name = @collation;";
        command.Parameters.Add("@collation", System.Data.SqlDbType.NVarChar, 128).Value = SearchCollation;

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
        {
            throw new LogStoreWriteException(
                LogStoreFailureKind.Permanent,
                $"照合順序 '{SearchCollation}' が接続先の SQL Server インスタンスに存在しません " +
                "(sys.fn_helpcollations() で確認できませんでした)。database.md §5.4 が要求する自由文検索の " +
                "一致規則を適用できないため、SQL Server のバージョン・エディションを確認してください。");
        }
    }

    /// <summary>
    /// スキーマ版間移行の適用点（<see cref="Sqlite.SqliteLogStore"/> の同名メソッドと同じ役割）。
    /// </summary>
    private static async Task ApplyMigrationsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int fromVersion,
        CancellationToken cancellationToken)
    {
        if (fromVersion < 2)
        {
            // v1 -> v2（database.md §5.4）: 対象 NVARCHAR 列へ COLLATE を明示し、
            // ヘッダ列（Hostname/AppName/ProcId/MsgId）は同時に NVARCHAR(MAX) へ拡張する
            // （列ごとに sys.columns で適用済みかを確認してから ALTER する——
            // database.md §5.2「現在の列照合順序を確認し、適用済みなら何もしない」の実体化。
            // 大テーブルでの ALTER COLUMN 1 回ごとの所要時間・ロック挙動は DB-10 で実機検証済み
            // （database.md §5.4）——単一トランザクション実行のまま採用し、分割実行は不要と判断した）。
            await EnsureColumnCollationAsync(connection, transaction, "dbo.LogRecords", "SourceAddress", "NVARCHAR(255)", expectMaxLength: false, isNullable: false, cancellationToken).ConfigureAwait(false);
            await EnsureColumnCollationAsync(connection, transaction, "dbo.LogRecords", "Hostname", "NVARCHAR(MAX)", expectMaxLength: true, isNullable: true, cancellationToken).ConfigureAwait(false);
            await EnsureColumnCollationAsync(connection, transaction, "dbo.LogRecords", "AppName", "NVARCHAR(MAX)", expectMaxLength: true, isNullable: true, cancellationToken).ConfigureAwait(false);
            await EnsureColumnCollationAsync(connection, transaction, "dbo.LogRecords", "ProcId", "NVARCHAR(MAX)", expectMaxLength: true, isNullable: true, cancellationToken).ConfigureAwait(false);
            await EnsureColumnCollationAsync(connection, transaction, "dbo.LogRecords", "MsgId", "NVARCHAR(MAX)", expectMaxLength: true, isNullable: true, cancellationToken).ConfigureAwait(false);
            await EnsureColumnCollationAsync(connection, transaction, "dbo.LogRecords", "StructuredData", "NVARCHAR(MAX)", expectMaxLength: true, isNullable: true, cancellationToken).ConfigureAwait(false);
            await EnsureColumnCollationAsync(connection, transaction, "dbo.LogRecords", "Message", "NVARCHAR(MAX)", expectMaxLength: true, isNullable: true, cancellationToken).ConfigureAwait(false);
            await EnsureColumnCollationAsync(connection, transaction, "dbo.SystemEvents", "Kind", "NVARCHAR(255)", expectMaxLength: false, isNullable: false, cancellationToken).ConfigureAwait(false);
            await EnsureColumnCollationAsync(connection, transaction, "dbo.SystemEvents", "Details", "NVARCHAR(MAX)", expectMaxLength: true, isNullable: true, cancellationToken).ConfigureAwait(false);

            await RecordSchemaVersionAppliedAsync(connection, transaction, 2, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 指定列が目標の型（<paramref name="expectMaxLength"/> が真なら NVARCHAR(MAX)）・
    /// <see cref="SearchCollation"/> に既に一致しているかを <c>sys.columns</c> で確認し、
    /// 一致していなければ <c>ALTER TABLE ... ALTER COLUMN</c> を適用する（database.md §5.2 の
    /// 冪等性要件——「現在の列照合順序を確認し、適用済みなら何もしない」）。
    /// </summary>
    private static async Task EnsureColumnCollationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string tableName,
        string columnName,
        string targetSqlType,
        bool expectMaxLength,
        bool isNullable,
        CancellationToken cancellationToken)
    {
        short currentMaxLength = 0;
        string? currentCollation = null;

        await using (var checkCommand = connection.CreateCommand())
        {
            checkCommand.Transaction = transaction;
            checkCommand.CommandTimeout = 0; // 理由は sp_getapplock コマンドのコメント参照（DB-10）。
            checkCommand.CommandText =
                """
                SELECT max_length, collation_name
                FROM sys.columns
                WHERE object_id = OBJECT_ID(@table) AND name = @column;
                """;
            checkCommand.Parameters.Add("@table", System.Data.SqlDbType.NVarChar, 256).Value = tableName;
            checkCommand.Parameters.Add("@column", System.Data.SqlDbType.NVarChar, 128).Value = columnName;

            await using var reader = await checkCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                currentMaxLength = reader.GetInt16(0);
                currentCollation = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }

        // NVARCHAR(MAX) は sys.columns.max_length = -1 として現れる（Microsoft Learn "sys.columns"
        // の記載どおり）。長さ変更が不要な列（SourceAddress・Kind）は expectMaxLength = false のため
        // 長さの一致判定を常に true とし、COLLATE の一致のみで冪等性を判定する。
        var lengthAlreadyCorrect = !expectMaxLength || currentMaxLength == -1;
        var collationAlreadyCorrect = string.Equals(currentCollation, SearchCollation, StringComparison.Ordinal);

        if (lengthAlreadyCorrect && collationAlreadyCorrect)
        {
            // database.md §5.2「適用済みなら何もしない」。
            return;
        }

        var nullability = isNullable ? "NULL" : "NOT NULL";
        await using var alterCommand = connection.CreateCommand();
        alterCommand.Transaction = transaction;
        // DB-10 実測（tools/Yagura.Bench SchemaMigrationDdl）: 1000 万行規模で
        // NVARCHAR(255)→NVARCHAR(MAX) を伴う列の ALTER COLUMN が ADO.NET 既定の 30 秒
        // CommandTimeout を超え、"実行タイムアウトの期限が切れました" で移行自体が失敗することを
        // 確認した（size-of-data 変更を伴う ALTER COLUMN は全ページ書き換えを要するため、
        // 行数に比例して時間がかかる）。この ALTER 自体が最も時間のかかるコマンドであるため、
        // 無制限にし、呼び出し側の cancellationToken にのみ打ち切りを委ねる
        // （sp_getapplock コマンドのコメント参照）。
        alterCommand.CommandTimeout = 0;
        alterCommand.CommandText =
            $"ALTER TABLE {tableName} ALTER COLUMN {columnName} {targetSqlType} COLLATE {SearchCollation} {nullability};";
        await alterCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// v2 の索引集合を確定させる。列の COLLATE/長さが確定した後（新規作成直後、
    /// または <see cref="ApplyMigrationsAsync"/> 完了後）に呼び出すこと——<see cref="EnsureColumnCollationAsync"/>
    /// のドキュメント参照。<c>IF (NOT) EXISTS</c> による冪等な収束のため、呼び出しごとに毎回
    /// 実行しても安全（かつ安価）。
    /// </summary>
    private static async Task EnsureLogRecordIndexesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // 索引作成自体も大規模データでは低速になり得る（DB-10 実測。sp_getapplock コマンドの
        // コメント参照）——3 索引の新規構築を伴い得るため既定 30 秒では不足し得る。
        command.CommandTimeout = 0;
        command.CommandText =
            """
            -- v1 の単一列索引は複合索引 IX_LogRecords_ReceivedAt_Id に包含される（先頭列が同じ）ため、
            -- 冗長な書き込みコストを避けるために削除する。
            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LogRecords_ReceivedAt' AND object_id = OBJECT_ID(N'dbo.LogRecords'))
            BEGIN
                DROP INDEX IX_LogRecords_ReceivedAt ON dbo.LogRecords;
            END;

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LogRecords_ReceivedAt_Id' AND object_id = OBJECT_ID(N'dbo.LogRecords'))
            BEGIN
                CREATE INDEX IX_LogRecords_ReceivedAt_Id ON dbo.LogRecords (ReceivedAt DESC, Id DESC);
            END;

            -- Issue #145 症状 1: Severity 絞り込み（閾値方式 Severity <= N——Issue #148）が
            -- 索引に乗らずフルスキャンする問題。
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LogRecords_Severity_ReceivedAt' AND object_id = OBJECT_ID(N'dbo.LogRecords'))
            BEGIN
                CREATE INDEX IX_LogRecords_Severity_ReceivedAt ON dbo.LogRecords (Severity, ReceivedAt DESC);
            END;

            -- Issue #145 症状 1 後段: QuerySourceActivityAsync の GROUP BY SourceAddress、
            -- および QueryAsync の SourceAddress 完全一致条件の両方に使う。
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LogRecords_SourceAddress_ReceivedAt' AND object_id = OBJECT_ID(N'dbo.LogRecords'))
            BEGIN
                CREATE INDEX IX_LogRecords_SourceAddress_ReceivedAt ON dbo.LogRecords (SourceAddress, ReceivedAt DESC);
            END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 適用したスキーマ版と適用日時を記録する（database.md §5.4「適用したスキーマ版と適用日時を
    /// 事後に問い合わせ可能な形で保持する」の SQL Server 実体化。
    /// <see cref="Sqlite.SqliteLogStore"/> の同名メソッドと同じ役割）。
    /// </summary>
    private static async Task RecordSchemaVersionAppliedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int version,
        CancellationToken cancellationToken)
    {
        await using (var upsertVersion = connection.CreateCommand())
        {
            upsertVersion.Transaction = transaction;
            upsertVersion.CommandText =
                """
                IF EXISTS (SELECT 1 FROM dbo.SchemaVersion WHERE Id = 1)
                    UPDATE dbo.SchemaVersion SET Version = @version WHERE Id = 1;
                ELSE
                    INSERT INTO dbo.SchemaVersion (Id, Version) VALUES (1, @version);
                """;
            upsertVersion.Parameters.AddWithValue("@version", version);
            await upsertVersion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var insertHistory = connection.CreateCommand();
        insertHistory.Transaction = transaction;
        insertHistory.CommandText =
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.SchemaMigrationHistory WHERE Version = @version)
            BEGIN
                INSERT INTO dbo.SchemaMigrationHistory (Version, AppliedAt) VALUES (@version, SYSUTCDATETIME());
            END;
            """;
        insertHistory.Parameters.AddWithValue("@version", version);
        await insertHistory.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 容量枯渇によるスキーマ初期化・移行の失敗を組み立てる（database.md §5.2「原因種別で提示内容を
    /// 書き分ける」）。権限不足（<see cref="BuildSchemaPermissionException"/>）とは異なり、
    /// 提示すべきは実行可能な SQL ではなく物理的対処である。
    /// </summary>
    private static LogStoreWriteException BuildCapacityExhaustedSchemaException(SqlException ex) =>
        new(
            LogStoreFailureKind.CapacityExhausted,
            $"スキーマ初期化・移行に必要な領域が不足しています (SqlErrorNumber={ex.Number})。" +
            "権限不足と異なりこの状態は実行可能な SQL では解決しません——ディスクの空き容量確保、" +
            "データファイル/ログファイルの増設、または保持期間短縮による事前のログ削除など、" +
            "物理的な対処が必要です（database.md §5.2）。",
            ex);

    // SqlServerFailureClassifier.IsPermissionFailure が true を返すエラー番号のうち、
    // 4060（CannotOpenDatabase）は「DB 不在」と「ログインに CONNECT 権限がない」の 2 通りの
    // 原因が同一番号に重なる（Microsoft Learn "Database Engine events and errors" の
    // 記載は原因を区別しない）。両者は提示すべき SQL が異なる
    // （前者は CREATE DATABASE から必要、後者はログイン作成・権限付与のみで足りる）ため、
    // 提示 SQL を作る側で両方に対応できる形にする。
    private const int CannotOpenDatabaseErrorNumber = 4060;

    /// <summary>
    /// 権限不足時の <see cref="SchemaPermissionException"/> を組み立てる（database.md §5.2）。
    /// </summary>
    private SchemaPermissionException BuildSchemaPermissionException(SqlException ex)
    {
        var builder = new SqlConnectionStringBuilder(_connectionString);
        var databaseName = string.IsNullOrWhiteSpace(builder.InitialCatalog) ? "<database>" : builder.InitialCatalog;

        // Windows 統合認証（既定・第一推奨。configuration.md §5.1）ではログイン名は実行時の
        // Windows ID であり、生成時点では確定できないため明示のプレースホルダとする。
        // SQL 認証を選んだ場合、ユーザー名は接続文字列に含まれる非秘密情報のため埋め込んでよいが、
        // パスワードは §5.2「提示 SQL は秘密情報を含まない」により常にプレースホルダとする。
        var loginName = builder.IntegratedSecurity || string.IsNullOrWhiteSpace(builder.UserID)
            ? "<Windows または SQL ログイン名>"
            : builder.UserID;

        var isCannotOpenDatabase = ex.Number == CannotOpenDatabaseErrorNumber;

        var missingPermission = isCannotOpenDatabase
            ? $"データベース '{databaseName}' に接続できません——データベースが存在しないか、" +
              $"ログインに CONNECT 権限がありません (SqlErrorNumber={ex.Number})。"
            : $"データベース '{databaseName}' に対するスキーマ作成・変更権限（CREATE TABLE 等）が不足しています " +
              $"(SqlErrorNumber={ex.Number})。";

        // 4060 はデータベース不在の可能性があるため、提示 SQL は「無ければ作成」を先頭に含める
        // （既存データベースへの接続時に不要な CREATE DATABASE は実行されない——IF NOT EXISTS 相当）。
        // それ以外（229 権限不足・18456 ログイン失敗）は既存データベースへの到達は確認済みのため、
        // データベース作成手順を含めない。
        var createDatabaseStep = isCannotOpenDatabase
            ? $"""
              -- SqlErrorNumber 4060 はデータベース不在・CONNECT 権限不足の両方で発生し得るため、
              -- まずデータベースの存在を確認し、無ければ作成する（既存データベースならこのブロックは何もしない）。
              IF DB_ID(N'{databaseName}') IS NULL
              BEGIN
                  CREATE DATABASE [{databaseName}];
              END;

              """
            : string.Empty;

        var remediationSql =
            $"""
            -- database.md §5.2: 管理者資格情報でそのまま実行できる SQL（秘密情報は含まない）。
            -- Windows 統合認証を第一推奨とするため、ログイン作成は既定でこの方式を示す。
            -- SQL 認証を選んだ場合、パスワード部は下記のプレースホルダを埋めて実行すること
            -- （このファイル自体にパスワードの実値を書かない——依頼文としてそのまま流通させるため）。
            {createDatabaseStep}USE [{databaseName}];
            IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'{loginName}')
            BEGIN
                CREATE USER [{loginName}] FOR LOGIN [{loginName}];
                -- SQL 認証ログイン自体が未作成の場合は先に以下を実行する（プレースホルダはそのまま埋めないこと）:
                -- CREATE LOGIN [{loginName}] WITH PASSWORD = '<password>';
            END;
            ALTER ROLE db_ddladmin ADD MEMBER [{loginName}];
            ALTER ROLE db_datareader ADD MEMBER [{loginName}];
            ALTER ROLE db_datawriter ADD MEMBER [{loginName}];
            """;

        return new SchemaPermissionException(missingPermission, remediationSql);
    }

    private static async Task<int?> ReadSchemaVersionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Version FROM dbo.SchemaVersion WHERE Id = 1;";

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }
}
