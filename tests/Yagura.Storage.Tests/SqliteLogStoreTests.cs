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
}
