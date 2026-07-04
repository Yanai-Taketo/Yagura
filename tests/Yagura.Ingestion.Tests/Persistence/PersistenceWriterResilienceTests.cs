using System.Threading.Channels;
using Yagura.Ingestion.Persistence;
using Yagura.Storage;

namespace Yagura.Ingestion.Tests.Persistence;

/// <summary>
/// 書き込み例外（一時的なディスクエラー・ロック等）で永続化段の消費ループが
/// 恒久停止しないことの確認（architecture.md §1.2「黙って縮退しない」）。
/// </summary>
public class PersistenceWriterResilienceTests
{
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
        var writer = new PersistenceWriter(q2.Reader, store);

        using var stoppingCts = new CancellationTokenSource();
        var runTask = Task.Run(() => writer.RunAsync(stoppingCts.Token));

        var baseline = DateTimeOffset.UtcNow;

        // 1 件目——書き込み失敗でバッチごと破棄されるが、ループは継続するはず。
        await q2.Writer.WriteAsync(CreateRecord(baseline, "first"));

        // 失敗した 1 回目の書き込み試行が完了するまで条件ポーリングで待つ。
        await WaitUntilAsync(() => store.AttemptCount >= 1, TimeSpan.FromSeconds(10));

        // 2 件目——ループが生きていれば今度は成功して書き込まれるはず。
        await q2.Writer.WriteAsync(CreateRecord(baseline.AddSeconds(1), "second"));

        await WaitUntilAsync(() => store.WrittenRecords.Count >= 1, TimeSpan.FromSeconds(10));

        stoppingCts.Cancel();
        await runTask;

        Assert.Contains(store.WrittenRecords, r => r.Message == "second");
        // 1 件目のバッチは破棄される（M2 の仕様。リトライ・スプール退避は M4）。
        Assert.DoesNotContain(store.WrittenRecords, r => r.Message == "first");
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
                throw new IOException("simulated transient disk error");
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
