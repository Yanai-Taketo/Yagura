using System.Globalization;
using Microsoft.Data.Sqlite;
using Yagura.Storage;

namespace Yagura.Host.Administration.OldDatabaseDisposal;

/// <summary>旧ファイルの要約（件数・期間・スキーマ）。</summary>
public sealed record OldDatabaseSummary(
    bool HasLogTable,
    int? SchemaVersion,
    long RecordCount,
    DateTimeOffset? OldestReceivedAt,
    DateTimeOffset? NewestReceivedAt,
    long SystemEventCount);

/// <summary>WAL の反映（チェックポイント）の結果。</summary>
public enum OldDatabaseCheckpointOutcome
{
    /// <summary>反映が完了した。</summary>
    Completed,

    /// <summary>反映が完了しなかった——プロセス外の誰かが使用中の可能性がある。</summary>
    Busy,

    /// <summary>ファイルが破損している（使用中とは区別する）。</summary>
    Corrupt,
}

/// <summary>件数と ReceivedAt の範囲。</summary>
public sealed record OldDatabaseRecordRange(long Count, DateTimeOffset? Oldest, DateTimeOffset? Newest)
{
    /// <summary>空の範囲。</summary>
    public static OldDatabaseRecordRange Empty { get; } = new(0, null, null);
}

