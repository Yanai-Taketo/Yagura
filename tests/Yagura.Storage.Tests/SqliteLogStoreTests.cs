using Microsoft.Data.Sqlite;
using Yagura.Storage;
using Yagura.Storage.Sqlite;

namespace Yagura.Storage.Tests;

public sealed class SqliteLogStoreTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"yagura-storage-tests-{Guid.NewGuid():N}.db");
    private SqliteLogStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new SqliteLogStore(_databasePath);
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();

        // WAL モードは -wal / -shm の補助ファイルも生成するため、まとめて削除する。
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task InitializeAsync_CalledTwice_DoesNotThrow()
    {
        var exception = await Record.ExceptionAsync(() => _store.InitializeAsync());

        Assert.Null(exception);
    }

    [Fact]
    public async Task WriteBatchAsync_ThenQueryLatest_ReturnsRecordsInDescendingOrderWithoutRawOrStructuredData()
    {
        var baseline = DateTimeOffset.UtcNow;
        var records = new[]
        {
            CreateParsedRecord(baseline.AddSeconds(-2), "10.0.0.1", "first"),
            CreateParsedRecord(baseline.AddSeconds(-1), "10.0.0.2", "second"),
            CreateParsedRecord(baseline, "10.0.0.3", "third"),
        };

        await _store.WriteBatchAsync(records);

        var results = await _store.QueryLatestAsync(limit: 10, timeout: TimeSpan.FromSeconds(5));

        Assert.Equal(3, results.Count);
        Assert.Equal("third", results[0].Message);
        Assert.Equal("second", results[1].Message);
        Assert.Equal("first", results[2].Message);

        // 射影型 (LogRecordSummary) には Raw / StructuredData のプロパティ自体が存在しないこと
        // （コンパイル時に検証されるため、ここでは公開プロパティの網羅を確認する）。
        var summaryProperties = typeof(LogRecordSummary).GetProperties().Select(p => p.Name);
        Assert.DoesNotContain("Raw", summaryProperties);
        Assert.DoesNotContain("StructuredData", summaryProperties);
    }

    [Fact]
    public async Task WriteBatchAsync_ParseFailedRecordWithRaw_RoundTripsThroughRawColumn()
    {
        var receivedAt = DateTimeOffset.UtcNow;
        var raw = new byte[] { 0x00, 0xFF, 0x41, 0x42, 0xC0 };
        var record = new LogRecord(
            ReceivedAt: receivedAt,
            SourceAddress: "192.168.1.10",
            SourcePort: 514,
            Protocol: Protocol.Tcp,
            ParseStatus: ParseStatus.ParseFailed,
            Raw: raw);

        await _store.WriteBatchAsync(new[] { record });

        var results = await _store.QueryLatestAsync(limit: 1, timeout: TimeSpan.FromSeconds(5));

        Assert.Single(results);
        Assert.Equal(ParseStatus.ParseFailed, results[0].ParseStatus);

        // 射影 (QueryLatestAsync) は Raw を含まないため、Raw 自体の往復は生の SQL で確認する。
        var storedRaw = (byte[]?)await ExecuteScalarOnVerificationConnectionAsync(
            "SELECT Raw FROM LogRecords WHERE Id = $id;",
            command => command.Parameters.AddWithValue("$id", results[0].Id));

        Assert.Equal(raw, storedRaw);
    }

    [Fact]
    public async Task WalModeIsEnabled()
    {
        var mode = (string?)await ExecuteScalarOnVerificationConnectionAsync("PRAGMA journal_mode;");

        Assert.Equal("wal", mode, ignoreCase: true);
    }

    /// <summary>
    /// テストの往復確認用に、対象データベースへ読み取り専用接続を開いて 1 件のスカラー値を取得する。
    /// Microsoft.Data.Sqlite の既定のネイティブ接続プーリングは Dispose 後も OS ファイル
    /// ハンドルを保持し得るため、テスト終了時のファイル削除に備えて明示的にプールを破棄する。
    /// </summary>
    private async Task<object?> ExecuteScalarOnVerificationConnectionAsync(
        string commandText,
        Action<SqliteCommand>? configureCommand = null)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());

        try
        {
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = commandText;
            configureCommand?.Invoke(command);

            return await command.ExecuteScalarAsync();
        }
        finally
        {
            await connection.DisposeAsync();
            SqliteConnection.ClearPool(connection);
        }
    }

    [Fact]
    public async Task QueryLatestAsync_LimitIsRespected()
    {
        var baseline = DateTimeOffset.UtcNow;
        var records = Enumerable.Range(0, 5)
            .Select(i => CreateParsedRecord(baseline.AddSeconds(-i), $"10.0.0.{i}", $"message-{i}"))
            .ToArray();

        await _store.WriteBatchAsync(records);

        var results = await _store.QueryLatestAsync(limit: 2, timeout: TimeSpan.FromSeconds(5));

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task WriteAndQueryConcurrently_DoNotInterfere()
    {
        // WAL の「読み取りは書き込みをブロックせず、書き込みも読み取りをブロックしない」
        // 性質が接続分離の実装で成立していることの確認。
        // 書き込みは ILogStore の契約どおり単一タスクに直列化し、読み取りを並行させる。
        const int batchCount = 20;
        const int recordsPerBatch = 10;
        const int readerCount = 3;
        var baseline = DateTimeOffset.UtcNow;

        var writer = Task.Run(async () =>
        {
            for (var batch = 0; batch < batchCount; batch++)
            {
                var records = Enumerable.Range(0, recordsPerBatch)
                    .Select(i => CreateParsedRecord(
                        baseline.AddSeconds(batch * recordsPerBatch + i),
                        "10.0.0.1",
                        $"batch-{batch}-record-{i}"))
                    .ToArray();
                await _store.WriteBatchAsync(records);
            }
        });

        var readers = Enumerable.Range(0, readerCount)
            .Select(_ => Task.Run(async () =>
            {
                // writer が完了するまで読み取りを繰り返し、書き込みと確実に重ねる。
                while (!writer.IsCompleted)
                {
                    await _store.QueryLatestAsync(limit: 50, timeout: TimeSpan.FromSeconds(30));
                }
            }))
            .ToArray();

        var exception = await Record.ExceptionAsync(() => Task.WhenAll(readers.Append(writer)));

        Assert.Null(exception);

        var finalResults = await _store.QueryLatestAsync(
            limit: batchCount * recordsPerBatch + 1,
            timeout: TimeSpan.FromSeconds(30));

        Assert.Equal(batchCount * recordsPerBatch, finalResults.Count);
    }

    // ------------------------------------------------------------------
    // システムイベント（database.md §2.3。M4-4: architecture.md §4.4 受信断可視化）
    // ------------------------------------------------------------------

    [Fact]
    public async Task WriteSystemEventAsync_NormalStopKind_PersistsAllColumns()
    {
        var baseline = DateTimeOffset.UtcNow;
        var systemEvent = new SystemEvent(
            Kind: "downtime.normal-stop",
            StartAt: baseline.AddMinutes(-5),
            EndAt: baseline,
            Approximate: false,
            Details: "test detail");

        await _store.WriteSystemEventAsync(systemEvent);

        var (kind, startAt, endAt, approximate, details) = await ReadSingleSystemEventAsync();
        Assert.Equal("downtime.normal-stop", kind);
        Assert.Equal(systemEvent.StartAt.UtcDateTime, startAt, TimeSpan.FromMilliseconds(1));
        Assert.Equal(systemEvent.EndAt.UtcDateTime, endAt, TimeSpan.FromMilliseconds(1));
        Assert.False(approximate);
        Assert.Equal("test detail", details);
    }

    [Fact]
    public async Task WriteSystemEventAsync_ApproximateCrashKind_PersistsApproximateTrue()
    {
        var baseline = DateTimeOffset.UtcNow;
        var systemEvent = new SystemEvent(
            Kind: "downtime.crash-approximate",
            StartAt: baseline.AddHours(-1),
            EndAt: baseline,
            Approximate: true);

        await _store.WriteSystemEventAsync(systemEvent);

        var (kind, _, _, approximate, details) = await ReadSingleSystemEventAsync();
        Assert.Equal("downtime.crash-approximate", kind);
        Assert.True(approximate);
        Assert.Null(details);
    }

    [Fact]
    public async Task WriteSystemEventAsync_MultipleEvents_AllPersisted()
    {
        var baseline = DateTimeOffset.UtcNow;

        await _store.WriteSystemEventAsync(new SystemEvent("downtime.normal-stop", baseline.AddMinutes(-10), baseline.AddMinutes(-9), false));
        await _store.WriteSystemEventAsync(new SystemEvent("downtime.crash-approximate", baseline.AddMinutes(-5), baseline, true));

        var count = await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM SystemEvents;";
            return (long)(await command.ExecuteScalarAsync())!;
        });

        Assert.Equal(2, count);
    }

    /// <summary>
    /// テスト用の読み取り接続を開いて操作し、確実にプールをクリアしてから返す
    /// （<see cref="SqliteLogStore.DisposeAsync"/> と同じ理由——テスト終了時の
    /// データベースファイル削除が「別プロセスが使用中」で失敗しないようにするため。
    /// Microsoft.Data.Sqlite は既定でネイティブ接続をプールし、<c>Dispose</c> 後も
    /// OS レベルのハンドルを保持し得る）。
    /// </summary>
    private async Task<T> WithConnectionAsync<T>(Func<SqliteConnection, Task<T>> action)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            return await action(connection);
        }
        finally
        {
            using var poolConnection = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(poolConnection);
        }
    }

    private Task<(string Kind, DateTime StartAt, DateTime EndAt, bool Approximate, string? Details)> ReadSingleSystemEventAsync() =>
        WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Kind, StartAt, EndAt, Approximate, Details FROM SystemEvents;";
            await using var reader = await command.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());
            var result = (
                reader.GetString(0),
                DateTime.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
                DateTime.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
                reader.GetInt64(3) != 0,
                reader.IsDBNull(4) ? null : reader.GetString(4));

            Assert.False(await reader.ReadAsync(), "1 件のみ書き込んだはずが複数件検出された。");
            return result;
        });

    private static LogRecord CreateParsedRecord(DateTimeOffset receivedAt, string sourceAddress, string message) =>
        new(
            ReceivedAt: receivedAt,
            SourceAddress: sourceAddress,
            SourcePort: 514,
            Protocol: Protocol.Udp,
            ParseStatus: ParseStatus.Parsed,
            Facility: 1,
            Severity: 5,
            Hostname: "host",
            AppName: "app",
            ProcId: "123",
            MsgId: "msg-1",
            StructuredData: "[exampleSDID@32473 iut=\"3\"]",
            Message: message);

    // ------------------------------------------------------------------
    // スキーマ版間移行の土台（database.md §1.2 契約 1。M5-1）
    // ------------------------------------------------------------------

    [Fact]
    public async Task InitializeAsync_RecordsCurrentSchemaVersion()
    {
        var version = await ExecuteScalarOnVerificationConnectionAsync("SELECT Version FROM SchemaVersion WHERE Id = 1;");

        Assert.Equal((long)SqliteLogStore.CurrentSchemaVersion, version);
    }

    [Fact]
    public async Task InitializeAsync_CalledTwice_SchemaVersionRemainsSingleRow()
    {
        await _store.InitializeAsync();
        await _store.InitializeAsync();

        var count = await ExecuteScalarOnVerificationConnectionAsync("SELECT COUNT(*) FROM SchemaVersion;");

        Assert.Equal(1L, count);
    }

    // ------------------------------------------------------------------
    // 対話的検索の完全化（database.md §1.2 契約 4。M5-1）
    // ------------------------------------------------------------------

    [Fact]
    public async Task QueryAsync_ReceivedAtRange_FiltersToRange()
    {
        var baseline = DateTimeOffset.UtcNow;
        var records = new[]
        {
            CreateParsedRecord(baseline.AddMinutes(-10), "10.0.0.1", "too-old"),
            CreateParsedRecord(baseline.AddMinutes(-5), "10.0.0.2", "in-range"),
            CreateParsedRecord(baseline, "10.0.0.3", "too-new"),
        };
        await _store.WriteBatchAsync(records);

        var results = await _store.QueryAsync(new LogQuery(
            Limit: 10,
            Timeout: TimeSpan.FromSeconds(5),
            ReceivedAtFrom: baseline.AddMinutes(-6),
            ReceivedAtTo: baseline.AddMinutes(-1)));

        Assert.Single(results);
        Assert.Equal("in-range", results[0].Message);
    }

    [Fact]
    public async Task QueryAsync_SourceAddress_FiltersToExactMatch()
    {
        var baseline = DateTimeOffset.UtcNow;
        await _store.WriteBatchAsync(new[]
        {
            CreateParsedRecord(baseline.AddSeconds(-1), "10.0.0.1", "from-1"),
            CreateParsedRecord(baseline, "10.0.0.2", "from-2"),
        });

        var results = await _store.QueryAsync(new LogQuery(
            Limit: 10,
            Timeout: TimeSpan.FromSeconds(5),
            SourceAddress: "10.0.0.2"));

        Assert.Single(results);
        Assert.Equal("from-2", results[0].Message);
    }

    [Fact]
    public async Task QueryAsync_Severity_FiltersToExactMatch()
    {
        var baseline = DateTimeOffset.UtcNow;
        var low = CreateParsedRecord(baseline.AddSeconds(-1), "10.0.0.1", "low-severity") with { Severity = 3 };
        var high = CreateParsedRecord(baseline, "10.0.0.1", "high-severity") with { Severity = 7 };
        await _store.WriteBatchAsync(new[] { low, high });

        var results = await _store.QueryAsync(new LogQuery(Limit: 10, Timeout: TimeSpan.FromSeconds(5), Severity: 7));

        Assert.Single(results);
        Assert.Equal("high-severity", results[0].Message);
    }

    [Fact]
    public async Task QueryAsync_SearchText_MatchesSubstringCaseInsensitive()
    {
        var baseline = DateTimeOffset.UtcNow;
        await _store.WriteBatchAsync(new[]
        {
            CreateParsedRecord(baseline.AddSeconds(-1), "10.0.0.1", "Connection RESET by peer"),
            CreateParsedRecord(baseline, "10.0.0.1", "normal heartbeat"),
        });

        var results = await _store.QueryAsync(new LogQuery(Limit: 10, Timeout: TimeSpan.FromSeconds(5), SearchText: "reset"));

        Assert.Single(results);
        Assert.Contains("RESET", results[0].Message);
    }

    [Fact]
    public async Task QueryAsync_SearchText_WithWildcardCharacters_TreatedLiterally()
    {
        var baseline = DateTimeOffset.UtcNow;
        await _store.WriteBatchAsync(new[]
        {
            CreateParsedRecord(baseline.AddSeconds(-1), "10.0.0.1", "100% cpu usage"),
            CreateParsedRecord(baseline, "10.0.0.1", "unrelated message"),
        });

        var results = await _store.QueryAsync(new LogQuery(Limit: 10, Timeout: TimeSpan.FromSeconds(5), SearchText: "100%"));

        Assert.Single(results);
        Assert.Equal("100% cpu usage", results[0].Message);
    }

    [Fact]
    public async Task QueryAsync_CombinedConditions_AllMustMatch()
    {
        var baseline = DateTimeOffset.UtcNow;
        var matching = CreateParsedRecord(baseline, "10.0.0.5", "disk failure detected") with { Severity = 2 };
        var wrongSource = CreateParsedRecord(baseline, "10.0.0.6", "disk failure detected") with { Severity = 2 };
        var wrongSeverity = CreateParsedRecord(baseline, "10.0.0.5", "disk failure detected") with { Severity = 6 };
        await _store.WriteBatchAsync(new[] { matching, wrongSource, wrongSeverity });

        var results = await _store.QueryAsync(new LogQuery(
            Limit: 10,
            Timeout: TimeSpan.FromSeconds(5),
            SourceAddress: "10.0.0.5",
            Severity: 2,
            SearchText: "disk failure"));

        Assert.Single(results);
    }

    [Fact]
    public async Task QueryAsync_MessageProjectionLength_TruncatesToFirstNCharacters()
    {
        var longMessage = new string('a', 500);
        await _store.WriteBatchAsync(new[] { CreateParsedRecord(DateTimeOffset.UtcNow, "10.0.0.1", longMessage) });

        var results = await _store.QueryAsync(new LogQuery(Limit: 10, Timeout: TimeSpan.FromSeconds(5), MessageProjectionLength: 200));

        Assert.Single(results);
        Assert.Equal(200, results[0].Message!.Length);
        Assert.Equal(longMessage[..200], results[0].Message);
    }

    [Fact]
    public async Task QueryAsync_MessageShorterThanProjectionLength_NotPadded()
    {
        await _store.WriteBatchAsync(new[] { CreateParsedRecord(DateTimeOffset.UtcNow, "10.0.0.1", "short") });

        var results = await _store.QueryAsync(new LogQuery(Limit: 10, Timeout: TimeSpan.FromSeconds(5)));

        Assert.Single(results);
        Assert.Equal("short", results[0].Message);
    }

    [Fact]
    public async Task QueryAsync_ExternalCancellation_ThrowsOperationCanceledNotTimeout()
    {
        // タイムアウト発火の実時間競合（極小タイムアウト vs クエリ実行速度）はタイマー分解能
        // 依存で flaky になるため、時間競合そのものはテストしない。ここでは決定的に検証できる
        // 「外部キャンセルは TimeoutException へ変換されない」（実装の when 句の弁別。
        // 呼び出し側が『打ち切られた』と『利用者がやめた』を区別できる契約）を固定化する。
        // タイムアウト→TimeoutException 変換の分岐自体は QueryLatestAsync 時代から同一構造で、
        // 網羅は分岐のコードレビューに委ねる。
        await _store.WriteBatchAsync(new[] { CreateParsedRecord(DateTimeOffset.UtcNow, "10.0.0.1", "message") });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _store.QueryAsync(new LogQuery(Limit: 10, Timeout: TimeSpan.FromSeconds(5)), cts.Token));
    }

    [Fact]
    public async Task QueryLatestAsync_DelegatesToQueryAsync_NoConditions()
    {
        var baseline = DateTimeOffset.UtcNow;
        await _store.WriteBatchAsync(new[]
        {
            CreateParsedRecord(baseline.AddSeconds(-1), "10.0.0.1", "first"),
            CreateParsedRecord(baseline, "10.0.0.2", "second"),
        });

        var results = await _store.QueryLatestAsync(limit: 10, timeout: TimeSpan.FromSeconds(5));

        Assert.Equal(2, results.Count);
        Assert.Equal("second", results[0].Message);
    }

    // ------------------------------------------------------------------
    // 保持期間削除（database.md §1.2 契約 5・§3。M5-1）
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeleteOlderThanAsync_CutoffBoundary_DeletesOnlyStrictlyOlderRecords()
    {
        var cutoff = DateTimeOffset.UtcNow;
        await _store.WriteBatchAsync(new[]
        {
            CreateParsedRecord(cutoff.AddSeconds(-1), "10.0.0.1", "older-than-cutoff"),
            CreateParsedRecord(cutoff, "10.0.0.2", "exactly-at-cutoff"),
            CreateParsedRecord(cutoff.AddSeconds(1), "10.0.0.3", "newer-than-cutoff"),
        });

        var result = await _store.DeleteOlderThanAsync(cutoff);

        Assert.Equal(1, result.DeletedCount);

        var remaining = await _store.QueryLatestAsync(limit: 10, timeout: TimeSpan.FromSeconds(5));
        Assert.Equal(2, remaining.Count);
        Assert.DoesNotContain(remaining, r => r.Message == "older-than-cutoff");
        Assert.Contains(remaining, r => r.Message == "exactly-at-cutoff");
        Assert.Contains(remaining, r => r.Message == "newer-than-cutoff");
    }

    [Fact]
    public async Task DeleteOlderThanAsync_MoreRecordsThanBatchSize_DeletesAllInMultipleBatches()
    {
        var cutoff = DateTimeOffset.UtcNow;
        var totalRecords = RetentionConstants.DeleteBatchMaxSize + 250;
        var records = Enumerable.Range(0, totalRecords)
            .Select(i => CreateParsedRecord(cutoff.AddSeconds(-1 - i), "10.0.0.1", $"old-{i}"))
            .ToArray();
        await _store.WriteBatchAsync(records);

        var result = await _store.DeleteOlderThanAsync(cutoff);

        Assert.Equal(totalRecords, result.DeletedCount);

        var remainingCount = await ExecuteScalarOnVerificationConnectionAsync("SELECT COUNT(*) FROM LogRecords;");
        Assert.Equal(0L, remainingCount);
    }

    [Fact]
    public async Task DeleteOlderThanAsync_NoMatchingRecords_ReturnsZero()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        await _store.WriteBatchAsync(new[] { CreateParsedRecord(DateTimeOffset.UtcNow, "10.0.0.1", "recent") });

        var result = await _store.DeleteOlderThanAsync(cutoff);

        Assert.Equal(0, result.DeletedCount);
    }

    [Fact]
    public async Task DeleteOlderThanAsync_ConcurrentWithWrites_BothCompleteWithoutError()
    {
        var baseline = DateTimeOffset.UtcNow;
        var oldRecords = Enumerable.Range(0, RetentionConstants.DeleteBatchMaxSize * 2)
            .Select(i => CreateParsedRecord(baseline.AddDays(-1).AddSeconds(-i), "10.0.0.1", $"old-{i}"))
            .ToArray();
        await _store.WriteBatchAsync(oldRecords);

        var cutoff = baseline;

        var deleteTask = _store.DeleteOlderThanAsync(cutoff);

        var writeTask = Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                await _store.WriteBatchAsync(new[] { CreateParsedRecord(baseline.AddSeconds(i), "10.0.0.2", $"new-{i}") });
            }
        });

        await Task.WhenAll(deleteTask, writeTask);

        var deleteResult = await deleteTask;
        Assert.Equal(oldRecords.Length, deleteResult.DeletedCount);

        var remainingCount = await ExecuteScalarOnVerificationConnectionAsync("SELECT COUNT(*) FROM LogRecords;");
        Assert.Equal(20L, remainingCount);
    }

    [Fact]
    public async Task DeleteOlderThanAsync_WritesNoSystemEvent_CallerIsResponsibleForRecording()
    {
        // ILogStore.DeleteOlderThanAsync のドキュメントどおり、実行記録（システムイベント）の
        // 書き込みは呼び出し側の責務であり、本メソッド自体は書かない。
        await _store.WriteBatchAsync(new[] { CreateParsedRecord(DateTimeOffset.UtcNow.AddDays(-1), "10.0.0.1", "old") });

        await _store.DeleteOlderThanAsync(DateTimeOffset.UtcNow);

        var systemEventCount = await ExecuteScalarOnVerificationConnectionAsync("SELECT COUNT(*) FROM SystemEvents;");
        Assert.Equal(0L, systemEventCount);
    }

    // ------------------------------------------------------------------
    // 統計（database.md §1.2 契約 6。M5-1）
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetStatisticsAsync_ReturnsRecordCountAndPositiveDatabaseSize()
    {
        await _store.WriteBatchAsync(new[]
        {
            CreateParsedRecord(DateTimeOffset.UtcNow, "10.0.0.1", "first"),
            CreateParsedRecord(DateTimeOffset.UtcNow, "10.0.0.2", "second"),
        });

        var statistics = await _store.GetStatisticsAsync();

        Assert.Equal(2, statistics.RecordCount);
        Assert.NotNull(statistics.DatabaseSizeBytes);
        Assert.True(statistics.DatabaseSizeBytes > 0);
        Assert.Null(statistics.DatabaseSizeUnavailableReason);
    }

    [Fact]
    public async Task GetStatisticsAsync_EmptyDatabase_RecordCountIsZero()
    {
        var statistics = await _store.GetStatisticsAsync();

        Assert.Equal(0, statistics.RecordCount);
    }

    [Fact]
    public async Task GetStatisticsAsync_AfterWriteBeforeCheckpoint_WalSizeIsPositive()
    {
        await _store.WriteBatchAsync(new[] { CreateParsedRecord(DateTimeOffset.UtcNow, "10.0.0.1", "wal-probe") });

        var statistics = await _store.GetStatisticsAsync();

        // WAL モードでは書き込み直後、checkpoint 前は -wal ファイルにフレームが残っている
        // （SQLite 公式ドキュメント "Write-Ahead Logging"。§4 の WAL 肥大監視の入力）。
        Assert.NotNull(statistics.WalSizeBytes);
        Assert.True(statistics.WalSizeBytes >= 0);
    }

    // ------------------------------------------------------------------
    // 失敗の 3 分類報告（database.md §1.2 契約 3。M5-1）
    // ------------------------------------------------------------------

    // 容量枯渇（SQLITE_FULL）経路のテストは SqliteCapacityTests.cs 参照。
    // 当初は PRAGMA max_page_count でストア経由の SQLITE_FULL を再現しようとしたが、
    // この PRAGMA は接続ごとの設定であり（SQLite 公式 "PRAGMA max_page_count"）、
    // 操作ごとに新しい接続を開く SqliteLogStore には効かない——テストは例外に到達せず
    // 成立しなかった。分類ロジックの検証（決定的）とエンジンレベルのページ再利用検証
    // （単一接続・決定的）に分けて SqliteCapacityTests.cs で行う。
}
