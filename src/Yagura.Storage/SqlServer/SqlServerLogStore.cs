using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Yagura.Storage.SqlServer;

/// <summary>
/// <see cref="ILogStore"/> の SQL Server 実装（database.md §5 本番 provider）。
/// </summary>
/// <remarks>
/// <para>
/// <b>読み書き分離の性質（database.md §1.2 契約表 末尾・§1.3 の文書化義務）</b>: 本実装は
/// 接続の分離レベルを明示的に変更せず、<b>SQL Server の既定分離レベル（READ COMMITTED、かつ
/// <c>READ_COMMITTED_SNAPSHOT</c> データベースオプションは既定 OFF）</b>のまま動作する。
/// この既定構成では、Microsoft Learn 公式ドキュメント「SET TRANSACTION ISOLATION LEVEL
/// (Transact-SQL)」（確認日 2026-07-05）の記載どおり:
/// <c>"If READ_COMMITTED_SNAPSHOT is set to OFF (the default on SQL Server), the Database Engine
/// uses shared locks to prevent other transactions from modifying rows while the current
/// transaction is running a read operation. The shared locks also block the statement from
/// reading rows modified by other transactions until the other transaction is completed."</c>
/// —— <b>つまり読み取り（検索）と書き込み（バッチ挿入・保持期間削除）は互いにブロックし得る</b>。
/// これは SQLite（WAL。<see cref="Sqlite.SqliteLogStore"/> のドキュメント参照）が実現する
/// 「読み取りは書き込みをブロックせず、書き込みも読み取りをブロックしない」性質とは<b>明確に異なる</b>。
/// </para>
/// <para>
/// <b>この性質の含意</b>: 対話的検索（<see cref="QueryAsync"/>）が長時間の共有ロックを保持すると、
/// 同時に実行される <see cref="WriteBatchAsync"/>・<see cref="DeleteOlderThanAsync"/> がブロックされ得る
/// （逆方向も同様）。<see cref="LogQuery.Timeout"/> による上限時間は「検索自体の打ち切り」であり、
/// 「検索が他の操作をブロックする時間」を直接には制限しない——ロック保持は検索文の実行時間と
/// 概ね一致するため、対話的検索のタイムアウト設計（M-10）が実質的な上限を与える。
/// <c>READ_COMMITTED_SNAPSHOT</c>（行バージョニングでロック不要の読み取りを実現する DB オプション）を
/// 有効化すれば SQLite の WAL に近い挙動へ変更できるが、v0.1 時点では既定のまま採用する
/// （行バージョニングは tempdb 使用量増加という別のトレードオフを伴うため、有効化の要否は
/// 実測を経て再評価する——DB-4 の実機検証と合わせて評価する候補とする）。
/// </para>
/// <para>
/// <b>付随する運用特性</b>: バルク挿入・保持期間削除は複数行にまたがるため、行ロックがページ/テーブル
/// ロックへエスカレーションし得る（SQL Server のロックエスカレーションの一般的な挙動。バッチサイズを
/// 適度に抑える設計——<see cref="RetentionConstants.DeleteBatchMaxSize"/> と同じ粒度——がエスカレーションの
/// 発生を抑える）。
/// </para>
/// </remarks>
public sealed class SqlServerLogStore : ILogStore, IBulkLogReader, IAsyncDisposable
{
    /// <summary>
    /// 現行のスキーマバージョン（<see cref="Sqlite.SqliteLogStore.CurrentSchemaVersion"/> と同じ意味）。
    /// v2（Issue #145・#147・#146。database.md §8 DB-6 決定・§5.4）: 絞り込み列の複合索引の追加、
    /// ヘッダ列（Hostname/AppName/ProcId/MsgId）の NVARCHAR(MAX) 化、対象 NVARCHAR 列への
    /// COLLATE <see cref="SearchCollation"/> の明示を行う。
    /// </summary>
    internal const int CurrentSchemaVersion = 2;

    /// <summary>
    /// 自由文検索の一致規則（database.md §1.2 DB-6・§5.4）を実装する列単位 COLLATE。
    /// Windows 照合順序 version 100・大文字小文字非区別（CI）・アクセント区別（AS）・
    /// かな種区別（KS）・全角/半角区別（WS）・補助文字対応（SC）。§5.4 の却下案検討を経て確定
    /// （KS/WS を明示し、大文字小文字以外を区別する側に固定することで「折り畳むのは大文字小文字のみ」
    /// という DB-6 の規則を過不足なく実装する）。
    /// </summary>
    internal const string SearchCollation = "Latin1_General_100_CI_AS_KS_WS_SC";

    // SERVERPROPERTY('EngineEdition') の値（Microsoft Learn "SERVERPROPERTY (Transact-SQL)" の
    // Edition テーブル。確認日 2026-07-05）: "4 = Express (For Express, Express with Tools, and
    // Express with Advanced Services)"。
    private const int EngineEditionExpress = 4;

    private readonly string _connectionString;

    // Windows 統合認証の接続か（イベントログ警告 1031 の分類は統合認証の接続に限って行う——
    // ADR-0015 決定 5。Issue #418。SQL 認証の 18456/4060 は統合認証の切り分け対象ではない）。
    private readonly bool _integratedAuthentication;

