using System.Net;
using Yagura.Ingestion.FlowControl;

namespace Yagura.Ingestion.Tests.FlowControlTests;

/// <summary>
/// <see cref="SwappableIngressGate"/> の単体テスト（CF-4 層1。Issue #262）。
/// </summary>
public sealed class SwappableIngressGateTests
{
    private sealed class FixedGate(bool verdict) : IIngressGate
    {
        public bool ShouldAdmit(IPAddress sourceAddress, ReadOnlySpan<byte> payload) => verdict;
    }

    [Fact]
    public void ShouldAdmit_DelegatesToCurrentImplementation_AndSwapTakesEffect()
    {
        var gate = new SwappableIngressGate(new FixedGate(verdict: true));
        Assert.True(gate.ShouldAdmit(IPAddress.Loopback, ReadOnlySpan<byte>.Empty));

        var replacement = new FixedGate(verdict: false);
        gate.Swap(replacement);

        Assert.False(gate.ShouldAdmit(IPAddress.Loopback, ReadOnlySpan<byte>.Empty));
        Assert.Same(replacement, gate.Current);
    }

    [Fact]
    public void Constructor_And_Swap_RejectNull()
    {
        Assert.Throws<ArgumentNullException>(() => new SwappableIngressGate(null!));
        var gate = new SwappableIngressGate(new FixedGate(true));
        Assert.Throws<ArgumentNullException>(() => gate.Swap(null!));
    }

    [Fact]
    public void SnapshotRejectedSources_DelegatesToCurrent_AndReturnsEmptyForNonReader()
    {
        // Issue #288: 現在の実装が読み取り口（IFlowControlRejectionReader）を持つ場合は委譲し、
        // 持たない場合（NoopIngressGate = 流量制御 opt-out）は空を返す。
        var tokenBucket = new TokenBucketIngressGate(messagesPerSecond: 10, burstSize: 1);
        var gate = new SwappableIngressGate(tokenBucket);

        Assert.True(gate.ShouldAdmit(IPAddress.Loopback, ReadOnlySpan<byte>.Empty));
        Assert.False(gate.ShouldAdmit(IPAddress.Loopback, ReadOnlySpan<byte>.Empty));
        var rejected = Assert.Single(gate.SnapshotRejectedSources(10));
        Assert.Equal(IPAddress.Loopback, rejected.SourceAddress);
        Assert.Equal(1, rejected.RejectedCount);

        // 差し替えで旧ゲートのバケット状態（拒否カウント込み）は捨てられる。
        gate.Swap(new NoopIngressGate());
        Assert.Empty(gate.SnapshotRejectedSources(10));
    }
}
