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
}
