using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Yagura.Storage.SqlServer;

public sealed partial class SqlServerLogStore
{
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
            var whereBuilder = new WhereClauseBuilder();

            if (query.ReceivedAtFrom is { } from)
            {
                whereBuilder.Add("ReceivedAt >= @receivedAtFrom",
                    () => command.Parameters.Add("@receivedAtFrom", System.Data.SqlDbType.DateTime2).Value = from.UtcDateTime);
            }

            if (query.ReceivedAtTo is { } to)
            {
                whereBuilder.Add("ReceivedAt <= @receivedAtTo",
                    () => command.Parameters.Add("@receivedAtTo", System.Data.SqlDbType.DateTime2).Value = to.UtcDateTime);
            }

            if (query.SourceAddress is { } sourceAddress)
            {
                whereBuilder.Add("SourceAddress = @sourceAddress",
                    () => command.Parameters.Add("@sourceAddress", System.Data.SqlDbType.NVarChar, 255).Value = sourceAddress);
            }

            if (query.SeverityAtMost is { } severityAtMost)
            {
                // 閾値方式（Severity <= N。LogQuery.SeverityAtMost の doc コメント参照——
                // syslog は数値が小さいほど深刻なため「N 以上の重大度」は「Severity <= N」になる。
                // Severity が NULL（PRI 未解析）の行は比較が unknown になり自然に対象外となる。
                whereBuilder.Add("Severity <= @severityAtMost",
                    () => command.Parameters.Add("@severityAtMost", System.Data.SqlDbType.Int).Value = severityAtMost);
            }

            if (query.Facility is { } facilityFilter)
            {
                whereBuilder.Add("Facility = @facility",
                    () => command.Parameters.Add("@facility", System.Data.SqlDbType.Int).Value = facilityFilter);
            }

            if (query.ParseStatus is { } parseStatusFilter)
            {
                whereBuilder.Add("ParseStatus = @parseStatus",
                    () => command.Parameters.Add("@parseStatus", System.Data.SqlDbType.Int).Value = (int)parseStatusFilter);
            }

            if (query.SearchText is { Length: > 0 } searchText)
            {
                // 自由文検索: Message に対する部分一致・大文字小文字を区別しない
                // （database.md §1.2 DB-6）。
                // v2 スキーマで Message 列に COLLATE Latin1_General_100_CI_AS_KS_WS_SC を明示した
                // ため（database.md §5.4）、LIKE の大文字小文字非区別（かつアクセント・かな種・
                // 全角/半角は区別する）はサーバの既定照合順序に依存せず列単位で保証される
                // （配備先の既定照合順序が CS でも本クエリの挙動は変わらない）。
                whereBuilder.Add("Message LIKE @searchText ESCAPE '\\'",
                    () => command.Parameters.Add("@searchText", System.Data.SqlDbType.NVarChar, -1).Value =
                        "%" + EscapeLikePattern(searchText) + "%");
            }

            if (query.Cursor is { } cursor)
            {
                // カーソル（キーセット）ページング（database.md §1.2・DB-11）:
                // 複合索引 IX_LogRecords_ReceivedAt_Id（ReceivedAt DESC, Id DESC）と同じ並びで
                // 「カーソルより過去」の行だけに絞るシーク条件。OFFSET は使わない。
                //
                // 述語の形（実行計画実測。SqlLocalDB・200 万行）:
                // 素朴な OR 分解 (ReceivedAt < @c OR (ReceivedAt = @c AND Id < @i)) は最適化器の
                // 計画選択が不安定で、統計サンプリングの揺れにより Clustered Index Scan + Sort
                // （全該当行を読んでからソート——中間カーソルで実際に約 100 万行を走査し 1 クエリ
                // 約 1 秒・浅いカーソルほど遅い）に落ちる場合と Index Seek になる場合の両方を
                // 同一データで観測した。下の形はこれと論理的に等価（R=@c かつ Id>=@i のとき
                // 両者とも偽・他も一致）で、先頭の conjunct（ReceivedAt <= @c）が単独で索引の
                // シーク述語になる——常にシーク可能な形であることが下記 FORCESEEK の前提。
                whereBuilder.AddRaw(
                    "(ReceivedAt <= @cursorReceivedAt AND (ReceivedAt < @cursorReceivedAt OR Id < @cursorId))");
                command.Parameters.Add("@cursorReceivedAt", System.Data.SqlDbType.DateTime2).Value =
                    cursor.ReceivedAt.UtcDateTime;
                command.Parameters.Add("@cursorId", System.Data.SqlDbType.BigInt).Value = cursor.Id;
            }

