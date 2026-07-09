namespace Yagura.Storage.Tests;

/// <summary>
/// <see cref="LogStoreWriteGate"/>（Issue #151。ライブ書き込み・drain・保持期間削除の 3 経路を
/// 直列化するプロセス内書き込みゲート）の単体テスト。
/// </summary>
public sealed class LogStoreWriteGateTests
{
    [Fact]
    public async Task AcquireAsync_WhenFree_CompletesImmediately()
    {
        using var gate = new LogStoreWriteGate();

        using var lease = await gate.AcquireAsync(CancellationToken.None);

        Assert.NotNull(lease);
    }

    [Fact]
    public async Task AcquireAsync_WithTimeout_WhenHeld_ThrowsGateTimeoutException()
    {
        using var gate = new LogStoreWriteGate();
        using var lease = await gate.AcquireAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<LogStoreWriteGateTimeoutException>(() =>
            gate.AcquireAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));

        Assert.Equal(TimeSpan.FromMilliseconds(50), ex.Timeout);
    }

    [Fact]
    public async Task AcquireAsync_WithTimeout_AfterRelease_Succeeds()
    {
        using var gate = new LogStoreWriteGate();
        var lease = await gate.AcquireAsync(CancellationToken.None);

        var second = gate.AcquireAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.False(second.IsCompleted);

        lease.Dispose();

        using var secondLease = await second;
        Assert.NotNull(secondLease);
    }

    [Fact]
    public async Task AcquireAsync_CallerCancellation_ThrowsOperationCanceledNotGateTimeout()
    {
        // 呼び出し側のキャンセル(停止要求)はゲート待ちタイムアウトと区別されて伝わること
        // ——呼び出し元(PersistenceWriter)が停止要求とゲート競合を別の経路で扱うための前提。
        using var gate = new LogStoreWriteGate();
        using var lease = await gate.AcquireAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.AcquireAsync(TimeSpan.FromSeconds(30), cts.Token));

        Assert.IsNotType<LogStoreWriteGateTimeoutException>(ex);
    }

    [Fact]
    public async Task Lease_DoubleDispose_DoesNotOverRelease()
    {
        using var gate = new LogStoreWriteGate();

        var lease = await gate.AcquireAsync(CancellationToken.None);
        lease.Dispose();
        lease.Dispose(); // 二重解放してもセマフォのカウントは 1 を超えない。

        // 二重解放でカウントが 2 になっていれば、2 つ同時に取得できてしまう——
        // 1 つ目を保持したまま 2 つ目がタイムアウトすることで、ゲートが二値のままであることを検証する。
        using var first = await gate.AcquireAsync(CancellationToken.None);
        await Assert.ThrowsAsync<LogStoreWriteGateTimeoutException>(() =>
            gate.AcquireAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
    }

    [Fact]
    public async Task AcquireAsync_ConcurrentHolders_AreSerialized()
    {
        using var gate = new LogStoreWriteGate();
        var active = 0;
        var maxObserved = 0;

        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            for (var i = 0; i < 20; i++)
            {
                using var lease = await gate.AcquireAsync(CancellationToken.None);
                var current = Interlocked.Increment(ref active);
                InterlockedExtensionsMax(ref maxObserved, current);
                await Task.Yield();
                Interlocked.Decrement(ref active);
            }
        }).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(1, maxObserved);
    }

    private static void InterlockedExtensionsMax(ref int location, int value)
    {
        int current;
        while ((current = Volatile.Read(ref location)) < value &&
               Interlocked.CompareExchange(ref location, value, current) != current)
        {
        }
    }
}
