using System.Threading.Channels;
using Yagura.Host.Retention;
using Yagura.Ingestion;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.Persistence;
using Yagura.Storage;
using Yagura.Storage.Spool;

namespace Yagura.Host.Tests.Retention;

/// <summary>
/// 書き込みゲート（Issue #151。<see cref="LogStoreWriteGate"/>）による 3 経路
/// ——ライブ書き込み（<see cref="PersistenceWriter"/>）・スプール drain
/// （<see cref="SpoolDrainCoordinator"/>）・保持期間削除（<see cref="RetentionScheduler"/>）——
/// の直列化を、真の並行実行で検証する結合テスト。
/// </summary>
/// <remarks>
/// <see cref="ILogStore"/> の契約（「書き込みは呼び出し側が直列化する」）が実配線でも成立する
/// ことの機械検証——ゲートが無い場合、本テストの 3 経路は独立タスクから並行に書き込み系
/// メソッドを呼び出し得る（Issue #151 の症状そのもの）。ストア側で並行呼び出しを検出したら
/// 失敗にする。
/// </remarks>
public sealed class WriteGateSerializationTests : IDisposable
{
    private readonly string _spoolDirectory = Path.Combine(Path.GetTempPath(), $"yagura-write-gate-tests-{Guid.NewGuid():N}");
    private DiskSpool? _spool;

