using System.Net;
using Microsoft.Extensions.Time.Testing;
using Yagura.Ingestion.FlowControl;

// 名前空間はフォルダ名（FlowControl）ではなく FlowControlTests とする——
// Yagura.Ingestion.Tests 配下に FlowControl を作ると、兄弟テストの
// 「FlowControl.NoopIngressGate」等の相対参照が製品名前空間ではなくテスト側へ解決されて
// しまうため（FlowControlDroppedCounterTests と同じ判断）。
namespace Yagura.Ingestion.Tests.FlowControlTests;

/// <summary>
/// <see cref="TokenBucketIngressGate"/> の単体テスト（architecture.md §3.3。ADR-0002 決定 2。
/// Issue #260）。時刻は <see cref="FakeTimeProvider"/> で決定的に制御する。
/// </summary>
public sealed class TokenBucketIngressGateTests
{
    private static readonly IPAddress SourceA = IPAddress.Parse("192.0.2.1");
    private static readonly IPAddress SourceB = IPAddress.Parse("192.0.2.2");

    private static bool Admit(TokenBucketIngressGate gate, IPAddress source) =>
        gate.ShouldAdmit(source, ReadOnlySpan<byte>.Empty);

    [Fact]
    public void ShouldAdmit_UpToBurstSize_ThenRejects()
    {
        var timeProvider = new FakeTimeProvider();
        var gate = new TokenBucketIngressGate(messagesPerSecond: 10, burstSize: 5, timeProvider);

        // 初期バケットは満杯（バーストぶん）——時間を進めなければちょうど burstSize 件で尽きる。
        for (var i = 0; i < 5; i++)
        {
            Assert.True(Admit(gate, SourceA), $"バースト内の {i + 1} 件目は受け入れられること。");
        }

        Assert.False(Admit(gate, SourceA), "バースト消費後・補充前の追加分は破棄されること。");
    }

    [Fact]
    public void ShouldAdmit_RefillsAtConfiguredRate()
    {
        var timeProvider = new FakeTimeProvider();
        var gate = new TokenBucketIngressGate(messagesPerSecond: 10, burstSize: 5, timeProvider);

        for (var i = 0; i < 5; i++)
        {
            Assert.True(Admit(gate, SourceA));
        }

        Assert.False(Admit(gate, SourceA));

        // 1 秒経過 = 10 件ぶん補充（上限 5 でクリップ）——再び 5 件受け入れられる。
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        for (var i = 0; i < 5; i++)
        {
            Assert.True(Admit(gate, SourceA), $"補充後の {i + 1} 件目は受け入れられること。");
        }

        Assert.False(Admit(gate, SourceA));
    }

    [Fact]
    public void ShouldAdmit_RefillCapsAtBurstSize()
    {
        var timeProvider = new FakeTimeProvider();
        var gate = new TokenBucketIngressGate(messagesPerSecond: 10, burstSize: 3, timeProvider);

        // 長時間の無通信でもバケットは burstSize を超えて溜まらない。
        timeProvider.Advance(TimeSpan.FromHours(1));

        for (var i = 0; i < 3; i++)
        {
            Assert.True(Admit(gate, SourceA));
        }

        Assert.False(Admit(gate, SourceA), "補充はバーストサイズでクリップされること。");
    }

    [Fact]
    public void ShouldAdmit_FractionalRefill_AccumulatesAcrossRejections()
    {
        var timeProvider = new FakeTimeProvider();
        var gate = new TokenBucketIngressGate(messagesPerSecond: 2, burstSize: 1, timeProvider);

        Assert.True(Admit(gate, SourceA));
        Assert.False(Admit(gate, SourceA));

        // 0.25 秒 = 0.5 トークン——まだ 1 に満たず拒否されるが、端数は失われない。
        timeProvider.Advance(TimeSpan.FromMilliseconds(250));
        Assert.False(Admit(gate, SourceA));

        // さらに 0.25 秒で合計 1.0 トークン——受け入れられる（拒否時の判定で端数が
        // リセットされないことの確認）。
        timeProvider.Advance(TimeSpan.FromMilliseconds(250));
        Assert.True(Admit(gate, SourceA));
    }

