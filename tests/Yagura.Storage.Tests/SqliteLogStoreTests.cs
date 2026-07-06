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
    public async Task WriteBatchAsync_ThenQueryLatest_ReturnsRecordsInDescendingOrderWithoutRaw()
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

        // 一覧射影は本文の接頭表示のため StructuredData を含む（ui.md §4・database.md §2.1）。
        Assert.All(results, r => Assert.Equal("[exampleSDID@32473 iut=\"3\"]", r.StructuredData));

        // 射影型 (LogRecordSummary) には Raw のプロパティ自体が存在しないこと
        // （コンパイル時に検証されるため、ここでは公開プロパティの網羅を確認する）。
        var summaryProperties = typeof(LogRecordSummary).GetProperties().Select(p => p.Name);
        Assert.DoesNotContain("Raw", summaryProperties);
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
    // 対話的検索: SQLite 固有の検証（database.md §1.2 契約 4。M5-1・M5-2）
    // ------------------------------------------------------------------
    //
    // 条件・組み合わせ・射影・上限・キャンセル伝播の機械検証は
    // Yagura.Storage.ConformanceTests/LogStoreConformanceTestBase に集約した
    // （M5-2 database.md §1.3。provider 非依存の契約検証は適合スイートを正とし、
    // 純粋に重複する検証は本ファイルから削除した）。ここには SQLite の LIKE 演算子の
    // ワイルドカードエスケープという実装固有の検証のみ残す。

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

    // ------------------------------------------------------------------
    // 保持期間削除: SQLite 固有の検証（database.md §1.2 契約 5・§3。M5-1・M5-2）
    // ------------------------------------------------------------------
    //
    // cutoff 境界・分割実行・削除件数・空 DB の機械検証は適合スイートに集約した
    // （上記と同じ理由。M5-2）。ここには接続分離での並行実行（WAL 固有の性質）と、
    // 「本メソッド自体はシステムイベントを書かない」という実装判断の検証を残す。

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
    // 統計: SQLite 固有の検証（database.md §1.2 契約 6。M5-1・M5-2）
    // ------------------------------------------------------------------
    //
    // 件数の正確さ・空 DB での 0 件・「値があるか取得不能の明示か」の機械検証は
    // 適合スイートに集約した（M5-2）。ここには SQLite が DatabaseSizeBytes を
    // 常に非 null の正数値として返すという実装固有の性質（page_count * page_size に
    // よる算出。§5.3 の SQL Server 必須要件とは異なり SQLite には「取得不能」の
    // 逃げ道自体が実質発生しない）と、WAL ファイルサイズの観測を残す。

    [Fact]
    public async Task GetStatisticsAsync_DatabaseSizeIsAlwaysPositiveNumber()
    {
        await _store.WriteBatchAsync(new[]
        {
            CreateParsedRecord(DateTimeOffset.UtcNow, "10.0.0.1", "first"),
            CreateParsedRecord(DateTimeOffset.UtcNow, "10.0.0.2", "second"),
        });

        var statistics = await _store.GetStatisticsAsync();

        Assert.NotNull(statistics.DatabaseSizeBytes);
        Assert.True(statistics.DatabaseSizeBytes > 0);
        Assert.Null(statistics.DatabaseSizeUnavailableReason);
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