    public void Dispose()
    {
        _spool?.Dispose();

        if (Directory.Exists(_spoolDirectory))
        {
            Directory.Delete(_spoolDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LiveWriteDrainAndRetentionDelete_RunningConcurrently_NeverOverlapOnStore()
    {
        using var gate = new LogStoreWriteGate();
        var store = new ConcurrencyAssertingLogStore();

        // --- drain 経路: あらかじめスプールへ退避分を作っておく。
        _spool = DiskSpool.TryOpen(new DiskSpoolOptions { Directory = _spoolDirectory }, out _);
        Assert.NotNull(_spool);
        var baseline = DateTimeOffset.UtcNow;
        for (var i = 0; i < 30; i++)
        {
            var appendResult = await _spool.TryAppendAsync(SpoolRecord.ForLog(CreateRecord(baseline, $"spooled-{i}")));
            Assert.Equal(SpoolAppendResult.Appended, appendResult);
        }

        // --- ライブ経路: Q2 へ投入する。
        var q2 = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(PipelineConstants.Q2Capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var metrics = new IngestionMetrics();
        var persistenceWriter = new PersistenceWriter(
            q2.Reader, store, _spool, metrics, logger: null, capacityExhaustionHandler: null, writeGate: gate);
        var drainCoordinator = new SpoolDrainCoordinator(
            _spool, q2.Reader, store, metrics, logger: null, capacityExhaustionHandler: null, writeGate: gate);

        // --- 削除経路: 容量枯渇契機の前倒し実行を繰り返し発火させる。
        await using var scheduler = new RetentionScheduler(
            store,
            new RetentionSchedulerOptions(RetentionDays: 7, RetentionSchedulerOptions.DefaultExecutionTimeOfDay),
            writeGate: gate);

        using var stoppingCts = new CancellationTokenSource();
        var persistenceTask = Task.Run(() => persistenceWriter.RunAsync(stoppingCts.Token));
        var drainTask = Task.Run(() => drainCoordinator.RunAsync(stoppingCts.Token));

        var liveProducer = Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                await q2.Writer.WriteAsync(CreateRecord(DateTimeOffset.UtcNow, $"live-{i}"));

                if (i % 40 == 0)
                {
                    // ライブ・drain の書き込みと重なるタイミングで削除を差し込む。
                    scheduler.OnCapacityExhausted();
                    await Task.Delay(20);
                }
            }
        });

        await liveProducer;

        // ライブ 200 件 + drain 30 件 + 削除 1 回以上が完了するまで待つ。
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while ((store.WrittenRecordCount < 230 || store.DeleteCallCount == 0) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        stoppingCts.Cancel();
        await Task.WhenAll(persistenceTask, drainTask);

        Assert.True(store.WrittenRecordCount >= 230, $"書き込み件数が不足: {store.WrittenRecordCount}");
        Assert.True(store.DeleteCallCount >= 1, "保持期間削除が 1 回も実行されなかった。");
        Assert.Equal(1, store.MaxObservedConcurrency);
    }

    private static LogRecord CreateRecord(DateTimeOffset receivedAt, string message) =>
        new(
            ReceivedAt: receivedAt,
            SourceAddress: "10.0.0.1",
            SourcePort: 514,
            Protocol: Protocol.Udp,
            ParseStatus: ParseStatus.Parsed,
            Message: message);

    /// <summary>
    /// 書き込み系操作（WriteBatch / WriteSystemEvent / DeleteOlderThan）の並行実行を検出する
    /// フェイク。各操作は意図的に短い待機を入れ、直列化されていなければ重なりが観測される
    /// ようにする。
    /// </summary>
    private sealed class ConcurrencyAssertingLogStore : ILogStore
    {
        private int _activeWriters;
        private int _maxObservedConcurrency;
        private int _writtenRecordCount;
        private int _deleteCallCount;

        public int MaxObservedConcurrency => Volatile.Read(ref _maxObservedConcurrency);
        public int WrittenRecordCount => Volatile.Read(ref _writtenRecordCount);
        public int DeleteCallCount => Volatile.Read(ref _deleteCallCount);

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken = default)
        {
            await TrackWriteAsync().ConfigureAwait(false);
            Interlocked.Add(ref _writtenRecordCount, records.Count);
        }

        public async Task WriteSystemEventAsync(SystemEvent systemEvent, CancellationToken cancellationToken = default)
        {
            await TrackWriteAsync().ConfigureAwait(false);
        }

        public async Task<DeleteOlderThanResult> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        {
            await TrackWriteAsync().ConfigureAwait(false);
            Interlocked.Increment(ref _deleteCallCount);
            return new DeleteOlderThanResult(DeletedCount: 0, Cutoff: cutoff);
        }

        private async Task TrackWriteAsync()
        {
            var current = Interlocked.Increment(ref _activeWriters);
            UpdateMax(ref _maxObservedConcurrency, current);
            try
            {
                // 直列化されていなければ他経路の書き込みと重なる時間窓を作る。
                await Task.Delay(5).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _activeWriters);
            }
        }

        private static void UpdateMax(ref int location, int value)
        {
            int current;
            while ((current = Volatile.Read(ref location)) < value &&
                   Interlocked.CompareExchange(ref location, value, current) != current)
            {
            }
        }

        public Task<IReadOnlyList<LogRecordSummary>> QueryLatestAsync(int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LogRecordSummary>>(Array.Empty<LogRecordSummary>());

        public Task<IReadOnlyList<LogRecordSummary>> QueryAsync(LogQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LogRecordSummary>>(Array.Empty<LogRecordSummary>());

        public Task<LogStoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LogStoreStatistics(RecordCount: 0, DatabaseSizeBytes: null, WalSizeBytes: null));

        public Task<LogRecord?> FindByIdAsync(long id, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<LogRecord?>(null);

        public Task<IReadOnlyList<SystemEvent>> QuerySystemEventsAsync(DateTimeOffset? from, DateTimeOffset? to, int limit, TimeSpan timeout, string? kind = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SystemEvent>>(Array.Empty<SystemEvent>());

        public Task<IReadOnlyList<SourceActivity>> QuerySourceActivityAsync(int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SourceActivity>>(Array.Empty<SourceActivity>());

        public Task<IReadOnlyList<SeverityCount>> QuerySeverityDistributionAsync(DateTimeOffset from, DateTimeOffset to, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SeverityCount>>(Array.Empty<SeverityCount>());

        public Task<IReadOnlyList<SourceActivity>> QueryTopTalkersAsync(DateTimeOffset from, DateTimeOffset to, int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SourceActivity>>(Array.Empty<SourceActivity>());
    }
}
