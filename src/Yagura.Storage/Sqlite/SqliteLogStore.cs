using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Yagura.Storage.Sqlite;

/// <summary>
/// <see cref="ILogStore"/> の SQLite 実装（database.md §4 組み込み provider）。
/// </summary>
/// <remarks>
/// <para>
/// <b>読み書き分離の性質（database.md §1.2 契約表 末尾・§1.3 の文書化義務）</b>: WAL モードを
/// 使用する。WAL では読み取りは書き込みをブロックせず、書き込みも読み取りをブロックしないが、
/// writer は同時に 1 つである（SQLite 公式ドキュメント "Write-Ahead Logging" の記載。
/// 確認日 2026-07-04）。<see cref="WriteBatchAsync"/> の呼び出しを直列化する契約は
/// <see cref="ILogStore"/> のドキュメントを参照。
/// </para>
/// <para>
/// <b>付随する運用特性 — WAL ファイルの肥大</b>（database.md §4）: 読み取りが常に重なり続けると
/// checkpoint が完了できず、WAL ファイルが際限なく成長し得る（checkpoint starvation。
/// SQLite 公式ドキュメント "Write-Ahead Logging" §6 の記載。確認日 2026-07-04）。対話的検索の
/// タイムアウト（<see cref="LogQuery.Timeout"/>）が個々の読者の上限時間を画す。WAL ファイル
/// サイズは <see cref="GetStatisticsAsync"/> が返す <see cref="LogStoreStatistics.WalSizeBytes"/>
/// で観測できる（architecture.md §4.6 のゲージの入力）。
/// </para>
/// <para>
/// 接続は操作ごとに開閉する（Microsoft.Data.Sqlite の既定の接続プーリングにより
/// 実コストは小さい）。読み取りと書き込みが別接続になることで、WAL の
/// 「読み書きが互いをブロックしない」性質が ADO.NET 層でも成立する。
/// </para>
/// </remarks>
public sealed class SqliteLogStore : ILogStore, IAsyncDisposable
{
    /// <summary>
    /// 現行のスキーマバージョン（database.md §1.2 契約 1「スキーマ管理」）。
    /// </summary>
    internal const int CurrentSchemaVersion = 1;

    private readonly string _databasePath;
    private readonly string _connectionString;

