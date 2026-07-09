using System.Net;
using Yagura.Ingestion.Net;

namespace Yagura.Ingestion.Tests.Net;

/// <summary>
/// <see cref="DualStackBindAddress"/> の純粋関数部分の単体テスト（Issue #133）。
/// 実ソケットを介した DualMode 受信そのものは
/// <c>Yagura.Ingestion.Tests.Udp.UdpSyslogListenerDualStackTests</c> /
/// <c>Yagura.Ingestion.Tests.Tcp.TcpSyslogListenerDualStackTests</c> で確認する。
/// </summary>
public class DualStackBindAddressTests
{
    [Fact]
    public void IsIPv6Wildcard_IPv6Any_ReturnsTrue()
    {
        Assert.True(DualStackBindAddress.IsIPv6Wildcard(IPAddress.IPv6Any));
        Assert.True(DualStackBindAddress.IsIPv6Wildcard(IPAddress.Parse("::")));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    public void IsIPv6Wildcard_NonWildcardAddress_ReturnsFalse(string address)
    {
        Assert.False(DualStackBindAddress.IsIPv6Wildcard(IPAddress.Parse(address)));
    }

    [Fact]
    public void NormalizeSourceAddress_IPv4MappedIPv6_MapsToPlainIPv4()
    {
        var mapped = IPAddress.Parse("::ffff:127.0.0.1");

        var normalized = DualStackBindAddress.NormalizeSourceAddress(mapped);

        Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, normalized.AddressFamily);
        Assert.Equal("127.0.0.1", normalized.ToString());
    }

    [Theory]
    [InlineData("192.168.1.10")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    public void NormalizeSourceAddress_NonMappedAddress_ReturnsUnchanged(string address)
    {
        var original = IPAddress.Parse(address);

        var normalized = DualStackBindAddress.NormalizeSourceAddress(original);

        Assert.Equal(original, normalized);
    }
}
