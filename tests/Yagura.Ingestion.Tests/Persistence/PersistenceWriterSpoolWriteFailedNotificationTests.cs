using System.Threading.Channels;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.Persistence;
using Yagura.Storage;
using Yagura.Storage.Spool;

namespace Yagura.Ingestion.Tests.Persistence;

/// <summary>
/// スプール書込失敗の即時通知（architecture.md §4.6。Issue #149）: 発生箇所
/// （<see cref="PersistenceWriter.EvacuateSingleRecordAsync"/>）からイベント ID 1005 で警告し、
/// 抑制窓（<see cref="FakeTimeProvider"/> により決定的に検証）で連発を抑える。
/// </summary>
public sealed class PersistenceWriterSpoolWriteFailedNotificationTests : IDisposable
{
    private readonly string _spoolDirectory = Path.Combine(Path.GetTempPath(), $"yagura-spoolwritefail-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_spoolDirectory))
        {
            Directory.Delete(_spoolDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SpoolWriteAlwaysFails_WarnsWithEventId1005_SuppressesWithinWindow_WarnsAgainAfterWindow()
    {
        var spool = DiskSpool.TryOpen(new DiskSpoolOptions { Directory = _spoolDirectory }, out _);
        Assert.NotNull(spool);

        // TryOpen 直後にディレクトリ自体を消し、以降の追記（FileStream の CreateNew）が
        // DirectoryNotFoundException（IOException のサブクラス）で必ず失敗する状況を作る
        // （DiskSpool.AppendFrameUnderGate はリトライ後に SpoolAppendResult.WriteFailed を返す）。
        Directory.Delete(_spoolDirectory, recursive: true);

        var q2 = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var store = new AlwaysThrowingLogStore();

        using var metrics = new IngestionMetrics();
        using var writeFailedCollector = new MetricCollector<long>(metrics.SpoolWriteFailedCounter, timeProvider: null);

        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-09T00:00:00Z"));
        var logCollector = new FakeLogCollector();
        var logger = new FakeLogger<PersistenceWriter>(logCollector);

        var writer = new PersistenceWriter(
            q2.Reader,
            store,
            spool,
            metrics,
            logger,
            capacityExhaustionHandler: null,
            timeProvider: timeProvider);

        using var stoppingCts = new CancellationTokenSource();
        var runTask = Task.Run(() => writer.RunAsync(stoppingCts.Token));

        // 1 件目——スプール書込失敗が発生し、警告が 1 件記録されるはず。
        // カウンタ計上（MeterListener 経由の通知）とログ書き込みは同一の同期処理内だが別スレッド
        // （テストスレッド）から見ると到達順序が保証されないため、カウンタでの待機だけでなく
        // ログ側も条件ポーリングで待つ（固定 sleep は使わない）。
        await q2.Writer.WriteAsync(CreateRecord("first"));
        await writeFailedCollector.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(10));
        await WaitForLogCountAsync(logCollector, 1);

        Assert.Equal(1, logCollector.GetSnapshot().Count(r => r.Id.Id == 1005));
        Assert.Contains(logCollector.GetSnapshot(), r => r.Id.Id == 1005 && r.Message.Contains("[spool-write-failed]"));

        // 2 件目——同一時刻（抑制窓 5 分の仮値以内）のため、カウンタは増えるが警告は抑制される。
        await q2.Writer.WriteAsync(CreateRecord("second"));
        await writeFailedCollector.WaitForMeasurementsAsync(minCount: 2, timeout: TimeSpan.FromSeconds(10));

        // 抑制されていることの確認は「増えない」ことの確認のため、猶予を置いてから判定する。
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.Equal(1, logCollector.GetSnapshot().Count(r => r.Id.Id == 1005));

        // 抑制窓を超えたら再度警告される。
        timeProvider.Advance(TimeSpan.FromMinutes(6));

        await q2.Writer.WriteAsync(CreateRecord("third"));
        await writeFailedCollector.WaitForMeasurementsAsync(minCount: 3, timeout: TimeSpan.FromSeconds(10));
        await WaitForLogCountAsync(logCollector, 2);

        Assert.Equal(2, logCollector.GetSnapshot().Count(r => r.Id.Id == 1005));

        stoppingCts.Cancel();
        await runTask;
    }

    [Fact]
    public async Task PermanentWriteFailure_WarnsWithEventId1030()
    {
        // ADR-0017 委任 10（Issue #369）: 恒久障害の開始通知は EventId 1030 を持つ——
        // 採番なし（= 0）のままだとメール通知プロバイダが構造的に捕捉できず、
        // イベントログの機械照合もできない。
        var spool = DiskSpool.TryOpen(new DiskSpoolOptions { Directory = _spoolDirectory }, out _);
        Assert.NotNull(spool);

        var q2 = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var metrics = new IngestionMetrics();
        var logCollector = new FakeLogCollector();

        var writer = new PersistenceWriter(
            q2.Reader,
            new AlwaysThrowingLogStore(),
            spool,
            metrics,
            new FakeLogger<PersistenceWriter>(logCollector),
            capacityExhaustionHandler: null,
            timeProvider: new FakeTimeProvider(DateTimeOffset.Parse("2026-07-20T00:00:00Z")));

        using var stoppingCts = new CancellationTokenSource();
        var runTask = Task.Run(() => writer.RunAsync(stoppingCts.Token));

        await q2.Writer.WriteAsync(CreateRecord("permanent"));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!logCollector.GetSnapshot().Any(r => r.Message.Contains("[permanent-failure]")) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        var record = Assert.Single(logCollector.GetSnapshot(), r => r.Message.Contains("[permanent-failure]"));
        Assert.Equal(1030, record.Id.Id);

        stoppingCts.Cancel();
        await runTask;
        spool!.Dispose();
    }

    /// <summary>ログスナップショットが指定件数（EventId 1005）に達するまで条件ポーリングで待つ。</summary>
    private static async Task WaitForLogCountAsync(FakeLogCollector collector, int expectedCount)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (collector.GetSnapshot().Count(r => r.Id.Id == 1005) < expectedCount && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    private static LogRecord CreateRecord(string message) => new(
        ReceivedAt: DateTimeOffset.UtcNow,
        SourceAddress: "10.0.0.1",
        SourcePort: 514,
        Protocol: Protocol.Udp,
        ParseStatus: ParseStatus.Parsed,
        Facility: 1,
        Severity: 5,
        Message: message);

    /// <summary>常に <see cref="LogStoreFailureKind.Permanent"/> で失敗する ILogStore スタブ（退避を必ず誘発する）。</summary>
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
