using System.Net;
using System.Net.Sockets;
using System.Text;
using Yagura.Ingestion.Tcp;
using Yagura.Ingestion.Udp;
using Yagura.Storage;
using Yagura.Storage.Spool;

namespace Yagura.Ingestion.Tests;

/// <summary>
/// 停止時退避 → 再起動 drain の往復（architecture.md §1.3 手順 2）。
/// パイプラインレベルの結合テスト: DB への書き込みが進まない状況で停止要求を出し、
/// メモリ上の未永続化ログが DB を待たずスプールへ退避されること、そのスプール分が
/// 次回起動（新しいパイプライン + drain）で DB に届くことを確認する。
/// </summary>
public sealed class StopTimeSpoolFlushTests : IDisposable
{
    private readonly string _spoolDirectory = Path.Combine(Path.GetTempPath(), $"yagura-stopflush-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_spoolDirectory))
        {
            Directory.Delete(_spoolDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StopDuringSlowWrite_RecordIsSpooled_ThenDrainedOnNextPipelineStart()
    {
        var marker = $"stop-flush-{Guid.NewGuid():N}";

        // --- 1 回目の「起動」: DB 書き込みが決して完了しないストアを使い、停止時に
        // メモリ上の未永続化ログがスプールへ退避されることを確認する。
        var neverCompletingStore = new NeverCompletingLogStore();

        using (var spool1 = DiskSpool.TryOpen(new DiskSpoolOptions { Directory = _spoolDirectory }, out var openFailure1))
        {
            Assert.NotNull(spool1);
            Assert.Null(openFailure1);

            await using var pipeline = new IngestionPipeline(
                new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
                new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
                neverCompletingStore,
                new FlowControl.NoopIngressGate(),
                loggerFactory: null,
                spool1);

            await pipeline.StartListenerAsync();
            pipeline.StartConsumers();

            using var sender = new UdpClient();
            var target = new IPEndPoint(IPAddress.Loopback, pipeline.BoundPort);
            var payload = Encoding.UTF8.GetBytes($"<34>{marker}");
            await sender.SendAsync(payload, target);

            // メッセージが Q2 まで到達し、PersistenceWriter が「書き込み中」（完了しない
            // WriteBatchAsync の待機中）になるまで待つ。
            await WaitUntilAsync(() => neverCompletingStore.WriteAttempted, TimeSpan.FromSeconds(10));

            // ここで停止要求——DB 書き込みは進行中のまま完了しない。§1.3 手順 2 により、
            // DB を待たずスプールへ退避されるはず。
            var stopTask = pipeline.StopAsync();
            var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(15)));

            Assert.Same(stopTask, completed);
            await stopTask;
        }

        // --- 前回退避分がスプールに残っていることを確認する（起動手順 1 の「前回退避分の
        // 存在確認」に相当）。
        using (var reopened = DiskSpool.TryOpen(new DiskSpoolOptions { Directory = _spoolDirectory }, out _))
        {
            Assert.NotNull(reopened);
            Assert.True(reopened!.CurrentUsageBytes > 0);
        }

        // --- 2 回目の「起動」: 正常に書き込める DB で drain し、スプール分が届くことを確認する。
        var recoveredStore = new RecordingLogStore();

        using (var spool2 = DiskSpool.TryOpen(new DiskSpoolOptions { Directory = _spoolDirectory }, out _))
        {
            Assert.NotNull(spool2);

            await using var pipeline = new IngestionPipeline(
                new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
                new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
                recoveredStore,
                new FlowControl.NoopIngressGate(),
                loggerFactory: null,
                spool2);

            await pipeline.StartListenerAsync();
            pipeline.StartConsumers();

            await WaitUntilAsync(
                () => recoveredStore.WrittenRecords.Any(r => r.Message == marker),
                TimeSpan.FromSeconds(15));

            await pipeline.StopAsync();
        }

        Assert.Contains(recoveredStore.WrittenRecords, r => r.Message == marker);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        Assert.True(condition(), $"条件が {timeout} 以内に成立しなかった。");
    }

    /// <summary>
    /// 呼び出されたことは記録するが、渡された <see cref="CancellationToken"/> が
    /// キャンセルされるまで決して完了しない <see cref="ILogStore.WriteBatchAsync"/> スタブ
    /// （DB がハングしている状況を模す。PersistenceWriter 側のタイムアウトより先に
    /// 停止要求が来る状況を作るためのもの）。
    /// </summary>
    private sealed class NeverCompletingLogStore : ILogStore
    {
        private volatile bool _writeAttempted;

        public bool WriteAttempted => _writeAttempted;

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken = default)
        {
            _writeAttempted = true;

            // 呼び出し元（PersistenceWriter）のバッチタイムアウトが発火するまで待ち続ける。
            // ここでは長い遅延で「応答のないハング」を模す——テスト側は先に stoppingToken を
            // キャンセルするため、このタスク自体が完了することはない。
            await Task.Delay(TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        }

        public Task<IReadOnlyList<LogRecordSummary>> QueryLatestAsync(
            int limit,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LogRecordSummary>>([]);

        public Task<IReadOnlyList<LogRecordSummary>> QueryAsync(
            LogQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LogRecordSummary>>([]);

        public Task WriteSystemEventAsync(SystemEvent systemEvent, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<DeleteOlderThanResult> DeleteOlderThanAsync(
            DateTimeOffset cutoff,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeleteOlderThanResult(0, cutoff));

        public Task<LogStoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LogStoreStatistics(0, 0));
    }

    private sealed class RecordingLogStore : ILogStore
    {
        public List<LogRecord> WrittenRecords { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken = default)
        {
            lock (WrittenRecords)
            {
                WrittenRecords.AddRange(records);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LogRecordSummary>> QueryLatestAsync(
            int limit,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LogRecordSummary>>([]);

        public Task<IReadOnlyList<LogRecordSummary>> QueryAsync(
            LogQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LogRecordSummary>>([]);

        public Task WriteSystemEventAsync(SystemEvent systemEvent, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<DeleteOlderThanResult> DeleteOlderThanAsync(
            DateTimeOffset cutoff,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeleteOlderThanResult(0, cutoff));

        public Task<LogStoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LogStoreStatistics(0, 0));
    }
}
