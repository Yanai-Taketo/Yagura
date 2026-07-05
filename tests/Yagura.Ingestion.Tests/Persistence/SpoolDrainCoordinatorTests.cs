using System.Threading.Channels;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.Persistence;
using Yagura.Storage;
using Yagura.Storage.Spool;

namespace Yagura.Ingestion.Tests.Persistence;

/// <summary>
/// スプール → drain → DB 到達の結合テスト（architecture.md §3.2.2）。
/// DB を一時的に失敗させるスタブ → 復旧 → スプール分が DB に現れることを確認する。
/// </summary>
public sealed class SpoolDrainCoordinatorTests : IDisposable
{
    private readonly string _spoolDirectory = Path.Combine(Path.GetTempPath(), $"yagura-drain-tests-{Guid.NewGuid():N}");
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
    public async Task SpooledRecords_DrainedAfterStoreRecovers_AppearInStoreAndSegmentIsDeleted()
    {
        _spool = DiskSpool.TryOpen(new DiskSpoolOptions { Directory = _spoolDirectory }, out _);
        Assert.NotNull(_spool);

        // あらかじめスプールへ 3 件退避しておく（Q2 溢れ・書き込み失敗を模したもの、という体）。
        var baseline = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++)
        {
            var record = new LogRecord(
                ReceivedAt: baseline.AddSeconds(i),
                SourceAddress: "10.0.0.1",
                SourcePort: 514,
                Protocol: Protocol.Udp,
                ParseStatus: ParseStatus.Parsed,
                Message: $"spooled-{i}");

            var result = await _spool.TryAppendAsync(SpoolRecord.ForLog(record));
            Assert.Equal(SpoolAppendResult.Appended, result);
        }

        var segmentsBeforeDrain = _spool.TrySealActiveSegmentAndListDrainable();
        Assert.Single(segmentsBeforeDrain);

        // DB は最初の 2 回失敗し、3 回目以降成功するスタブ（「復旧」を模す）。
        var store = new FlakyLogStore(failFirstNAttempts: 2);

        var q2 = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var metrics = new IngestionMetrics();
        var coordinator = new SpoolDrainCoordinator(_spool, q2.Reader, store, metrics);

        using var stoppingCts = new CancellationTokenSource();
        var runTask = Task.Run(() => coordinator.RunAsync(stoppingCts.Token));

        // 復旧後、スプール分の 3 件が DB に現れるまで条件ポーリングで待つ。
        await WaitUntilAsync(() => store.WrittenRecords.Count >= 3, TimeSpan.FromSeconds(15));

        stoppingCts.Cancel();
        await runTask;

        var messages = store.WrittenRecords.Select(r => r.Message).ToList();
        Assert.Contains("spooled-0", messages);
        Assert.Contains("spooled-1", messages);
        Assert.Contains("spooled-2", messages);

        // drain 成功後はセグメントが削除され、スプール使用量が 0 に戻る
        // （architecture.md §3.2.4「消化済みセグメントは通常のファイル削除で速やかに削除する」）。
        await WaitUntilAsync(() => _spool.CurrentUsageBytes == 0, TimeSpan.FromSeconds(15));

        var segmentsAfterDrain = _spool.TrySealActiveSegmentAndListDrainable();
        Assert.Empty(segmentsAfterDrain);
    }

    [Fact]
    public async Task SpooledSelfTestRecord_IsDiscardedBeforeReachingStore()
    {
        _spool = DiskSpool.TryOpen(new DiskSpoolOptions { Directory = _spoolDirectory }, out _);
        Assert.NotNull(_spool);

        var normalRecord = new LogRecord(
            ReceivedAt: DateTimeOffset.UtcNow,
            SourceAddress: "10.0.0.1",
            SourcePort: 514,
            Protocol: Protocol.Udp,
            ParseStatus: ParseStatus.Parsed,
            Message: "normal-record");

        await _spool.TryAppendAsync(SpoolRecord.ForLog(normalRecord));
        await _spool.TryAppendAsync(SpoolRecord.ForSelfTest("self-test-marker"));

        var store = new FlakyLogStore(failFirstNAttempts: 0);

        var q2 = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var metrics = new IngestionMetrics();
        var coordinator = new SpoolDrainCoordinator(_spool, q2.Reader, store, metrics);

        using var stoppingCts = new CancellationTokenSource();
        var runTask = Task.Run(() => coordinator.RunAsync(stoppingCts.Token));

        await WaitUntilAsync(() => store.WrittenRecords.Count >= 1, TimeSpan.FromSeconds(15));

        stoppingCts.Cancel();
        await runTask;

        // 自己検証用の合成レコードは DB 書き込みの直前で破棄され、通常ログのみが到達する
        // （architecture.md §3.2.5「drain は種別を見て DB 書き込みの直前で合成レコードを破棄する」）。
        Assert.Single(store.WrittenRecords);
        Assert.Equal("normal-record", store.WrittenRecords[0].Message);
    }

    [Fact]
    public async Task DrainSegmentWriteFailure_SegmentIsNotReAppendedToSpool_OnlyOriginalSegmentRemains()
    {
        _spool = DiskSpool.TryOpen(new DiskSpoolOptions { Directory = _spoolDirectory }, out _);
        Assert.NotNull(_spool);

        var record = new LogRecord(
            ReceivedAt: DateTimeOffset.UtcNow,
            SourceAddress: "10.0.0.1",
            SourcePort: 514,
            Protocol: Protocol.Udp,
            ParseStatus: ParseStatus.Parsed,
            Message: "always-fails-to-write");

        await _spool.TryAppendAsync(SpoolRecord.ForLog(record));
        var segmentsBefore = _spool.TrySealActiveSegmentAndListDrainable();
        Assert.Single(segmentsBefore);

        // 常に失敗するストア。
        var store = new FlakyLogStore(failFirstNAttempts: int.MaxValue);

        var q2 = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var metrics = new IngestionMetrics();
        var coordinator = new SpoolDrainCoordinator(_spool, q2.Reader, store, metrics);

        using var stoppingCts = new CancellationTokenSource();
        var runTask = Task.Run(() => coordinator.RunAsync(stoppingCts.Token));

        // 複数回書き込みが試みられる（リトライ的にバックオフ後再試行される）まで待つ。
        await WaitUntilAsync(() => store.AttemptCount >= 2, TimeSpan.FromSeconds(15));

        stoppingCts.Cancel();
        await runTask;

        // 書き込みに一度も成功していないため、セグメントは削除されず 1 本のまま
        // （§3.2.2「drain 由来のバッチはスプールへ再追記しない」——複製されず、かつ喪失もしない）。
        var segmentsAfter = _spool.TrySealActiveSegmentAndListDrainable();
        Assert.Single(segmentsAfter);
        Assert.Equal(segmentsBefore[0], segmentsAfter[0]);
        Assert.Empty(store.WrittenRecords);
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
    /// 最初の <paramref name="failFirstNAttempts"/> 回の <see cref="WriteBatchAsync"/> は
    /// 失敗し、以降は成功する ILogStore スタブ（DB の一時的な障害 → 復旧を模す）。
    /// </summary>
    private sealed class FlakyLogStore(int failFirstNAttempts) : ILogStore
    {
        private int _attemptCount;

        public int AttemptCount => Volatile.Read(ref _attemptCount);

        public List<LogRecord> WrittenRecords { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _attemptCount);
            if (attempt <= failFirstNAttempts)
            {
                throw new IOException($"simulated transient disk error (attempt {attempt})");
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
    }
}
