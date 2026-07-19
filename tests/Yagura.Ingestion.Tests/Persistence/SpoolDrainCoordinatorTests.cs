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

        var segmentsBeforeDrain = _spool.SealActiveSegmentAndListDrainable();
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

        var segmentsAfterDrain = _spool.SealActiveSegmentAndListDrainable();
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
    public async Task SpooledSelfTestRecord_NotifiesSelfTestTracker_WithMatchingMarker()
    {
        // 定期自己検証（architecture.md §3.2.5。Issue #152）の照合機構: drain が自己検証の
        // 合成レコードを破棄する際、SpoolSelfTestTracker へ通知することを検証する。
        _spool = DiskSpool.TryOpen(new DiskSpoolOptions { Directory = _spoolDirectory }, out _);
        Assert.NotNull(_spool);

        var tracker = new SpoolSelfTestTracker();
        var marker = tracker.BeginNewMarker(DateTimeOffset.UtcNow);

        await _spool.TryAppendAsync(SpoolRecord.ForSelfTest(marker));

        var store = new FlakyLogStore(failFirstNAttempts: 0);
        var q2 = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var metrics = new IngestionMetrics();
        var coordinator = new SpoolDrainCoordinator(_spool, q2.Reader, store, metrics, selfTestTracker: tracker);

        using var stoppingCts = new CancellationTokenSource();
        var runTask = Task.Run(() => coordinator.RunAsync(stoppingCts.Token));

        // 照合済み（未照合が解消される = タイムアウト判定が false のまま）になるまで待つ。
        await WaitUntilAsync(() => !tracker.IsPendingTimedOut(DateTimeOffset.UtcNow, TimeSpan.Zero), TimeSpan.FromSeconds(15));

        stoppingCts.Cancel();
        await runTask;

        Assert.False(tracker.IsPendingTimedOut(DateTimeOffset.UtcNow, TimeSpan.Zero));
        Assert.Empty(store.WrittenRecords);
    }

    [Fact]
    public async Task SpooledSelfTestRecord_NoTrackerPassed_DrainStillDiscardsWithoutError()
    {
        // トラッカー未指定（null）でも drain 自体の識別・破棄動作は変わらない
        // （検証の有無は drain の正しさに影響しない。SpoolDrainCoordinator の remarks 参照）。
        _spool = DiskSpool.TryOpen(new DiskSpoolOptions { Directory = _spoolDirectory }, out _);
        Assert.NotNull(_spool);

        await _spool.TryAppendAsync(SpoolRecord.ForSelfTest("unobserved-marker"));

        var normalRecord = new LogRecord(
            ReceivedAt: DateTimeOffset.UtcNow,
            SourceAddress: "10.0.0.1",
            SourcePort: 514,
            Protocol: Protocol.Udp,
            ParseStatus: ParseStatus.Parsed,
            Message: "normal-record-2");
        await _spool.TryAppendAsync(SpoolRecord.ForLog(normalRecord));

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

        Assert.Single(store.WrittenRecords);
        Assert.Equal("normal-record-2", store.WrittenRecords[0].Message);
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
        var segmentsBefore = _spool.SealActiveSegmentAndListDrainable();
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
        var segmentsAfter = _spool.SealActiveSegmentAndListDrainable();
        Assert.Single(segmentsAfter);
        Assert.Equal(segmentsBefore[0], segmentsAfter[0]);
        Assert.Empty(store.WrittenRecords);
    }

    // ------------------------------------------------------------------
    // Issue #201: スプール末尾破損（corruptTailDetected）を drain がどのカウンタにも計上せず
    // セグメントを削除していたギャップの回帰テスト。
    // ------------------------------------------------------------------

    [Fact]
    public async Task DrainSegmentWithCorruptTail_RecordsDiscardedBytesAndDrainsRecoveredRecords()
    {
        _spool = DiskSpool.TryOpen(new DiskSpoolOptions { Directory = _spoolDirectory }, out _);
        Assert.NotNull(_spool);

        var record = new LogRecord(
            ReceivedAt: DateTimeOffset.UtcNow,
            SourceAddress: "10.0.0.1",
            SourcePort: 514,
            Protocol: Protocol.Udp,
            ParseStatus: ParseStatus.Parsed,
            Message: "recovered-record");

        Assert.Equal(SpoolAppendResult.Appended, await _spool.TryAppendAsync(SpoolRecord.ForLog(record)));

        var segments = _spool.SealActiveSegmentAndListDrainable();
        var segmentPath = Assert.Single(segments);

        // 正常な 1 件の直後に、中途半端な末尾バイト（不完全なフレーム）を追記する
        // ——クラッシュ・強制終了で末尾が中途半端に切れた状況を模擬する
        // （DiskSpoolTests.ReadSegmentRecords_TruncatedTailAfterNRecords_... と同じ模擬）。
        using (var stream = new FileStream(segmentPath, FileMode.Append, FileAccess.Write))
        {
            var partialLengthPrefix = new byte[] { 0x10, 0x00, 0x00, 0x00 }; // 長さプレフィックスのみで本体が続かない
            stream.Write(partialLengthPrefix);
        }

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

        // 回収できた正常レコードは drain され、セグメントは消化済みとして削除される
        // （破損があっても回収できた分は救う。§3.2.1）。
        Assert.Single(store.WrittenRecords);
        Assert.Equal("recovered-record", store.WrittenRecords[0].Message);
        await WaitUntilAsync(() => _spool.CurrentUsageBytes == 0, TimeSpan.FromSeconds(15));

        // 末尾破損として読み捨てた 4 バイト（追記した中途半端な長さプレフィックス）が
        // 専用カウンタへ計上される（Issue #201。従来はどのカウンタにも計上されなかった）。
        var snapshot = metrics.SnapshotCumulativeCounters();
        Assert.Equal(4, snapshot.SpoolCorruptTailDiscardedBytes);
    }

    [Fact]
    public async Task DrainSegmentWithCorruptTail_WriteFailsThenRecovers_DiscardedBytesAreCountedOnlyOnce()
    {
        // 末尾破損の計上は DeleteSegment 確定直前でのみ行う設計（SpoolDrainCoordinator クラス
        // remarks 参照）——書き込み失敗による再試行のたびに同じ破損セグメントを再読み込みしても、
        // 破損バイト数が重複計上されないことを固定化する回帰テスト。
        _spool = DiskSpool.TryOpen(new DiskSpoolOptions { Directory = _spoolDirectory }, out _);
        Assert.NotNull(_spool);

        var record = new LogRecord(
            ReceivedAt: DateTimeOffset.UtcNow,
            SourceAddress: "10.0.0.1",
            SourcePort: 514,
            Protocol: Protocol.Udp,
            ParseStatus: ParseStatus.Parsed,
            Message: "recovered-after-retry");

        Assert.Equal(SpoolAppendResult.Appended, await _spool.TryAppendAsync(SpoolRecord.ForLog(record)));

        var segments = _spool.SealActiveSegmentAndListDrainable();
        var segmentPath = Assert.Single(segments);

        using (var stream = new FileStream(segmentPath, FileMode.Append, FileAccess.Write))
        {
            var partialLengthPrefix = new byte[] { 0x10, 0x00, 0x00, 0x00 };
            stream.Write(partialLengthPrefix);
        }

        // 最初の 2 回は失敗し、3 回目以降で成功する（同じ破損セグメントが複数回再読み込みされる
        // 状況を作る）。
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

        await WaitUntilAsync(() => store.WrittenRecords.Count >= 1, TimeSpan.FromSeconds(15));
        // 失敗 → 再試行が実際に複数回起きたことを確認する（このテストの前提条件）。
        Assert.True(store.AttemptCount >= 3, $"AttemptCount was {store.AttemptCount}, expected at least 3 (2 failures + 1 success).");

        stoppingCts.Cancel();
        await runTask;

        await WaitUntilAsync(() => _spool.CurrentUsageBytes == 0, TimeSpan.FromSeconds(15));

        // 複数回読み直されたにもかかわらず、破損バイト数は 4（1 回分）のまま
        // ——8・12 等の重複計上になっていないことを確認する。
        var snapshot = metrics.SnapshotCumulativeCounters();
        Assert.Equal(4, snapshot.SpoolCorruptTailDiscardedBytes);
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
                throw new LogStoreWriteException(
                    LogStoreFailureKind.Transient,
                    $"simulated transient disk error (attempt {attempt})");
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
