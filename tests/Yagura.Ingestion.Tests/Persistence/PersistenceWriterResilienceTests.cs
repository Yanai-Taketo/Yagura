using System.Threading.Channels;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.Persistence;
using Yagura.Storage;
using Yagura.Storage.Spool;

namespace Yagura.Ingestion.Tests.Persistence;

/// <summary>
/// 書き込み例外（一時的なディスクエラー・ロック等）で永続化段の消費ループが
/// 恒久停止しないことの確認（architecture.md §1.2「黙って縮退しない」）。
/// M4-3 以降、書き込み失敗バッチは破棄ではなくスプールへ退避する
/// （architecture.md §3.2.1。PR #28 オーナー確認事項 2 の解消）。
/// </summary>
public sealed class PersistenceWriterResilienceTests : IDisposable
{
    private readonly string _spoolDirectory = Path.Combine(Path.GetTempPath(), $"yagura-spool-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_spoolDirectory))
        {
            Directory.Delete(_spoolDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteBatchThrows_LoopContinues_AndSubsequentBatchIsWritten()
    {
        var q2 = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        // 1 回目の WriteBatchAsync は例外、2 回目以降は成功する ILogStore スタブ。
        var store = new ThrowOnceLogStore();
        var spool = DiskSpool.TryOpen(new DiskSpoolOptions { Directory = _spoolDirectory }, out _);
        Assert.NotNull(spool);

        using var metrics = new IngestionMetrics();
        var writer = new PersistenceWriter(q2.Reader, store, spool, metrics);

        using var stoppingCts = new CancellationTokenSource();
        var runTask = Task.Run(() => writer.RunAsync(stoppingCts.Token));

        var baseline = DateTimeOffset.UtcNow;

        // 1 件目——書き込み失敗でバッチごとスプールへ退避されるが、ループは継続するはず。
        await q2.Writer.WriteAsync(CreateRecord(baseline, "first"));

        // 失敗した 1 回目の書き込み試行が完了するまで条件ポーリングで待つ。
        await WaitUntilAsync(() => store.AttemptCount >= 1, TimeSpan.FromSeconds(10));

        // 2 件目——ループが生きていれば今度は成功して書き込まれるはず。
        await q2.Writer.WriteAsync(CreateRecord(baseline.AddSeconds(1), "second"));

        await WaitUntilAsync(() => store.WrittenRecords.Count >= 1, TimeSpan.FromSeconds(10));

        // 1 件目はスプールへ退避されているはず（drain コーディネータは動かしていないため
        // セグメントファイルとして残る）。
        await WaitUntilAsync(() => spool!.CurrentUsageBytes > 0, TimeSpan.FromSeconds(10));

        stoppingCts.Cancel();
        await runTask;

        Assert.Contains(store.WrittenRecords, r => r.Message == "second");
        // 1 件目のバッチは DB には現れない（スプールへ退避されたため）。
        Assert.DoesNotContain(store.WrittenRecords, r => r.Message == "first");

        // スプールから実際に読み戻し、"first" が退避されていることを直接確認する。
        var segments = spool!.TrySealActiveSegmentAndListDrainable();
        var spooledMessages = segments
            .SelectMany(path => spool.ReadSegmentRecords(path, out _))
            .Where(r => r.Kind == SpoolRecordKind.Normal)
            .Select(r => r.LogRecord!.Message)
            .ToList();

        Assert.Contains("first", spooledMessages);
    }

    private static LogRecord CreateRecord(DateTimeOffset receivedAt, string message) =>
        new(
            ReceivedAt: receivedAt,
            SourceAddress: "10.0.0.1",
            SourcePort: 514,
            Protocol: Protocol.Udp,
            ParseStatus: ParseStatus.Parsed,
            Facility: 1,
            Severity: 5,
            Message: message);

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
    /// 1 回目の <see cref="WriteBatchAsync"/> のみ失敗し、以降は成功する ILogStore スタブ。
    /// </summary>
    private sealed class ThrowOnceLogStore : ILogStore
    {
        private int _attemptCount;

        public int AttemptCount => Volatile.Read(ref _attemptCount);

        public List<LogRecord> WrittenRecords { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _attemptCount) == 1)
            {
                throw new LogStoreWriteException(LogStoreFailureKind.Transient, "simulated transient disk error");
            }

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

        // M8-3 で追加された読み取り専用 3 操作（閲覧画面用の読み取り口）。本テストダブルの
        // 検証対象では使用しないため未対応で明示する。
        public Task<LogRecord?> FindByIdAsync(long id, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("このテストダブルは閲覧用の読み取り操作を対象としない。");

        public Task<IReadOnlyList<SystemEvent>> QuerySystemEventsAsync(DateTimeOffset? from, DateTimeOffset? to, int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("このテストダブルは閲覧用の読み取り操作を対象としない。");

        public Task<IReadOnlyList<SourceActivity>> QuerySourceActivityAsync(int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("このテストダブルは閲覧用の読み取り操作を対象としない。");
    }
}