/// <summary>
/// 旧・組み込み DB ファイルを読む操作（database.md §6.3）。呼び出し側が旧ファイルの排他
/// （<see cref="OldDatabaseAccessGate"/>）を保持していることを前提とする。
/// </summary>
/// <remarks>
/// <para>
/// 接続は<b>ファイルを作成しない開き方</b>（<see cref="SqliteOpenMode.ReadWrite"/>）かつ接続プールを使わない
/// ——ファイルが無ければ作らずに失敗し、閉じた時点でハンドルを手放す（前提条件 3「プロセス内に旧ファイルを
/// 開いた接続が残っていないこと」を構造で満たす）。
/// </para>
/// <para>
/// 長い文（集計・チェックポイント）は取り消しで <c>sqlite3_interrupt</c> を呼んで文の途中で止める。
/// </para>
/// </remarks>
public static class OldSqliteDatabase
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    /// <summary>旧ファイルを作成しない・プールしない接続文字列。</summary>
    public static string ConnectionString(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWrite) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString();

    /// <summary>要約を読む。</summary>
    public static async Task<OldDatabaseSummary> ReadSummaryAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        return await ReadSummaryAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>開いた接続から要約を読む（書き出したファイルの検証でも使う）。</summary>
    public static async Task<OldDatabaseSummary> ReadSummaryAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var hasLogTable = await TableExistsAsync(connection, "LogRecords", cancellationToken).ConfigureAwait(false);
        if (!hasLogTable)
        {
            return new OldDatabaseSummary(false, null, 0, null, null, 0);
        }

        int? schemaVersion = null;
        if (await TableExistsAsync(connection, "SchemaVersion", cancellationToken).ConfigureAwait(false))
        {
            var version = await ScalarAsync(connection, "SELECT Version FROM SchemaVersion WHERE Id = 1;", cancellationToken)
                .ConfigureAwait(false);
            schemaVersion = version is null or DBNull ? null : Convert.ToInt32(version, CultureInfo.InvariantCulture);
        }

        var range = await QueryRangeAsync(connection, "SELECT COUNT(*), MIN(ReceivedAt), MAX(ReceivedAt) FROM LogRecords;", null, cancellationToken)
            .ConfigureAwait(false);

        long systemEventCount = 0;
        if (await TableExistsAsync(connection, "SystemEvents", cancellationToken).ConfigureAwait(false))
        {
            systemEventCount = Convert.ToInt64(
                await ScalarAsync(connection, "SELECT COUNT(*) FROM SystemEvents;", cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        return new OldDatabaseSummary(true, schemaVersion, range.Count, range.Oldest, range.Newest, systemEventCount);
    }

    /// <summary>
    /// WAL を本体へ反映する（<c>PRAGMA wal_checkpoint(TRUNCATE)</c>）。反映が完了しない（busy）ことは
    /// 使用中の可能性を示すが、完了は使用中でないことの証明にはならない——最終的な検出は本体を最後に消す
    /// 削除の失敗が担う。
    /// </summary>
    public static async Task<OldDatabaseCheckpointOutcome> CheckpointAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using var interrupt = RegisterInterrupt(connection, cancellationToken);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.GetInt64(0) != 0)
            {
                return OldDatabaseCheckpointOutcome.Busy;
            }

            return OldDatabaseCheckpointOutcome.Completed;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteBusy or SqliteLocked)
        {
            return OldDatabaseCheckpointOutcome.Busy;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            return OldDatabaseCheckpointOutcome.Corrupt;
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <summary>ReceivedAt の 1 時間ごとの件数（<paramref name="toExclusive"/> 未満）。</summary>
    public static async Task<IReadOnlyList<HourlyLogCount>> CountByHourAsync(
        string path,
        DateTimeOffset? toExclusive,
        CancellationToken cancellationToken)
    {
        await using var store = new Yagura.Storage.Sqlite.SqliteLogStore(path, createIfMissing: false);
        return await store.CountByHourAsync(null, toExclusive, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>移行済み位置 <c>(ReceivedAt, Id)</c> より後のレコードの件数と範囲。</summary>
    public static async Task<OldDatabaseRecordRange> RangeAfterPositionAsync(
        string path,
        DateTimeOffset receivedAt,
        long id,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        return await QueryRangeAsync(
            connection,
            """
            SELECT COUNT(*), MIN(ReceivedAt), MAX(ReceivedAt) FROM LogRecords
            WHERE ReceivedAt > $at OR (ReceivedAt = $at AND Id > $id);
            """,
            command =>
            {
                command.Parameters.Add("$at", SqliteType.Text).Value = receivedAt.UtcDateTime.ToString("O");
                command.Parameters.Add("$id", SqliteType.Integer).Value = id;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary><paramref name="afterExclusive"/> より後の ReceivedAt を持つレコードの件数と範囲。</summary>
    public static async Task<OldDatabaseRecordRange> RangeAfterAsync(
        string path,
        DateTimeOffset afterExclusive,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        return await QueryRangeAsync(
            connection,
            "SELECT COUNT(*), MIN(ReceivedAt), MAX(ReceivedAt) FROM LogRecords WHERE ReceivedAt > $after;",
            command => command.Parameters.Add("$after", SqliteType.Text).Value = afterExclusive.UtcDateTime.ToString("O"),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary><paramref name="cutoff"/> より前の ReceivedAt を持つレコードの件数（保存期間を過ぎたログ）。</summary>
    public static async Task<long> CountBeforeAsync(string path, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var range = await QueryRangeAsync(
            connection,
            "SELECT COUNT(*), MIN(ReceivedAt), MAX(ReceivedAt) FROM LogRecords WHERE ReceivedAt < $cutoff;",
            command => command.Parameters.Add("$cutoff", SqliteType.Text).Value = cutoff.UtcDateTime.ToString("O"),
            cancellationToken).ConfigureAwait(false);
        return range.Count;
    }

    /// <summary>
    /// 未移行とみなすレコードの件数と範囲: 照合に落ちた時間帯（UTC 正時の鍵 <c>yyyy-MM-ddTHH</c>）・
    /// 照合できない区間（<paramref name="unreconcilableAfter"/> より後）・移行済み位置より後、のいずれかに
    /// 当たるもの。
    /// </summary>
    public static async Task<OldDatabaseRecordRange> RangeUnmigratedAsync(
        string path,
        IReadOnlyCollection<DateTimeOffset> failedHourStarts,
        DateTimeOffset? unreconcilableAfter,
        (DateTimeOffset ReceivedAt, long Id)? migratedPosition,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var hourKeys = System.Text.Json.JsonSerializer.Serialize(
            failedHourStarts.Select(hour => hour.UtcDateTime.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture)));
        return await QueryRangeAsync(
            connection,
            """
            SELECT COUNT(*), MIN(ReceivedAt), MAX(ReceivedAt) FROM LogRecords
            WHERE substr(ReceivedAt, 1, 13) IN (SELECT value FROM json_each($hours))
               OR ($after IS NOT NULL AND ReceivedAt > $after)
               OR ($posAt IS NOT NULL AND (ReceivedAt > $posAt OR (ReceivedAt = $posAt AND Id > $posId)));
            """,
            command =>
            {
                command.Parameters.Add("$hours", SqliteType.Text).Value = hourKeys;
                command.Parameters.Add("$after", SqliteType.Text).Value =
                    (object?)unreconcilableAfter?.UtcDateTime.ToString("O") ?? DBNull.Value;
                command.Parameters.Add("$posAt", SqliteType.Text).Value =
                    (object?)migratedPosition?.ReceivedAt.UtcDateTime.ToString("O") ?? DBNull.Value;
                command.Parameters.Add("$posId", SqliteType.Integer).Value =
                    (object?)migratedPosition?.Id ?? DBNull.Value;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>旧ファイルのシステムイベントをすべて読む（件数はログ本体より桁違いに少ない）。</summary>
    public static async Task<IReadOnlyList<SystemEvent>> ReadSystemEventsAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        if (!await TableExistsAsync(connection, "SystemEvents", cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Kind, StartAt, EndAt, Approximate, Details FROM SystemEvents ORDER BY StartAt;";
        var results = new List<SystemEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new SystemEvent(
                Kind: reader.GetString(1),
                StartAt: ParseTimestamp(reader.GetString(2)),
                EndAt: ParseTimestamp(reader.GetString(3)),
                Approximate: reader.GetInt32(4) != 0,
                Id: reader.GetInt64(0),
                Details: reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return results;
    }

    /// <summary>旧ファイルを作成しない開き方で開く。</summary>
    public static async Task<SqliteConnection> OpenAsync(
        string path,
        CancellationToken cancellationToken,
        SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        var connection = new SqliteConnection(ConnectionString(path, mode));
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 取り消しで実行中の文を中断させる登録（<c>sqlite3_interrupt</c> は別スレッドから呼んでよい）。
    /// </summary>
    public static CancellationTokenRegistration RegisterInterrupt(SqliteConnection connection, CancellationToken cancellationToken) =>
        cancellationToken.Register(static state =>
        {
            var target = (SqliteConnection)state!;
            if (target.Handle is { } handle)
            {
                SQLitePCL.raw.sqlite3_interrupt(handle);
            }
        }, connection);

    /// <summary>表の有無。</summary>
    public static async Task<bool> TableExistsAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.Add("$name", SqliteType.Text).Value = table;
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var interrupt = RegisterInterrupt(connection, cancellationToken);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OldDatabaseRecordRange> QueryRangeAsync(
        SqliteConnection connection,
        string sql,
        Action<SqliteCommand>? bind,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind?.Invoke(command);
        using var interrupt = RegisterInterrupt(connection, cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return OldDatabaseRecordRange.Empty;
        }

        return new OldDatabaseRecordRange(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : ParseTimestamp(reader.GetString(1)),
            reader.IsDBNull(2) ? null : ParseTimestamp(reader.GetString(2)));
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
