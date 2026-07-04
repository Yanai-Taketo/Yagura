using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Yagura.Storage.Sqlite;

/// <summary>
/// <see cref="ILogStore"/> の SQLite 実装（database.md §4 組み込み provider）。
/// </summary>
/// <remarks>
/// <para>
/// WAL モードを使用する。WAL では読み取りは書き込みをブロックせず、書き込みも
/// 読み取りをブロックしないが、writer は同時に 1 つである
/// （SQLite 公式ドキュメント "Write-Ahead Logging" の記載。確認日 2026-07-04）。
/// <see cref="WriteBatchAsync"/> の呼び出しを直列化する契約は <see cref="ILogStore"/>
/// のドキュメントを参照。
/// </para>
/// <para>
/// 接続は操作ごとに開閉する（Microsoft.Data.Sqlite の既定の接続プーリングにより
/// 実コストは小さい）。読み取りと書き込みが別接続になることで、WAL の
/// 「読み書きが互いをブロックしない」性質が ADO.NET 層でも成立する。
/// </para>
/// </remarks>
public sealed class SqliteLogStore : ILogStore, IAsyncDisposable
{
    private readonly string _connectionString;

    /// <summary>
    /// 指定したデータベースファイルパスで <see cref="SqliteLogStore"/> を構築する。
    /// </summary>
    /// <param name="databasePath">SQLite データベースファイルのパス。</param>
    public SqliteLogStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

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

        await using var command = connection.CreateCommand();
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
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        if (records.Count == 0)
        {
            return;
        }

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

    /// <inheritdoc />
    public async Task<IReadOnlyList<LogRecordSummary>> QueryLatestAsync(
        int limit,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeout.Ticks);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        await using var connection = new SqliteConnection(_connectionString);

        var results = new List<LogRecordSummary>(limit);

        try
        {
            await connection.OpenAsync(linkedCts.Token).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT Id, ReceivedAt, SourceAddress, SourcePort, Protocol, ParseStatus,
                       DeviceTimestamp, Facility, Severity, Hostname, AppName, ProcId, MsgId, Message
                FROM LogRecords
                ORDER BY ReceivedAt DESC
                LIMIT $limit;
                """;
            command.Parameters.Add("$limit", SqliteType.Integer).Value = limit;

            await using var reader = await command.ExecuteReaderAsync(linkedCts.Token).ConfigureAwait(false);

            while (await reader.ReadAsync(linkedCts.Token).ConfigureAwait(false))
            {
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
                    Message: reader.IsDBNull(13) ? null : reader.GetString(13)));
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"QueryLatestAsync がタイムアウト時間 {timeout} を超過した。");
        }

        return results;
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