            var whereSql = whereBuilder.BuildWhereSql();

            // FORCESEEK テーブルヒント（カーソル指定時のみ）: 上記のとおり計画選択が不安定な
            // コスト境界領域にあるため、「索引シークのみを許す」ヒントで計画を固定する
            // （Microsoft Learn "Table hints (Transact-SQL)" が FORCESEEK の用途として挙げる
            // 「推定の問題でシークではなくスキャンが選ばれる場合」そのもの）。
            // カーソル指定時は常にシーク可能な範囲条件（ReceivedAt <= @c）が WHERE に含まれる
            // ためコンパイル不能にはならない（他フィルタ——Severity 閾値・LIKE——との併用も
            // 実行計画で確認済み。database.md §8 DB-11）。実測: シーク計画はカーソル深度に
            // よらず約 19ms/クエリ（limit=10,000）で平坦——スキャン計画（深度により
            // 約 0.2〜2 秒）を常に下回る。カーソルなし（先頭ページ・従来経路）には付与しない
            // ——従来の計画選択を変えない。
            var fromClause = query.Cursor is not null
                ? "FROM dbo.LogRecords WITH (FORCESEEK)"
                : "FROM dbo.LogRecords";

            // Id DESC のタイブレーク: ReceivedAt 単独では同一時刻（同一ミリ秒）の
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
                results.Add(LogRecordDataReaderMapper.ReadSummary(reader, ReadTimestamp, query.MessageProjectionLength));
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
            // 翻訳される）として送出する（dotnet/SqlClient の maintainer が
            // ".NET Framework と同一の挙動であり、変更予定はない" と明言している）。
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

            return LogRecordDataReaderMapper.ReadRecord(reader, ReadTimestamp);
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
    /// 一括読み出し（IBulkLogReader。database.md §1.2 予約 (a) の実体化）。
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
                    batch.Add(LogRecordDataReaderMapper.ReadRecord(reader, ReadTimestamp));
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
            var whereBuilder = new WhereClauseBuilder();

            // 区間の重なり判定（ILogStore の契約参照）: 範囲に少しでも掛かる区間を返す。
            if (from is { } fromValue)
            {
                whereBuilder.Add("EndAt >= @from",
                    () => command.Parameters.Add("@from", System.Data.SqlDbType.DateTime2).Value = fromValue.UtcDateTime);
            }

            if (to is { } toValue)
            {
                whereBuilder.Add("StartAt <= @to",
                    () => command.Parameters.Add("@to", System.Data.SqlDbType.DateTime2).Value = toValue.UtcDateTime);
            }

            // 種別の完全一致フィルタ（ILogStore の契約参照）。
            if (kind is not null)
            {
                whereBuilder.Add("Kind = @kind",
                    () => command.Parameters.Add("@kind", System.Data.SqlDbType.NVarChar, 255).Value = kind);
            }

            var whereSql = whereBuilder.BuildWhereSql();

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
                results.Add(LogRecordDataReaderMapper.ReadSystemEvent(reader, ReadTimestamp, ReadApproximate));
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
        // 新しい順（候補選択用。打ち切りで切り捨てるのは「古い側」）。
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
                results.Add(LogRecordDataReaderMapper.ReadSourceActivity(reader, ReadTimestamp));
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
            // Severity 列に索引が無いための窓必須化）。
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
                results.Add(LogRecordDataReaderMapper.ReadSeverityCount(reader));
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
                results.Add(LogRecordDataReaderMapper.ReadSourceActivity(reader, ReadTimestamp));
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

    // LogRecordDataReaderMapper へ注入する SQL Server 側アダプタ（DateTime2 列。UTC は
    // アプリケーション層の約束であり列自体は Kind を持たないため SpecifyKind で明示する）。
    private static DateTimeOffset ReadTimestamp(DbDataReader reader, int ordinal) =>
        DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc);

    // SystemEvents.Approximate は BIT 列。
    private static bool ReadApproximate(DbDataReader reader, int ordinal) => reader.GetBoolean(ordinal);

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
}