    [Fact]
    public void ShouldAdmit_SourcesAreIndependent()
    {
        var timeProvider = new FakeTimeProvider();
        var gate = new TokenBucketIngressGate(messagesPerSecond: 10, burstSize: 2, timeProvider);

        Assert.True(Admit(gate, SourceA));
        Assert.True(Admit(gate, SourceA));
        Assert.False(Admit(gate, SourceA), "送信元 A はバースト消費で拒否されること。");

        // 送信元 A の枯渇は送信元 B に影響しない（送信元単位の制限。§3.3）。
        Assert.True(Admit(gate, SourceB));
        Assert.True(Admit(gate, SourceB));
        Assert.False(Admit(gate, SourceB));
    }

    [Fact]
    public void Sweep_RemovesFullyRefilledBuckets()
    {
        var timeProvider = new FakeTimeProvider();
        var gate = new TokenBucketIngressGate(
            messagesPerSecond: 10,
            burstSize: 5,
            maxTrackedSources: 100,
            sweepInterval: TimeSpan.FromSeconds(60),
            timeProvider);

        Assert.True(Admit(gate, SourceA));
        Assert.True(Admit(gate, SourceB));
        Assert.Equal(2, gate.TrackedSourceCount);

        // スイープ周期経過 + 全バケットが満杯まで回復 → 次の判定を契機に削除される。
        timeProvider.Advance(TimeSpan.FromSeconds(61));
        Assert.True(Admit(gate, SourceA));

        // SourceB のバケットは削除済み（SourceA は直前の判定で再生成・消費されたため残る）。
        Assert.Equal(1, gate.TrackedSourceCount);
    }

    [Fact]
    public void Sweep_DoesNotRemoveDepletedBuckets()
    {
        var timeProvider = new FakeTimeProvider();
        var gate = new TokenBucketIngressGate(
            messagesPerSecond: 1,
            burstSize: 100,
            maxTrackedSources: 100,
            sweepInterval: TimeSpan.FromSeconds(60),
            timeProvider);

        // SourceB を大きく消費させる（補充速度 1 件/秒に対し 100 件消費——満杯回復まで遠い）。
        for (var i = 0; i < 100; i++)
        {
            Assert.True(Admit(gate, SourceB));
        }

        timeProvider.Advance(TimeSpan.FromSeconds(61));
        Assert.True(Admit(gate, SourceA));

        // SourceB は満杯（100）まで回復していない（61 秒 × 1 件/秒 = 61 トークン）ため
        // 削除されない——削除すると満杯で再生成され、消費状態がリセットされて制限逃れになる。
        Assert.Equal(2, gate.TrackedSourceCount);

        // 消費状態の維持の証明: 新規バケットなら 100 件受け入れるところ、補充済みの 61 件で尽きる。
        for (var i = 0; i < 61; i++)
        {
            Assert.True(Admit(gate, SourceB), $"補充済みトークン内の {i + 1} 件目は受け入れられること。");
        }

        Assert.False(Admit(gate, SourceB), "スイープを跨いでも消費状態が維持されること。");
    }

    [Fact]
    public void ShouldAdmit_AtTrackedSourceCap_AdmitsNewSourcesWithoutTracking()
    {
        var timeProvider = new FakeTimeProvider();
        var gate = new TokenBucketIngressGate(
            messagesPerSecond: 10,
            burstSize: 1,
            maxTrackedSources: 2,
            sweepInterval: TimeSpan.FromHours(1),
            timeProvider);

        Assert.True(Admit(gate, IPAddress.Parse("198.51.100.1")));
        Assert.True(Admit(gate, IPAddress.Parse("198.51.100.2")));
        Assert.Equal(2, gate.TrackedSourceCount);

        // 追跡上限到達後の新規送信元は fail-open で通す（受信を止めない。クラス remarks 参照）。
        // バースト 1 を消費済みでも、追跡されない送信元は拒否されない。
        var untracked = IPAddress.Parse("198.51.100.3");
        Assert.True(Admit(gate, untracked));
        Assert.True(Admit(gate, untracked));
        Assert.Equal(2, gate.TrackedSourceCount);

        // 追跡済み送信元の制限は上限到達後も維持される（バースト 1 消費済み → 拒否）。
        Assert.False(Admit(gate, IPAddress.Parse("198.51.100.1")));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void Constructor_InvalidArguments_Throws(int messagesPerSecond, int burstSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TokenBucketIngressGate(messagesPerSecond, burstSize));
    }