    /// <summary>
    /// 指定したデータベースファイルパスで <see cref="SqliteLogStore"/> を構築する。
    /// </summary>
    /// <param name="databasePath">SQLite データベースファイルのパス。</param>
    public SqliteLogStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _databasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>スキーマ版間移行の土台（database.md §1.2 契約 1・M5-1）</b>: <c>SchemaVersion</c> 表に
    /// 現在適用済みのバージョンを 1 行だけ保持する。<see cref="InitializeAsync"/> は
    /// (1) 表がなければ v1 相当のスキーマを新規作成して <c>SchemaVersion = 1</c> を書き込み、
    /// (2) 表があれば記録済みバージョンを読み、<see cref="CurrentSchemaVersion"/> より低ければ
    /// 移行スクリプトを順に適用する（v0.1 時点では v1 のみのため移行スクリプトは存在しない）、
    /// という 2 分岐で冪等性を実現する——「既に適用済みなら何もしない」は
    /// 「記録済みバージョン == 現行バージョン」の等値判定で成立する。
    /// </para>
    /// <para>
    /// <b>権限不足の区別可能な報告（database.md §5.2）は SQLite では実質発生しない</b>:
    /// SQLite はファイルシステム上の単一ファイルであり、「ログイン作成・権限付与」という
    /// 区別可能な権限体系を持たない。ファイル ACL 不足で失敗した場合は
    /// <see cref="SchemaPermissionException"/> ではなく <see cref="LogStoreWriteException"/>
    /// （<see cref="LogStoreFailureKind.Permanent"/>）として報告する——インターフェース上の
    /// 契約（<see cref="SchemaPermissionException"/> 型自体）は SQL Server 実装（M5-3）での
    /// 実体化を待つ。
    /// </para>
    /// </remarks>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            throw ex.ToLogStoreWriteException("スキーマ初期化のための接続");
        }

        // WAL モードはデータベースファイルに永続化されるため、ここで一度設定すれば
        // 以後の接続には引き継がれる。接続ごとの再設定は不要
        // （SQLite 公式ドキュメント "Write-Ahead Logging"（www.sqlite.org/wal.html）:
        // "Unlike the other journaling modes, PRAGMA journal_mode=WAL is persistent.
        // If a process sets WAL mode, then closes and reopens the database, the
        // database will come back in WAL mode." 確認日 2026-07-05）。
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    CREATE TABLE IF NOT EXISTS LogRecords (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ReceivedAt TEXT NOT NULL,
                        SourceAddress TEXT NOT NULL,
                        SourcePort INTEGER NOT NULL,
                        Protocol INTEGER NOT NULL,
                        DeviceTimestamp TEXT NULL,
                        Facility INTEGER NULL,
                        Severity INTEGER NULL,
                        Hostname TEXT NULL,
                        AppName TEXT NULL,
                        ProcId TEXT NULL,
                        MsgId TEXT NULL,
                        StructuredData TEXT NULL,
                        Message TEXT NULL,
                        Raw BLOB NULL,
                        ParseStatus INTEGER NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS IX_LogRecords_ReceivedAt ON LogRecords (ReceivedAt);

                    CREATE TABLE IF NOT EXISTS SystemEvents (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Kind TEXT NOT NULL,
                        StartAt TEXT NOT NULL,
                        EndAt TEXT NOT NULL,
                        Approximate INTEGER NOT NULL,
                        Details TEXT NULL
                    );

                    CREATE INDEX IF NOT EXISTS IX_SystemEvents_StartAt ON SystemEvents (StartAt);

                    CREATE TABLE IF NOT EXISTS SchemaVersion (
                        Id INTEGER PRIMARY KEY CHECK (Id = 1),
                        Version INTEGER NOT NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var recordedVersion = await ReadSchemaVersionAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);

            if (recordedVersion is null)
            {
                // 初回作成（このデータベースファイルが今回新規に作られた、または
                // SchemaVersion 表が存在しない過去バージョンから引き継いだ）。
                // 現行バージョンをそのまま記録する——v0.1 時点で唯一のバージョンが v1 のため、
                // 「移行」ではなく「新規作成の完了記録」として書き込む。
                await using var insertVersion = connection.CreateCommand();
                insertVersion.Transaction = transaction;
                insertVersion.CommandText = "INSERT INTO SchemaVersion (Id, Version) VALUES (1, $version);";
                insertVersion.Parameters.AddWithValue("$version", CurrentSchemaVersion);
                await insertVersion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (recordedVersion.Value < CurrentSchemaVersion)
            {
                // 移行フレーム: v0.1 時点では適用すべき移行スクリプトが存在しない
                // （CurrentSchemaVersion = 1 が唯一のバージョンのため、この分岐には入らない）。
                // 将来のバージョン追加時は、ここに recordedVersion から CurrentSchemaVersion までの
                // 移行ステップを順に適用し、最後に SchemaVersion.Version を更新する
                // （適用済み移行の再適用を避ける冪等性は、この version 比較そのものが担保する）。
                await ApplyMigrationsAsync(connection, transaction, recordedVersion.Value, cancellationToken)
                    .ConfigureAwait(false);
            }

            // recordedVersion.Value == CurrentSchemaVersion の場合: 既に適用済みのため何もしない
            // （冪等性——database.md §1.2「既に適用済みなら何もしない」）。

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            throw ex.ToLogStoreWriteException("スキーマ初期化");
        }
    }

    private static async Task<int?> ReadSchemaVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Version FROM SchemaVersion WHERE Id = 1;";

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// スキーマ版間移行の適用点（database.md §1.2 契約 1）。v0.1 時点では
    /// <see cref="CurrentSchemaVersion"/> が唯一のバージョン（1）であるため、本メソッドは
    /// 到達しない土台のみである。将来バージョンを追加する際は
    /// <c>fromVersion</c> から <see cref="CurrentSchemaVersion"/> までの移行ステップを
    /// ここに追加し、各ステップ適用後に SchemaVersion.Version を更新する。
    /// </summary>
    private static Task ApplyMigrationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int fromVersion,
        CancellationToken cancellationToken)
    {
        // 現時点で適用すべき移行スクリプトは存在しない（CurrentSchemaVersion = 1 が唯一）。
        throw new NotSupportedException(
            $"スキーマバージョン {fromVersion} から {CurrentSchemaVersion} への移行スクリプトは未定義です。");
    }

    /// <inheritdoc />
    public async Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        if (records.Count == 0)
        {
            return;
        }

        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO LogRecords
                    (ReceivedAt, SourceAddress, SourcePort, Protocol, DeviceTimestamp,
                     Facility, Severity, Hostname, AppName, ProcId, MsgId,
                     StructuredData, Message, Raw, ParseStatus)
                VALUES
                    ($receivedAt, $sourceAddress, $sourcePort, $protocol, $deviceTimestamp,
                     $facility, $severity, $hostname, $appName, $procId, $msgId,
                     $structuredData, $message, $raw, $parseStatus);
                """;

            var receivedAt = command.Parameters.Add("$receivedAt", SqliteType.Text);
            var sourceAddress = command.Parameters.Add("$sourceAddress", SqliteType.Text);
            var sourcePort = command.Parameters.Add("$sourcePort", SqliteType.Integer);
            var protocol = command.Parameters.Add("$protocol", SqliteType.Integer);
            var deviceTimestamp = command.Parameters.Add("$deviceTimestamp", SqliteType.Text);
            var facility = command.Parameters.Add("$facility", SqliteType.Integer);
            var severity = command.Parameters.Add("$severity", SqliteType.Integer);
            var hostname = command.Parameters.Add("$hostname", SqliteType.Text);
            var appName = command.Parameters.Add("$appName", SqliteType.Text);
            var procId = command.Parameters.Add("$procId", SqliteType.Text);
            var msgId = command.Parameters.Add("$msgId", SqliteType.Text);
            var structuredData = command.Parameters.Add("$structuredData", SqliteType.Text);
            var message = command.Parameters.Add("$message", SqliteType.Text);
            var raw = command.Parameters.Add("$raw", SqliteType.Blob);
            var parseStatus = command.Parameters.Add("$parseStatus", SqliteType.Integer);

            foreach (var record in records)
            {
                receivedAt.Value = record.ReceivedAt.UtcDateTime.ToString("O");
                sourceAddress.Value = record.SourceAddress;
                sourcePort.Value = record.SourcePort;
                protocol.Value = (int)record.Protocol;
                deviceTimestamp.Value = (object?)record.DeviceTimestamp?.UtcDateTime.ToString("O") ?? DBNull.Value;
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
        catch (SqliteException ex)
        {
            throw ex.ToLogStoreWriteException($"ログレコードのバッチ書き込み ({records.Count} 件)");
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

        await using var connection = new SqliteConnection(_connectionString);

        var results = new List<LogRecordSummary>(query.Limit);

        try
        {
            await connection.OpenAsync(linkedCts.Token).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            var whereClauses = new List<string>();

            if (query.ReceivedAtFrom is { } from)
            {
                whereClauses.Add("ReceivedAt >= $receivedAtFrom");
                command.Parameters.Add("$receivedAtFrom", SqliteType.Text).Value = from.UtcDateTime.ToString("O");
            }

            if (query.ReceivedAtTo is { } to)
            {
                whereClauses.Add("ReceivedAt <= $receivedAtTo");
                command.Parameters.Add("$receivedAtTo", SqliteType.Text).Value = to.UtcDateTime.ToString("O");
            }

            if (query.SourceAddress is { } sourceAddress)
            {
                whereClauses.Add("SourceAddress = $sourceAddress");
                command.Parameters.Add("$sourceAddress", SqliteType.Text).Value = sourceAddress;
            }

            if (query.SeverityAtMost is { } severityAtMost)
            {
                // 閾値方式（Severity <= N。LogQuery.SeverityAtMost の doc コメント参照——
                // syslog は数値が小さいほど深刻なため「N 以上の重大度」は「Severity <= N」になる。
                // Severity が NULL（PRI 未解析）の行は比較が unknown になり自然に対象外となる。
                whereClauses.Add("Severity <= $severityAtMost");
                command.Parameters.Add("$severityAtMost", SqliteType.Integer).Value = severityAtMost;
            }

            if (query.Facility is { } facilityFilter)
            {
                whereClauses.Add("Facility = $facility");
                command.Parameters.Add("$facility", SqliteType.Integer).Value = facilityFilter;
            }

            if (query.ParseStatus is { } parseStatusFilter)
            {
                whereClauses.Add("ParseStatus = $parseStatus");
                command.Parameters.Add("$parseStatus", SqliteType.Integer).Value = (int)parseStatusFilter;
            }

            if (query.SearchText is { Length: > 0 } searchText)
            {
                // 自由文検索: Message に対する部分一致・大文字小文字を区別しない
                // （DB-6 確定までの暫定規則。LogQuery のドキュメント参照）。
                // SQLite の LIKE は既定で ASCII の大文字小文字を区別しない
                // （公式ドキュメント "The LIKE operator" — www.sqlite.org/lang_expr.html:
                // "the LIKE operator is case sensitive by default for unicode characters that
                // are beyond the ASCII range... the operator is not case sensitive... for ASCII
                // characters"。確認日 2026-07-05）。ワイルドカード文字 %・_ はエスケープする。
                whereClauses.Add("Message LIKE $searchText ESCAPE '\\'");
                command.Parameters.Add("$searchText", SqliteType.Text).Value = "%" + EscapeLikePattern(searchText) + "%";
            }

            var whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : string.Empty;

            // Id DESC のタイブレーク（Issue #144）: ReceivedAt 単独では同一時刻（同一ミリ秒）の
            // 行の相対順序が SQL 上未定義になる——UDP バースト・スタックトレースの分割送信等、
            // syslog では同一時刻多発が日常的に起きる。Id は採番順（挿入順）と一致するため、
            // 同時刻内は「新しく挿入された行が先」という決定的な順序になる。
            command.CommandText =
                $"""
                SELECT Id, ReceivedAt, SourceAddress, SourcePort, Protocol, ParseStatus,
                       DeviceTimestamp, Facility, Severity, Hostname, AppName, ProcId, MsgId,
                       StructuredData, Message
                FROM LogRecords
                {whereSql}
                ORDER BY ReceivedAt DESC, Id DESC
                LIMIT $limit;
                """;
            command.Parameters.Add("$limit", SqliteType.Integer).Value = query.Limit;

            await using var reader = await command.ExecuteReaderAsync(linkedCts.Token).ConfigureAwait(false);

            while (await reader.ReadAsync(linkedCts.Token).ConfigureAwait(false))
            {
                var message = reader.IsDBNull(14) ? null : reader.GetString(14);
                results.Add(new LogRecordSummary(
                    Id: reader.GetInt64(0),
                    ReceivedAt: ParseUtcTimestamp(reader.GetString(1)),
                    SourceAddress: reader.GetString(2),
                    SourcePort: reader.GetInt32(3),
                    Protocol: (Protocol)reader.GetInt32(4),
                    ParseStatus: (ParseStatus)reader.GetInt32(5),
                    DeviceTimestamp: reader.IsDBNull(6) ? null : ParseUtcTimestamp(reader.GetString(6)),
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
        catch (SqliteException ex)
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
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(linkedCts.Token).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT Id, ReceivedAt, SourceAddress, SourcePort, Protocol, ParseStatus,
                       DeviceTimestamp, Facility, Severity, Hostname, AppName, ProcId, MsgId,
                       StructuredData, Message, Raw
                FROM LogRecords
                WHERE Id = $id;
                """;
            command.Parameters.Add("$id", SqliteType.Integer).Value = id;

            await using var reader = await command.ExecuteReaderAsync(linkedCts.Token).ConfigureAwait(false);

            if (!await reader.ReadAsync(linkedCts.Token).ConfigureAwait(false))
            {
                return null;
            }

            return new LogRecord(
                Id: reader.GetInt64(0),
                ReceivedAt: ParseUtcTimestamp(reader.GetString(1)),
                SourceAddress: reader.GetString(2),
                SourcePort: reader.GetInt32(3),
                Protocol: (Protocol)reader.GetInt32(4),
                ParseStatus: (ParseStatus)reader.GetInt32(5),
                DeviceTimestamp: reader.IsDBNull(6) ? null : ParseUtcTimestamp(reader.GetString(6)),
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
        catch (SqliteException ex)
        {
            throw ex.ToLogStoreWriteException("詳細表示の個別取得");
        }
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
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(linkedCts.Token).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            var whereClauses = new List<string>();

            // 区間の重なり判定（ILogStore の契約参照）: 範囲に少しでも掛かる区間を返す。
            if (from is { } fromValue)
            {
                whereClauses.Add("EndAt >= $from");
                command.Parameters.Add("$from", SqliteType.Text).Value = fromValue.UtcDateTime.ToString("O");
            }

            if (to is { } toValue)
            {
                whereClauses.Add("StartAt <= $to");
                command.Parameters.Add("$to", SqliteType.Text).Value = toValue.UtcDateTime.ToString("O");
            }

            // 種別の完全一致フィルタ（Issue #150。ILogStore の契約参照）。
            if (kind is not null)
            {
                whereClauses.Add("Kind = $kind");
                command.Parameters.Add("$kind", SqliteType.Text).Value = kind;
            }

            var whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : string.Empty;

            command.CommandText =
                $"""
                SELECT Id, Kind, StartAt, EndAt, Approximate, Details
                FROM SystemEvents
                {whereSql}
                ORDER BY StartAt DESC
                LIMIT $limit;
                """;
            command.Parameters.Add("$limit", SqliteType.Integer).Value = limit;

            await using var reader = await command.ExecuteReaderAsync(linkedCts.Token).ConfigureAwait(false);

            while (await reader.ReadAsync(linkedCts.Token).ConfigureAwait(false))
            {
                results.Add(new SystemEvent(
                    Id: reader.GetInt64(0),
                    Kind: reader.GetString(1),
                    StartAt: ParseUtcTimestamp(reader.GetString(2)),
                    EndAt: ParseUtcTimestamp(reader.GetString(3)),
                    Approximate: reader.GetInt32(4) != 0,
                    Details: reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"システムイベントの読み出しがタイムアウト時間 {timeout} を超過した。");
        }
        catch (SqliteException ex)
        {
            throw ex.ToLogStoreWriteException("システムイベントの読み出し");
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SourceActivity>> QuerySourceActivityAsync(
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
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(linkedCts.Token).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            // 最終受信時刻の古い順（無音の疑いが強い順。UI-4——ILogStore の契約参照）。
            command.CommandText =
                """
                SELECT SourceAddress, MAX(ReceivedAt) AS LastReceivedAt, COUNT(*) AS RecordCount
                FROM LogRecords
                GROUP BY SourceAddress
                ORDER BY LastReceivedAt ASC
                LIMIT $limit;
                """;
            command.Parameters.Add("$limit", SqliteType.Integer).Value = limit;

            await using var reader = await command.ExecuteReaderAsync(linkedCts.Token).ConfigureAwait(false);

            while (await reader.ReadAsync(linkedCts.Token).ConfigureAwait(false))
            {
                results.Add(new SourceActivity(
                    SourceAddress: reader.GetString(0),
                    LastReceivedAt: ParseUtcTimestamp(reader.GetString(1)),
                    RecordCount: reader.GetInt64(2)));
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"送信元別集計がタイムアウト時間 {timeout} を超過した。");
        }
        catch (SqliteException ex)
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
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(linkedCts.Token).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            // 索引済みの ReceivedAt 範囲へ先に絞り込んでから集計する（ILogStore の契約参照。
            // Issue #145——Severity 列に索引が無いための窓必須化）。
            command.CommandText =
                """
                SELECT Severity, COUNT(*) AS RecordCount
                FROM LogRecords
                WHERE ReceivedAt >= $from AND ReceivedAt <= $to
                GROUP BY Severity;
                """;
            command.Parameters.Add("$from", SqliteType.Text).Value = from.UtcDateTime.ToString("O");
            command.Parameters.Add("$to", SqliteType.Text).Value = to.UtcDateTime.ToString("O");

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
        catch (SqliteException ex)
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
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(linkedCts.Token).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            // 受信量降順（Top talkers。ILogStore の契約参照——QuerySourceActivityAsync とは
            // 逆順の集計）。同数は SourceAddress 昇順で決定的にする。
            command.CommandText =
                """
                SELECT SourceAddress, MAX(ReceivedAt) AS LastReceivedAt, COUNT(*) AS RecordCount
                FROM LogRecords
                WHERE ReceivedAt >= $from AND ReceivedAt <= $to
                GROUP BY SourceAddress
                ORDER BY RecordCount DESC, SourceAddress ASC
                LIMIT $limit;
                """;
            command.Parameters.Add("$from", SqliteType.Text).Value = from.UtcDateTime.ToString("O");
            command.Parameters.Add("$to", SqliteType.Text).Value = to.UtcDateTime.ToString("O");
            command.Parameters.Add("$limit", SqliteType.Integer).Value = limit;

            await using var reader = await command.ExecuteReaderAsync(linkedCts.Token).ConfigureAwait(false);

            while (await reader.ReadAsync(linkedCts.Token).ConfigureAwait(false))
            {
                results.Add(new SourceActivity(
                    SourceAddress: reader.GetString(0),
                    LastReceivedAt: ParseUtcTimestamp(reader.GetString(1)),
                    RecordCount: reader.GetInt64(2)));
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"受信量上位の送信元集計がタイムアウト時間 {timeout} を超過した。");
        }
        catch (SqliteException ex)
        {
            throw ex.ToLogStoreWriteException("受信量上位の送信元集計");
        }

        return results;
    }

    private static string EscapeLikePattern(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is '\\' or '%' or '_')
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
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO SystemEvents (Kind, StartAt, EndAt, Approximate, Details)
                VALUES ($kind, $startAt, $endAt, $approximate, $details);
                """;

            command.Parameters.Add("$kind", SqliteType.Text).Value = systemEvent.Kind;
            command.Parameters.Add("$startAt", SqliteType.Text).Value = systemEvent.StartAt.UtcDateTime.ToString("O");
            command.Parameters.Add("$endAt", SqliteType.Text).Value = systemEvent.EndAt.UtcDateTime.ToString("O");
            command.Parameters.Add("$approximate", SqliteType.Integer).Value = systemEvent.Approximate ? 1 : 0;
            command.Parameters.Add("$details", SqliteType.Text).Value = (object?)systemEvent.Details ?? DBNull.Value;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            throw ex.ToLogStoreWriteException("システムイベントの書き込み");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 分割実行（database.md §3）: <see cref="RetentionConstants.DeleteBatchMaxSize"/> 件ずつ
    /// <c>DELETE ... LIMIT</c> を繰り返し、削除対象が尽きるまで続ける。SQLite の <c>DELETE</c> 文の
    /// <c>LIMIT</c> 句は既定ビルドでは無効（<c>SQLITE_ENABLE_UPDATE_DELETE_LIMIT</c> コンパイル
    /// オプションが必要——SQLite 公式ドキュメント "The UPDATE ... LIMIT ... The DELETE ... LIMIT"
    /// (www.sqlite.org/lang_delete.html) の記載。確認日 2026-07-05。Microsoft.Data.Sqlite が
    /// 同梱する SQLitePCLRaw.lib.e_sqlite3 のビルドはこのオプションを有効化していない）ため、
    /// <c>rowid IN (SELECT rowid FROM LogRecords WHERE ... LIMIT n)</c> の副問い合わせ形で
    /// 同じ効果を得る。
    /// </remarks>
    public async Task<DeleteOlderThanResult> DeleteOlderThanAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        long totalDeleted = 0;
        var cutoffText = cutoff.UtcDateTime.ToString("O");

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    DELETE FROM LogRecords
                    WHERE Id IN (
                        SELECT Id FROM LogRecords
                        WHERE ReceivedAt < $cutoff
                        LIMIT $batchSize
                    );
                    """;
                command.Parameters.Add("$cutoff", SqliteType.Text).Value = cutoffText;
                command.Parameters.Add("$batchSize", SqliteType.Integer).Value = RetentionConstants.DeleteBatchMaxSize;

                var deletedInBatch = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                totalDeleted += deletedInBatch;

                if (deletedInBatch < RetentionConstants.DeleteBatchMaxSize)
                {
                    // 削除対象が尽きた（このバッチで上限未満しか削除できなかった = 残りがない）。
                    break;
                }
            }
        }
        catch (SqliteException ex)
        {
            throw ex.ToLogStoreWriteException($"保持期間削除 (cutoff={cutoffText})");
        }

        return new DeleteOlderThanResult(totalDeleted, cutoff);
    }

    /// <inheritdoc />
    /// <remarks>
    /// DB サイズは <c>PRAGMA page_count</c> * <c>PRAGMA page_size</c> で算出する
    /// （SQLite 公式ドキュメント "PRAGMA page_count" (www.sqlite.org/pragma.html#pragma_page_count):
    /// "the total number of pages in the database file" の記載。確認日 2026-07-05。
    /// メインの DB ファイルサイズであり、WAL ファイルは含まない——WAL は
    /// <see cref="LogStoreStatistics.WalSizeBytes"/> で別途返す）。
    /// </remarks>
    public async Task<LogStoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            long recordCount;
            await using (var countCommand = connection.CreateCommand())
            {
                countCommand.CommandText = "SELECT COUNT(*) FROM LogRecords;";
                recordCount = (long)(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            }

            long pageCount;
            await using (var pageCountCommand = connection.CreateCommand())
            {
                pageCountCommand.CommandText = "PRAGMA page_count;";
                pageCount = Convert.ToInt64(
                    await pageCountCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
            }

            long pageSize;
            await using (var pageSizeCommand = connection.CreateCommand())
            {
                pageSizeCommand.CommandText = "PRAGMA page_size;";
                pageSize = Convert.ToInt64(
                    await pageSizeCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
            }

            var databaseSizeBytes = pageCount * pageSize;

            long? walSizeBytes = null;
            var walPath = _databasePath + "-wal";
            try
            {
                if (File.Exists(walPath))
                {
                    walSizeBytes = new FileInfo(walPath).Length;
                }
            }
            catch (IOException)
            {
                // WAL サイズは観測用の付随情報であり、取得できなくても統計全体を失敗させない
                // （§4 の WAL 肥大監視は本来 null であってもゲージ側で「取得不能」を扱える設計とする）。
                walSizeBytes = null;
            }

            return new LogStoreStatistics(
                RecordCount: recordCount,
                DatabaseSizeBytes: databaseSizeBytes,
                DatabaseSizeUnavailableReason: null,
                WalSizeBytes: walSizeBytes);
        }
        catch (SqliteException ex)
        {
            throw ex.ToLogStoreWriteException("統計情報の取得");
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // Microsoft.Data.Sqlite は既定でネイティブ接続をプールするため、接続の Dispose 後も
        // OS レベルのファイルハンドルが保持され得る。呼び出し側がすぐにファイル削除・移動を
        // 行うケース（テスト・退避処理等）を安全にするため、明示的にプールを破棄する。
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);

        return ValueTask.CompletedTask;
    }

    private static DateTimeOffset ParseUtcTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