    /// <summary>
    /// 指定した接続文字列で <see cref="SqlServerLogStore"/> を構築する。
    /// </summary>
    /// <param name="connectionString">
    /// SQL Server への接続文字列（configuration.md §2 の DPAPI 保護対象。本クラスは
    /// 復号済みの平文接続文字列を受け取る——復号自体はホスト層の責務）。
    /// </param>
    public SqlServerLogStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connectionString = connectionString;
        _integratedAuthentication = UsesIntegratedAuthentication(connectionString);
    }

    /// <summary>
    /// 接続文字列が Windows 統合認証か。パース不能な接続文字列はどのみち接続時に失敗して
    /// 通常の恒久障害経路（1030）に乗るため、ここでは偽へ倒すだけにする（構築時に投げない——
    /// 本コンストラクタは従来から接続文字列の妥当性検証を責務にしていない）。
    /// </summary>
    private static bool UsesIntegratedAuthentication(string connectionString)
    {
        try
        {
            return new SqlConnectionStringBuilder(connectionString).IntegratedSecurity;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

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
            // トランザクション終了時に自動解放される——Microsoft Learn "sp_getapplock (Transact-SQL)"。
            // 確認日 2026-07-10）でスキーマ管理全体を排他し、後着は先着の完了を待ってから
            // 冪等判定（適用済みなら何もしない）に入る。
            await using (var lockCommand = connection.CreateCommand())
            {
                lockCommand.Transaction = transaction;
                // スキーマ管理は対話的検索のようなタイムアウト予算（M-10）を持たない管理経路
                // （database.md §1.2「契約拡張の予約」・対話的検索の防御は管理経路に適用しない）。
                // DB-10 実測（tools/Yagura.Bench SchemaMigrationDdl。2026-07-10・1000 万行規模）で、
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

            var recordedVersion = await ReadSchemaVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

            if (recordedVersion is null)
            {
                // 新規作成: 上の DDL が直接 v2 形状（NVARCHAR(MAX)・COLLATE 済み）で作成しているため、
                // 列の移行作業は不要——現行バージョンをそのまま記録する。
                await RecordSchemaVersionAppliedAsync(connection, transaction, CurrentSchemaVersion, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (recordedVersion.Value < CurrentSchemaVersion)
            {
                await ApplyMigrationsAsync(connection, transaction, recordedVersion.Value, cancellationToken)
                    .ConfigureAwait(false);
            }

            // recordedVersion.Value == CurrentSchemaVersion の場合: 既に適用済みのため列移行は行わない。

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
            // v1 -> v2（Issue #146・#147・database.md §5.4）: 対象 NVARCHAR 列へ COLLATE を明示し、
            // ヘッダ列（Hostname/AppName/ProcId/MsgId）は同時に NVARCHAR(MAX) へ拡張する
            // （Issue #147。列ごとに sys.columns で適用済みかを確認してから ALTER する——
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
        // DB-10 実測（tools/Yagura.Bench SchemaMigrationDdl。2026-07-10）: 1000 万行規模で
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
    /// v2 の索引集合を確定させる（Issue #145）。列の COLLATE/長さが確定した後（新規作成直後、
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
    // 記載——確認日 2026-07-05——は原因を区別しない）。両者は提示すべき SQL が異なる
    // （前者は CREATE DATABASE から必要、後者はログイン作成・権限付与のみで足りる）ため、
    // 提示 SQL を作る側で両方に対応できる形にする（コードレビューで指摘・確認済み）。
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

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>SqlBulkCopy 採用可否の判断（本 Issue の設計判断）</b>: <c>SqlBulkCopy</c> は採用しない。
    /// 理由: (1) <c>SqlBulkCopy</c> は既定でトランザクションログへの記録を最小化する一括ロード
    /// 経路であり、行単位のエラー詳細（どの行が失敗したか）を返さない——本設計は
    /// database.md §1.2「部分成功の扱い」を明確にする必要があり、パラメータ化 INSERT を
    /// 1 トランザクションにまとめる方式の方が失敗の分類（<see cref="SqlServerFailureClassifier"/>）と
    /// 整合しやすい。(2) 保持期間削除（<see cref="DeleteOlderThanAsync"/>）が分割実行である一方、
    /// バッチ挿入の想定件数（受信段のバッチ粒度）は <c>SqlBulkCopy</c> の性能優位が効く規模
    /// （数万〜数十万行）に達しない見込みであり、複雑さに見合わない。(3) <c>SqlBulkCopy</c> は
    /// 既定でスキーマ制約（CHECK 等）を一部バイパスする設計であり、将来スキーマに制約を
    /// 追加する余地を狭める。性能上の必要性が実測で確認された場合は再評価する
    /// （M5-3 の設計判断として最終報告に明記する）。
    /// </para>
    /// <para>
    /// パラメータ化 INSERT を 1 トランザクションにまとめて実行する
    /// （<see cref="Sqlite.SqliteLogStore.WriteBatchAsync"/> と同じ方式）。
    /// </para>
    /// </remarks>
    public async Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        if (records.Count == 0)
        {
            return;
        }

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var transaction = (SqlTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO dbo.LogRecords
                    (ReceivedAt, SourceAddress, SourcePort, Protocol, DeviceTimestamp,
                     Facility, Severity, Hostname, AppName, ProcId, MsgId,
                     StructuredData, Message, Raw, ParseStatus)
                VALUES
                    (@receivedAt, @sourceAddress, @sourcePort, @protocol, @deviceTimestamp,
                     @facility, @severity, @hostname, @appName, @procId, @msgId,
                     @structuredData, @message, @raw, @parseStatus);
                """;

            var receivedAt = command.Parameters.Add("@receivedAt", System.Data.SqlDbType.DateTime2);
            var sourceAddress = command.Parameters.Add("@sourceAddress", System.Data.SqlDbType.NVarChar, 255);
            var sourcePort = command.Parameters.Add("@sourcePort", System.Data.SqlDbType.Int);
            var protocol = command.Parameters.Add("@protocol", System.Data.SqlDbType.Int);
            var deviceTimestamp = command.Parameters.Add("@deviceTimestamp", System.Data.SqlDbType.DateTime2);
            var facility = command.Parameters.Add("@facility", System.Data.SqlDbType.Int);
            var severity = command.Parameters.Add("@severity", System.Data.SqlDbType.Int);
            // Issue #147: Hostname/AppName/ProcId/MsgId は NVARCHAR(MAX) 列（v2 スキーマ）のため
            // パラメータの Size 指定も撤廃する（Size=255 のまま残すと、DDL 側を MAX 化しても
            // パラメータ側で 255 文字に黙って切り詰められる——ADO.NET のパラメータ長は列長と独立に
            // 効くため、両方を揃える必要がある）。
            var hostname = command.Parameters.Add("@hostname", System.Data.SqlDbType.NVarChar, -1);
            var appName = command.Parameters.Add("@appName", System.Data.SqlDbType.NVarChar, -1);
            var procId = command.Parameters.Add("@procId", System.Data.SqlDbType.NVarChar, -1);
            var msgId = command.Parameters.Add("@msgId", System.Data.SqlDbType.NVarChar, -1);
            var structuredData = command.Parameters.Add("@structuredData", System.Data.SqlDbType.NVarChar, -1);
            var message = command.Parameters.Add("@message", System.Data.SqlDbType.NVarChar, -1);
            var raw = command.Parameters.Add("@raw", System.Data.SqlDbType.VarBinary, -1);
            var parseStatus = command.Parameters.Add("@parseStatus", System.Data.SqlDbType.Int);

            foreach (var record in records)
            {
                receivedAt.Value = record.ReceivedAt.UtcDateTime;
                sourceAddress.Value = record.SourceAddress;
                sourcePort.Value = record.SourcePort;
                protocol.Value = (int)record.Protocol;
                deviceTimestamp.Value = (object?)record.DeviceTimestamp?.UtcDateTime ?? DBNull.Value;
                facility.Value = (object?)record.Facility ?? DBNull.Value;
                severity.Value = (object?)record.Severity ?? DBNull.Value;
                hostname.Value = (object?)record.Hostname ?? DBNull.Value;
                appName.Value = (object?)record.AppName ?? DBNull.Value;
                procId.Value = (object?)record.ProcId ?? DBNull.Value;
                msgId.Value = (object?)record.MsgId ?? DBNull.Value;
                structuredData.Value = (object?)record.StructuredData ?? DBNull.Value;
                message.Value = (object?)record.Message ?? DBNull.Value;
                raw.Value = (object?)record.Raw ?? DBNull.Value;
                parseStatus.Value = (int)record.ParseStatus;

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            // 統合認証フラグを渡すのは本経路のみ: 1031 の発火点（PersistenceWriter——恒久障害の
            // 抑制窓を持つ場所）が消費するのは WriteBatchAsync の失敗だけであり、閲覧系の失敗に
            // 分類を付けても読み手がいない（Issue #418）。
            throw ex.ToLogStoreWriteException(
                $"ログレコードのバッチ書き込み ({records.Count} 件)", _integratedAuthentication);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LogRecordSummary>> QueryLatestAsync(
        int limit,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        QueryAsync(new LogQuery(Limit: limit, Timeout: timeout), cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<LogRecordSummary>> QueryAsync(
        LogQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Limit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Timeout.Ticks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.MessageProjectionLength);

        using var timeoutCts = new CancellationTokenSource(query.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        await using var connection = new SqlConnection(_connectionString);

        var results = new List<LogRecordSummary>(query.Limit);

        try
        {
            await connection.OpenAsync(linkedCts.Token).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            // クエリタイムアウトは接続文字列既定（30 秒）に加え、LogQuery.Timeout でも
            // キャンセルする（CommandTimeout は秒単位の粗い指定のため、実際の打ち切りは
            // CancellationToken 経由の ExecuteReaderAsync キャンセルに委ねる）。
            var whereClauses = new List<string>();

            if (query.ReceivedAtFrom is { } from)
            {
                whereClauses.Add("ReceivedAt >= @receivedAtFrom");
                command.Parameters.Add("@receivedAtFrom", System.Data.SqlDbType.DateTime2).Value = from.UtcDateTime;
            }

            if (query.ReceivedAtTo is { } to)
            {
                whereClauses.Add("ReceivedAt <= @receivedAtTo");
                command.Parameters.Add("@receivedAtTo", System.Data.SqlDbType.DateTime2).Value = to.UtcDateTime;
            }

            if (query.SourceAddress is { } sourceAddress)
            {
                whereClauses.Add("SourceAddress = @sourceAddress");
                command.Parameters.Add("@sourceAddress", System.Data.SqlDbType.NVarChar, 255).Value = sourceAddress;
            }

            if (query.SeverityAtMost is { } severityAtMost)
            {
                // 閾値方式（Severity <= N。LogQuery.SeverityAtMost の doc コメント参照——
                // syslog は数値が小さいほど深刻なため「N 以上の重大度」は「Severity <= N」になる。
                // Severity が NULL（PRI 未解析）の行は比較が unknown になり自然に対象外となる。
                whereClauses.Add("Severity <= @severityAtMost");
                command.Parameters.Add("@severityAtMost", System.Data.SqlDbType.Int).Value = severityAtMost;
            }

            if (query.Facility is { } facilityFilter)
            {
                whereClauses.Add("Facility = @facility");
                command.Parameters.Add("@facility", System.Data.SqlDbType.Int).Value = facilityFilter;
            }

            if (query.ParseStatus is { } parseStatusFilter)
            {
                whereClauses.Add("ParseStatus = @parseStatus");
                command.Parameters.Add("@parseStatus", System.Data.SqlDbType.Int).Value = (int)parseStatusFilter;
            }

            if (query.SearchText is { Length: > 0 } searchText)
            {
                // 自由文検索: Message に対する部分一致・大文字小文字を区別しない
                // （database.md §1.2 DB-6。2026-07-09 オーナー決定で規則は確定済み）。
                // v2 スキーマで Message 列に COLLATE Latin1_General_100_CI_AS_KS_WS_SC を明示した
                // ため（database.md §5.4）、LIKE の大文字小文字非区別（かつアクセント・かな種・
                // 全角/半角は区別する）はサーバの既定照合順序に依存せず列単位で保証される
                // （Issue #146 の解消——配備先の既定照合順序が CS でも本クエリの挙動は変わらない）。
                whereClauses.Add("Message LIKE @searchText ESCAPE '\\'");
                command.Parameters.Add("@searchText", System.Data.SqlDbType.NVarChar, -1).Value =
                    "%" + EscapeLikePattern(searchText) + "%";
            }

            if (query.Cursor is { } cursor)
            {
                // カーソル（キーセット）ページング（database.md §1.2・DB-11。Issue #144）:
                // 複合索引 IX_LogRecords_ReceivedAt_Id（ReceivedAt DESC, Id DESC）と同じ並びで
                // 「カーソルより過去」の行だけに絞るシーク条件。OFFSET は使わない。
                //
                // 述語の形（PR #221 レビュー起点の実行計画実測。2026-07-10・SqlLocalDB・200 万行）:
                // 素朴な OR 分解 (ReceivedAt < @c OR (ReceivedAt = @c AND Id < @i)) は最適化器の
                // 計画選択が不安定で、統計サンプリングの揺れにより Clustered Index Scan + Sort
                // （全該当行を読んでからソート——中間カーソルで実際に約 100 万行を走査し 1 クエリ
                // 約 1 秒・浅いカーソルほど遅い）に落ちる場合と Index Seek になる場合の両方を
                // 同一データで観測した。下の形はこれと論理的に等価（R=@c かつ Id>=@i のとき
                // 両者とも偽・他も一致）で、先頭の conjunct（ReceivedAt <= @c）が単独で索引の
                // シーク述語になる——常にシーク可能な形であることが下記 FORCESEEK の前提。
                whereClauses.Add(
                    "(ReceivedAt <= @cursorReceivedAt AND (ReceivedAt < @cursorReceivedAt OR Id < @cursorId))");
                command.Parameters.Add("@cursorReceivedAt", System.Data.SqlDbType.DateTime2).Value =
                    cursor.ReceivedAt.UtcDateTime;
                command.Parameters.Add("@cursorId", System.Data.SqlDbType.BigInt).Value = cursor.Id;
            }

            var whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : string.Empty;

            // FORCESEEK テーブルヒント（カーソル指定時のみ）: 上記のとおり計画選択が不安定な
            // コスト境界領域にあるため、「索引シークのみを許す」ヒントで計画を固定する
            // （Microsoft Learn "Table hints (Transact-SQL)" が FORCESEEK の用途として挙げる
            // 「推定の問題でシークではなくスキャンが選ばれる場合」そのもの。確認日 2026-07-10）。
            // カーソル指定時は常にシーク可能な範囲条件（ReceivedAt <= @c）が WHERE に含まれる
            // ためコンパイル不能にはならない（他フィルタ——Severity 閾値・LIKE——との併用も
            // 実行計画で確認済み。database.md §8 DB-11）。実測: シーク計画はカーソル深度に
            // よらず約 19ms/クエリ（limit=10,000）で平坦——スキャン計画（深度により
            // 約 0.2〜2 秒）を常に下回る。カーソルなし（先頭ページ・従来経路）には付与しない
            // ——従来の計画選択を変えない。
            var fromClause = query.Cursor is not null
                ? "FROM dbo.LogRecords WITH (FORCESEEK)"
                : "FROM dbo.LogRecords";

            // Id DESC のタイブレーク（Issue #144）: ReceivedAt 単独では同一時刻（同一ミリ秒）の
            // 行の相対順序が SQL 上未定義になる——UDP バースト・スタックトレースの分割送信等、
            // syslog では同一時刻多発が日常的に起きる。Id は採番順（挿入順）と一致するため、
            // 同時刻内は「新しく挿入された行が先」という決定的な順序になる。
            command.CommandText =
                $"""
                SELECT TOP (@limit) Id, ReceivedAt, SourceAddress, SourcePort, Protocol, ParseStatus,
                       DeviceTimestamp, Facility, Severity, Hostname, AppName, ProcId, MsgId,
                       StructuredData, Message
                {fromClause}
                {whereSql}
                ORDER BY ReceivedAt DESC, Id DESC;
                """;
            command.Parameters.Add("@limit", System.Data.SqlDbType.Int).Value = query.Limit;

            await using var reader = await command.ExecuteReaderAsync(linkedCts.Token).ConfigureAwait(false);

            while (await reader.ReadAsync(linkedCts.Token).ConfigureAwait(false))
            {
                var message = reader.IsDBNull(14) ? null : reader.GetString(14);
                results.Add(new LogRecordSummary(
                    Id: reader.GetInt64(0),
                    ReceivedAt: DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
                    SourceAddress: reader.GetString(2),
                    SourcePort: reader.GetInt32(3),
                    Protocol: (Protocol)reader.GetInt32(4),
                    ParseStatus: (ParseStatus)reader.GetInt32(5),
                    DeviceTimestamp: reader.IsDBNull(6) ? null : DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc),
                    Facility: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    Severity: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    Hostname: reader.IsDBNull(9) ? null : reader.GetString(9),
                    AppName: reader.IsDBNull(10) ? null : reader.GetString(10),
                    ProcId: reader.IsDBNull(11) ? null : reader.GetString(11),
                    MsgId: reader.IsDBNull(12) ? null : reader.GetString(12),
                    StructuredData: reader.IsDBNull(13) ? null : reader.GetString(13),
                    Message: MessageProjection.Truncate(message, query.MessageProjectionLength)));
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"検索がタイムアウト時間 {query.Timeout} を超過した。");
        }
        catch (SqlException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Microsoft.Data.SqlClient は CancellationToken によるキャンセルを OperationCanceledException
            // ではなく SqlException（メッセージ "Operation cancelled by user" 相当。ロケール依存で
            // 翻訳される）として送出する（dotnet/SqlClient の公開 Issue #26・#2424 で maintainer が
            // ".NET Framework と同一の挙動であり、変更予定はない" と明言——確認日 2026-07-05）。
            // 上の catch (OperationCanceledException) 節だけではこのキャンセル経路を捕捉できないため、
            // SqlException 側でも同じタイムアウト条件（timeoutCts が発火し、かつ外部キャンセルではない）
            // を判定し、TimeoutException へ変換する。この節は次の catch (SqlException ex) より
            // 先に評価されるため、キャンセル起因の SqlException が「対話的検索」の恒久障害として
            // 誤分類されることを防ぐ。
            throw new TimeoutException($"検索がタイムアウト時間 {query.Timeout} を超過した。");
        }
        catch (SqlException ex)
        {
            throw ex.ToLogStoreWriteException("対話的検索");
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<LogRecord?> FindByIdAsync(
        long id,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeout.Ticks);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(linkedCts.Token).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT Id, ReceivedAt, SourceAddress, SourcePort, Protocol, ParseStatus,
                       DeviceTimestamp, Facility, Severity, Hostname, AppName, ProcId, MsgId,
                       StructuredData, Message, Raw
                FROM dbo.LogRecords
                WHERE Id = @id;
                """;
            command.Parameters.Add("@id", System.Data.SqlDbType.BigInt).Value = id;

            await using var reader = await command.ExecuteReaderAsync(linkedCts.Token).ConfigureAwait(false);

            if (!await reader.ReadAsync(linkedCts.Token).ConfigureAwait(false))
            {
                return null;
            }

            return new LogRecord(
                Id: reader.GetInt64(0),
                ReceivedAt: DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
                SourceAddress: reader.GetString(2),
                SourcePort: reader.GetInt32(3),
                Protocol: (Protocol)reader.GetInt32(4),
                ParseStatus: (ParseStatus)reader.GetInt32(5),
                DeviceTimestamp: reader.IsDBNull(6) ? null : DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc),
                Facility: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                Severity: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                Hostname: reader.IsDBNull(9) ? null : reader.GetString(9),
                AppName: reader.IsDBNull(10) ? null : reader.GetString(10),
                ProcId: reader.IsDBNull(11) ? null : reader.GetString(11),
                MsgId: reader.IsDBNull(12) ? null : reader.GetString(12),
                StructuredData: reader.IsDBNull(13) ? null : reader.GetString(13),
                Message: reader.IsDBNull(14) ? null : reader.GetString(14),
                Raw: reader.IsDBNull(15) ? null : (byte[])reader.GetValue(15));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"詳細取得がタイムアウト時間 {timeout} を超過した。");
        }
        catch (SqlException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // キャンセルが SqlException として現れる経路（QueryAsync の同型 catch のコメント参照）。
            throw new TimeoutException($"詳細取得がタイムアウト時間 {timeout} を超過した。");
        }
        catch (SqlException ex)
        {
            throw ex.ToLogStoreWriteException("詳細表示の個別取得");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 一括読み出し（IBulkLogReader。database.md §1.2 予約 (a) の実体化。Issue #266）。
    /// 昇順キーセット反復。述語は DB-11 と同じ書き換え形（先頭 conjunct が単独でシーク述語に
    /// なる形）を昇順へ反転して使う。移行・エクスポートは対話検索と異なり深度依存の
    /// レイテンシが問題にならないため FORCESEEK は付与しない。
    /// </remarks>
    public async IAsyncEnumerable<LogRecord> ReadAllAscendingAsync(
        BulkReadCursor? resumeAfter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        const int batchSize = 1000;
        var cursor = resumeAfter;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = new List<LogRecord>(batchSize);

            await using (var connection = new Microsoft.Data.SqlClient.SqlConnection(_connectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                await using var command = connection.CreateCommand();
                var whereSql = cursor is null
                    ? string.Empty
                    : "WHERE (ReceivedAt >= @cursorReceivedAt AND (ReceivedAt > @cursorReceivedAt OR Id > @cursorId))";
                command.CommandText =
                    $"""
                    SELECT TOP (@batchSize) Id, ReceivedAt, SourceAddress, SourcePort, Protocol, ParseStatus,
                           DeviceTimestamp, Facility, Severity, Hostname, AppName, ProcId, MsgId,
                           StructuredData, Message, Raw
                    FROM dbo.LogRecords
                    {whereSql}
                    ORDER BY ReceivedAt ASC, Id ASC;
                    """;
                command.Parameters.Add("@batchSize", System.Data.SqlDbType.Int).Value = batchSize;
                if (cursor is not null)
                {
                    command.Parameters.Add("@cursorReceivedAt", System.Data.SqlDbType.DateTime2).Value =
                        cursor.ReceivedAt.UtcDateTime;
                    command.Parameters.Add("@cursorId", System.Data.SqlDbType.BigInt).Value = cursor.Id;
                }

                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    batch.Add(new LogRecord(
                        Id: reader.GetInt64(0),
                        ReceivedAt: DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
                        SourceAddress: reader.GetString(2),
                        SourcePort: reader.GetInt32(3),
                        Protocol: (Protocol)reader.GetInt32(4),
                        ParseStatus: (ParseStatus)reader.GetInt32(5),
                        DeviceTimestamp: reader.IsDBNull(6) ? null : DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc),
                        Facility: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                        Severity: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                        Hostname: reader.IsDBNull(9) ? null : reader.GetString(9),
                        AppName: reader.IsDBNull(10) ? null : reader.GetString(10),
                        ProcId: reader.IsDBNull(11) ? null : reader.GetString(11),
                        MsgId: reader.IsDBNull(12) ? null : reader.GetString(12),
                        StructuredData: reader.IsDBNull(13) ? null : reader.GetString(13),
                        Message: reader.IsDBNull(14) ? null : reader.GetString(14),
                        Raw: reader.IsDBNull(15) ? null : (byte[])reader.GetValue(15)));
                }
            }

            foreach (var record in batch)
            {
                yield return record;
            }

            if (batch.Count < batchSize)
            {
                yield break;
            }

            var last = batch[^1];
            cursor = new BulkReadCursor(last.ReceivedAt, last.Id!.Value);
        }
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(DateTimeOffset? toInclusive, CancellationToken cancellationToken = default)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        if (toInclusive is { } to)
        {
            command.CommandText = "SELECT COUNT_BIG(*) FROM dbo.LogRecords WHERE ReceivedAt <= @to;";
            command.Parameters.Add("@to", System.Data.SqlDbType.DateTime2).Value = to.UtcDateTime;
        }
        else
        {
            command.CommandText = "SELECT COUNT_BIG(*) FROM dbo.LogRecords;";
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return (long)result!;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SystemEvent>> QuerySystemEventsAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        TimeSpan timeout,
        string? kind = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeout.Ticks);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var results = new List<SystemEvent>();

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(linkedCts.Token).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            var whereClauses = new List<string>();

            // 区間の重なり判定（ILogStore の契約参照）: 範囲に少しでも掛かる区間を返す。
            if (from is { } fromValue)
            {
                whereClauses.Add("EndAt >= @from");
                command.Parameters.Add("@from", System.Data.SqlDbType.DateTime2).Value = fromValue.UtcDateTime;
            }

            if (to is { } toValue)
            {
                whereClauses.Add("StartAt <= @to");
                command.Parameters.Add("@to", System.Data.SqlDbType.DateTime2).Value = toValue.UtcDateTime;
            }

            // 種別の完全一致フィルタ（Issue #150。ILogStore の契約参照）。
            if (kind is not null)
            {
                whereClauses.Add("Kind = @kind");
                command.Parameters.Add("@kind", System.Data.SqlDbType.NVarChar, 255).Value = kind;
            }

            var whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : string.Empty;

            command.CommandText =
                $"""
                SELECT TOP (@limit) Id, Kind, StartAt, EndAt, Approximate, Details
                FROM dbo.SystemEvents
                {whereSql}
                ORDER BY StartAt DESC;
                """;
            command.Parameters.Add("@limit", System.Data.SqlDbType.Int).Value = limit;

            await using var reader = await command.ExecuteReaderAsync(linkedCts.Token).ConfigureAwait(false);

            while (await reader.ReadAsync(linkedCts.Token).ConfigureAwait(false))
            {
                results.Add(new SystemEvent(
                    Id: reader.GetInt64(0),
                    Kind: reader.GetString(1),
                    StartAt: DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
                    EndAt: DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
                    Approximate: reader.GetBoolean(4),
                    Details: reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"システムイベントの読み出しがタイムアウト時間 {timeout} を超過した。");
        }
        catch (SqlException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"システムイベントの読み出しがタイムアウト時間 {timeout} を超過した。");
        }
        catch (SqlException ex)
        {
            throw ex.ToLogStoreWriteException("システムイベントの読み出し");
        }

        return results;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SourceActivity>> QuerySourceActivityAsync(
        int limit,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        // 最終受信時刻の古い順（無音の疑いが強い順。UI-4——ILogStore の契約参照）。
        QuerySourceActivityCoreAsync(limit, timeout, mostRecentFirst: false, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SourceActivity>> QueryMostRecentlyActiveSourcesAsync(
        int limit,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        // 新しい順（候補選択用。Issue #383——打ち切りで切り捨てるのは「古い側」）。
        QuerySourceActivityCoreAsync(limit, timeout, mostRecentFirst: true, cancellationToken);

    private async Task<IReadOnlyList<SourceActivity>> QuerySourceActivityCoreAsync(
        int limit,
        TimeSpan timeout,
        bool mostRecentFirst,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeout.Ticks);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var results = new List<SourceActivity>();

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(linkedCts.Token).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT TOP (@limit) SourceAddress, MAX(ReceivedAt) AS LastReceivedAt, COUNT_BIG(*) AS RecordCount
                FROM dbo.LogRecords
                GROUP BY SourceAddress
                ORDER BY LastReceivedAt {(mostRecentFirst ? "DESC" : "ASC")};
                """;
            command.Parameters.Add("@limit", System.Data.SqlDbType.Int).Value = limit;

            await using var reader = await command.ExecuteReaderAsync(linkedCts.Token).ConfigureAwait(false);

            while (await reader.ReadAsync(linkedCts.Token).ConfigureAwait(false))
            {
                results.Add(new SourceActivity(
                    SourceAddress: reader.GetString(0),
                    LastReceivedAt: DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
                    RecordCount: reader.GetInt64(2)));
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"送信元別集計がタイムアウト時間 {timeout} を超過した。");
        }
        catch (SqlException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"送信元別集計がタイムアウト時間 {timeout} を超過した。");
        }
        catch (SqlException ex)
        {
            throw ex.ToLogStoreWriteException("送信元別の受信状況の集計");
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SeverityCount>> QuerySeverityDistributionAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeout.Ticks);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var results = new List<SeverityCount>();

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(linkedCts.Token).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            // 索引済みの ReceivedAt 範囲へ先に絞り込んでから集計する（ILogStore の契約参照。
            // Issue #145——Severity 列に索引が無いための窓必須化）。
            command.CommandText =
                """
                SELECT Severity, COUNT_BIG(*) AS RecordCount
                FROM dbo.LogRecords
                WHERE ReceivedAt >= @from AND ReceivedAt <= @to
                GROUP BY Severity;
                """;
            command.Parameters.Add("@from", System.Data.SqlDbType.DateTime2).Value = from.UtcDateTime;
            command.Parameters.Add("@to", System.Data.SqlDbType.DateTime2).Value = to.UtcDateTime;

            await using var reader = await command.ExecuteReaderAsync(linkedCts.Token).ConfigureAwait(false);

            while (await reader.ReadAsync(linkedCts.Token).ConfigureAwait(false))
            {
                results.Add(new SeverityCount(
                    Severity: reader.IsDBNull(0) ? null : reader.GetInt32(0),
                    Count: reader.GetInt64(1)));
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"重大度分布の集計がタイムアウト時間 {timeout} を超過した。");
        }
        catch (SqlException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // キャンセルが SqlException として現れる経路（QueryAsync の同型 catch のコメント参照）。
            throw new TimeoutException($"重大度分布の集計がタイムアウト時間 {timeout} を超過した。");
        }
        catch (SqlException ex)
        {
            throw ex.ToLogStoreWriteException("重大度分布の集計");
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SourceActivity>> QueryTopTalkersAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeout.Ticks);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var results = new List<SourceActivity>();

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(linkedCts.Token).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            // 受信量降順（Top talkers。ILogStore の契約参照——QuerySourceActivityAsync とは
            // 逆順の集計）。同数は SourceAddress 昇順で決定的にする。
            command.CommandText =
                """
                SELECT TOP (@limit) SourceAddress, MAX(ReceivedAt) AS LastReceivedAt, COUNT_BIG(*) AS RecordCount
                FROM dbo.LogRecords
                WHERE ReceivedAt >= @from AND ReceivedAt <= @to
                GROUP BY SourceAddress
                ORDER BY RecordCount DESC, SourceAddress ASC;
                """;
            command.Parameters.Add("@from", System.Data.SqlDbType.DateTime2).Value = from.UtcDateTime;
            command.Parameters.Add("@to", System.Data.SqlDbType.DateTime2).Value = to.UtcDateTime;
            command.Parameters.Add("@limit", System.Data.SqlDbType.Int).Value = limit;

            await using var reader = await command.ExecuteReaderAsync(linkedCts.Token).ConfigureAwait(false);

            while (await reader.ReadAsync(linkedCts.Token).ConfigureAwait(false))
            {
                results.Add(new SourceActivity(
                    SourceAddress: reader.GetString(0),
                    LastReceivedAt: DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
                    RecordCount: reader.GetInt64(2)));
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"受信量上位の送信元集計がタイムアウト時間 {timeout} を超過した。");
        }
        catch (SqlException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"受信量上位の送信元集計がタイムアウト時間 {timeout} を超過した。");
        }
        catch (SqlException ex)
        {
            throw ex.ToLogStoreWriteException("受信量上位の送信元集計");
        }

        return results;
    }

    private static string EscapeLikePattern(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is '\\' or '%' or '_' or '[')
            {
                builder.Append('\\');
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <inheritdoc />
    public async Task WriteSystemEventAsync(SystemEvent systemEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(systemEvent);

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO dbo.SystemEvents (Kind, StartAt, EndAt, Approximate, Details)
                VALUES (@kind, @startAt, @endAt, @approximate, @details);
                """;

            command.Parameters.Add("@kind", System.Data.SqlDbType.NVarChar, 255).Value = systemEvent.Kind;
            command.Parameters.Add("@startAt", System.Data.SqlDbType.DateTime2).Value = systemEvent.StartAt.UtcDateTime;
            command.Parameters.Add("@endAt", System.Data.SqlDbType.DateTime2).Value = systemEvent.EndAt.UtcDateTime;
            command.Parameters.Add("@approximate", System.Data.SqlDbType.Bit).Value = systemEvent.Approximate;
            command.Parameters.Add("@details", System.Data.SqlDbType.NVarChar, -1).Value = (object?)systemEvent.Details ?? DBNull.Value;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            throw ex.ToLogStoreWriteException("システムイベントの書き込み");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 分割実行（database.md §3）: <see cref="RetentionConstants.DeleteBatchMaxSize"/> 件ずつ
    /// <c>DELETE TOP (n)</c>（SQL Server 固有構文。SQLite の副問い合わせ形と等価な分割削除）を
    /// 繰り返し実行する。
    /// </remarks>
    public async Task<DeleteOlderThanResult> DeleteOlderThanAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        long totalDeleted = 0;
        var cutoffUtc = cutoff.UtcDateTime;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    DELETE TOP (@batchSize) FROM dbo.LogRecords
                    WHERE ReceivedAt < @cutoff;
                    """;
                command.Parameters.Add("@cutoff", System.Data.SqlDbType.DateTime2).Value = cutoffUtc;
                command.Parameters.Add("@batchSize", System.Data.SqlDbType.Int).Value = RetentionConstants.DeleteBatchMaxSize;

                var deletedInBatch = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                totalDeleted += deletedInBatch;

                if (deletedInBatch < RetentionConstants.DeleteBatchMaxSize)
                {
                    break;
                }
            }
        }
        catch (SqlException ex)
        {
            throw ex.ToLogStoreWriteException($"保持期間削除 (cutoff={cutoffUtc:O})");
        }

        return new DeleteOlderThanResult(totalDeleted, cutoff);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>SQL Server provider には「取得不能」の逃げ道を適用しない</b>（database.md §5.3）:
    /// DB サイズは常に値を返す（取得自体に失敗した場合は例外として扱い、統計取得全体を失敗させる）。
    /// </para>
    /// <para>
    /// <b>計測対象は割当ファイルサイズ</b>（<c>sys.database_files.size</c>。8-KB ページ単位。
    /// Microsoft Learn "sys.database_files (Transact-SQL)" の記載——確認日 2026-07-05:
    /// "Current size of the file, in 8-KB pages"）。行データ・ログファイルの合計を返す。
    /// <b>DB-4 の実機検証で未確定の点</b>: 削除後にこの値が縮小するか（自動 shrink は既定で
    /// 行われないため、削除後も割当サイズは維持され続ける可能性が高い——実機確認は DB-4 に委ねる）。
    /// 使用ページ量（<c>FILEPROPERTY(name, 'SpaceUsed')</c>）との差分は「解放可能だが未解放」の
    /// 容量として別途観測できるが、v0.1 時点では割当サイズのみを <see cref="LogStoreStatistics.DatabaseSizeBytes"/>
    /// として返す（DB-4 で警告閾値・残り日数換算とあわせて設計を確定する）。
    /// </para>
    /// <para>
    /// <b>Express エディション検出</b>（database.md §5.3 の必須要件）:
    /// <c>SERVERPROPERTY('EngineEdition')</c> が <c>4</c>（Express。Microsoft Learn
    /// "SERVERPROPERTY (Transact-SQL)" の EngineEdition テーブル——確認日 2026-07-05:
    /// "4 = Express (For Express, Express with Tools, and Express with Advanced Services)"）の
    /// 場合、<see cref="LogStoreStatistics.DatabaseSizeBytes"/> と
    /// <see cref="ExpressMaxDatabaseSizeBytes"/>（10 GB。database.md §5.3 出典 Microsoft Learn
    /// "Editions and supported features of SQL Server 2022"）から利用者が接近を判定できる。
    /// 接近警告そのもの（閾値・能動通知への配線）は architecture.md §4.6 の経路——本メソッドは
    /// 判定に必要な生データ（サイズ・上限）を提供するところまでを担う。
    /// </para>
    /// </remarks>
    public async Task<LogStoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            long recordCount;
            await using (var countCommand = connection.CreateCommand())
            {
                countCommand.CommandText = "SELECT COUNT_BIG(*) FROM dbo.LogRecords;";
                recordCount = (long)(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            }

            long databaseSizeBytes;
            await using (var sizeCommand = connection.CreateCommand())
            {
                // sys.database_files.size は 8-KB ページ単位（Microsoft Learn 確認日 2026-07-05）。
                sizeCommand.CommandText = "SELECT SUM(CAST(size AS BIGINT)) * 8192 FROM sys.database_files;";
                var sizeResult = await sizeCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                databaseSizeBytes = sizeResult is null or DBNull ? 0 : Convert.ToInt64(sizeResult, CultureInfo.InvariantCulture);
            }

            return new LogStoreStatistics(
                RecordCount: recordCount,
                DatabaseSizeBytes: databaseSizeBytes,
                DatabaseSizeUnavailableReason: null,
                WalSizeBytes: null);
        }
        catch (SqlException ex)
        {
            throw ex.ToLogStoreWriteException("統計情報の取得");
        }
    }

    /// <summary>
    /// SQL Server Express の DB 最大サイズ（database.md §5.3。Microsoft Learn
    /// "Editions and supported features of SQL Server 2022" の記載。確認日 2026-07-04）。
    /// </summary>
    public const long ExpressMaxDatabaseSizeBytes = 10L * 1024 * 1024 * 1024;

    /// <summary>
    /// 接続先インスタンスが SQL Server Express（Express with Tools / Express with Advanced Services
    /// を含む）かどうかを判定する（database.md §5.3 の必須要件）。
    /// </summary>
    /// <remarks>
    /// <c>SERVERPROPERTY('EngineEdition')</c> を用いる（確認日 2026-07-05。<see cref="GetStatisticsAsync"/>
    /// のドキュメント参照）。LocalDB は SQL Server Express の一種として配布される実行形態だが、
    /// <c>EngineEdition</c> は LocalDB でも <c>4</c>（Express）を返す（LocalDB は Express の
    /// インストール不要な変種であり、エンジン自体は同一——本判定はテスト環境の LocalDB でも
    /// Express と同じ 10 GB 上限の警告対象として扱われることを意味する。これは安全側であり、
    /// LocalDB を本番相当として扱う縮退は許容できる）。
    /// </remarks>
    public async Task<bool> IsExpressEditionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT SERVERPROPERTY('EngineEdition');";
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return result is not (null or DBNull) && Convert.ToInt32(result, CultureInfo.InvariantCulture) == EngineEditionExpress;
        }
        catch (SqlException ex)
        {
            throw ex.ToLogStoreWriteException("エディション判別");
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // Microsoft.Data.SqlClient も既定で接続プーリングを行う。SqliteLogStore と平行に、
        // 明示的な破棄経路を用意する（テスト・退避処理でのプール解放を安全にするため）。
        // ClearPool は未オープンの SqlConnection インスタンスを鍵として渡せば足りるが、
        // そのインスタンス自体は使い捨てのため確実に破棄する（using で漏れを防ぐ）。
        using var connection = new SqlConnection(_connectionString);
        SqlConnection.ClearPool(connection);
        return ValueTask.CompletedTask;
    }
}
