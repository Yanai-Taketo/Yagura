using System.Threading.Channels;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.Persistence;
using Yagura.Storage;
using Yagura.Storage.Spool;

namespace Yagura.Ingestion.Tests.Persistence;

/// <summary>
/// スプール上限到達時の破棄・カウンタ計上（architecture.md §3.2.3）と、
/// スプールが無い（縮退運転。§1.2）場合の永続化失敗カウンタ計上を確認する。
/// </summary>
public sealed class PersistenceWriterSpoolLimitTests : IDisposable
{
    private readonly string _spoolDirectory = Path.Combine(Path.GetTempPath(), $"yagura-spoollimit-tests-{Guid.NewGuid():N}");
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
    public async Task WriteBatchFails_SpoolAtQuota_RecordDiscarded_SpoolDiscardedAndPersistenceFailedCountersIncrement()
    {
        // 上限をごく小さく（0 バイト）設定し、追記が必ず QuotaExceeded になる状況を作る。
        _spool = DiskSpool.TryOpen(new DiskSpoolOptions { Directory = _spoolDirectory, QuotaBytes = 0 }, out _);
        Assert.NotNull(_spool);

        var q2 = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var store = new AlwaysThrowingLogStore();

        using var metrics = new IngestionMetrics();
        using var discardedCollector = new MetricCollector<long>(metrics.SpoolDiscardedCounter, timeProvider: null);
        using var persistenceFailedCollector = new MetricCollector<long>(metrics.PersistenceFailedCounter, timeProvider: null);

        var writer = new PersistenceWriter(q2.Reader, store, _spool, metrics);

        using var stoppingCts = new CancellationTokenSource();
        var runTask = Task.Run(() => writer.RunAsync(stoppingCts.Token));

        await q2.Writer.WriteAsync(new LogRecord(
            ReceivedAt: DateTimeOffset.UtcNow,
            SourceAddress: "10.0.0.1",
            SourcePort: 514,
            Protocol: Protocol.Udp,
            ParseStatus: ParseStatus.Parsed,
            Message: "quota-blocked"));

        await discardedCollector.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(10));
        await persistenceFailedCollector.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(10));

        stoppingCts.Cancel();
        await runTask;

        Assert.True(discardedCollector.GetMeasurementSnapshot().Sum(m => m.Value) >= 1);
        Assert.True(persistenceFailedCollector.GetMeasurementSnapshot().Sum(m => m.Value) >= 1);
        Assert.Equal(0, _spool.CurrentUsageBytes);
    }

    [Fact]
    public async Task WriteBatchFails_NoSpool_DegradedMode_PersistenceFailedCounterIncrements()
    {
        var q2 = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var store = new AlwaysThrowingLogStore();

        using var metrics = new IngestionMetrics();
        using var persistenceFailedCollector = new MetricCollector<long>(metrics.PersistenceFailedCounter, timeProvider: null);

        // spool: null ——スプールなし縮退運転（§1.2）。
        var writer = new PersistenceWriter(q2.Reader, store, spool: null, metrics);

        using var stoppingCts = new CancellationTokenSource();
        var runTask = Task.Run(() => writer.RunAsync(stoppingCts.Token));

        await q2.Writer.WriteAsync(new LogRecord(
            ReceivedAt: DateTimeOffset.UtcNow,
            SourceAddress: "10.0.0.1",
            SourcePort: 514,
            Protocol: Protocol.Udp,
            ParseStatus: ParseStatus.Parsed,
            Message: "degraded-mode-loss"));

        await persistenceFailedCollector.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(10));

        stoppingCts.Cancel();
        await runTask;

        Assert.True(persistenceFailedCollector.GetMeasurementSnapshot().Sum(m => m.Value) >= 1);
    }

    private sealed class AlwaysThrowingLogStore : ILogStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken = default) =>
            throw new LogStoreWriteException(LogStoreFailureKind.Permanent, "simulated permanent disk error");

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

        // M8-3 で追加された読み取り専用 3 操作（閲覧画面用の読み取り口）。本テストダブルの
        // 検証対象では使用しないため未対応で明示する。
        public Task<LogRecord?> FindByIdAsync(long id, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("このテストダブルは閲覧用の読み取り操作を対象としない。");

        public Task<IReadOnlyList<SystemEvent>> QuerySystemEventsAsync(DateTimeOffset? from, DateTimeOffset? to, int limit, TimeSpan timeout, string? kind = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("このテストダブルは閲覧用の読み取り操作を対象としない。");

        public Task<IReadOnlyList<SourceActivity>> QuerySourceActivityAsync(int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("このテストダブルは閲覧用の読み取り操作を対象としない。");

        public Task<IReadOnlyList<SeverityCount>> QuerySeverityDistributionAsync(DateTimeOffset from, DateTimeOffset to, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("このテストダブルは閲覧用の読み取り操作を対象としない。");

        public Task<IReadOnlyList<SourceActivity>> QueryTopTalkersAsync(DateTimeOffset from, DateTimeOffset to, int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("このテストダブルは閲覧用の読み取り操作を対象としない。");
    }
}