    [Fact]
    public async Task ShouldAdmit_ConcurrentCallsForSameSource_NeverOverAdmitsBeyondBudget()
    {
        // 並行判定でもバースト + 補充ぶんを超えて受け入れないこと（バケット単位 lock の検証）。
        // 実時計を使わない——FakeTimeProvider は進めないため予算はバーストぶんのみ。
        var timeProvider = new FakeTimeProvider();
        var gate = new TokenBucketIngressGate(messagesPerSecond: 1, burstSize: 1000, timeProvider);

        var admitted = 0;
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                if (gate.ShouldAdmit(SourceA, ReadOnlySpan<byte>.Empty))
                {
                    Interlocked.Increment(ref admitted);
                }
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(1000, Volatile.Read(ref admitted));
    }

    // ---- 送信元別の拒否カウント（Issue #288。IFlowControlRejectionReader） ----

    [Fact]
    public void SnapshotRejectedSources_NoRejections_ReturnsEmpty()
    {
        var timeProvider = new FakeTimeProvider();
        var gate = new TokenBucketIngressGate(messagesPerSecond: 10, burstSize: 5, timeProvider);

        Assert.True(Admit(gate, SourceA));

        // 受け入れのみ（拒否ゼロ）の送信元はスナップショットに載らない。
        Assert.Empty(gate.SnapshotRejectedSources(10));
    }

    [Fact]
    public void SnapshotRejectedSources_CountsPerSource_OrderedByCountDescending()
    {
        var timeProvider = new FakeTimeProvider();
        var gate = new TokenBucketIngressGate(messagesPerSecond: 10, burstSize: 1, timeProvider);

        // SourceA: 拒否 3 件 / SourceB: 拒否 1 件。
        Assert.True(Admit(gate, SourceA));
        Assert.False(Admit(gate, SourceA));
        Assert.False(Admit(gate, SourceA));
        Assert.False(Admit(gate, SourceA));
        Assert.True(Admit(gate, SourceB));
        Assert.False(Admit(gate, SourceB));

        var snapshot = gate.SnapshotRejectedSources(10);

        Assert.Equal(2, snapshot.Count);
        Assert.Equal(SourceA, snapshot[0].SourceAddress);
        Assert.Equal(3, snapshot[0].RejectedCount);
        Assert.Equal(SourceB, snapshot[1].SourceAddress);
        Assert.Equal(1, snapshot[1].RejectedCount);

        // maxCount で上位のみに絞られる。1 未満は空。
        Assert.Equal([SourceA], gate.SnapshotRejectedSources(1).Select(s => s.SourceAddress));
        Assert.Empty(gate.SnapshotRejectedSources(0));
    }

    [Fact]
    public void SnapshotRejectedSources_SweptBucket_DisappearsWithItsCount()
    {
        // 拒否カウントの寿命はバケットと同じ（意図した設計——可視化のために有界化を崩さない。
        // IFlowControlRejectionReader remarks）: 制限なく受信できる状態（満杯まで回復）が
        // スイープ周期続いた送信元は、カウントごと一覧から消える。
        var timeProvider = new FakeTimeProvider();
        var gate = new TokenBucketIngressGate(
            messagesPerSecond: 10,
            burstSize: 1,
            maxTrackedSources: 100,
            sweepInterval: TimeSpan.FromSeconds(60),
            timeProvider);

        Assert.True(Admit(gate, SourceA));
        Assert.False(Admit(gate, SourceA));
        Assert.Single(gate.SnapshotRejectedSources(10));

        // 満杯まで回復 + スイープ周期経過後、別送信元の判定がスイープを駆動する。
        timeProvider.Advance(TimeSpan.FromSeconds(61));
        Assert.True(Admit(gate, SourceB));

        Assert.Empty(gate.SnapshotRejectedSources(10));
    }
}
