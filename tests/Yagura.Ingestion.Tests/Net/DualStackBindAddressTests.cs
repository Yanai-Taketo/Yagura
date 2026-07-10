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

    // ------------------------------------------------------------------
    // IPv6 不可の環境での IPv4 縮小の判定（PR #193 レビュー指摘 Major への対応）。
    // Socket.OSSupportsIPv6 = false や AddressFamilyNotSupported は IPv6 有効の開発機・CI
    // では実挙動として再現できないため、判定を純粋関数として切り出して全分岐を固定する
    // （bind 実試行側の分岐も同じ入力——明示指定フラグ——で分かれる。実装は
    // UdpSyslogListener.CreateDualModeUdpClientOrFallBack / TcpSyslogListener.
    // CreateDualModeTcpListenerOrFallBack を参照）。
    // ------------------------------------------------------------------

    [Fact]
    public void ShouldFallBackToIPv4Wildcard_DefaultWildcardAndIPv6Unavailable_ReturnsTrue()
    {
        // 既定値（非明示）の :: + IPv6 不可 → IPv4 縮小して起動を継続する（唯一の縮小ケース）。
        Assert.True(DualStackBindAddress.ShouldFallBackToIPv4Wildcard(
            IPAddress.IPv6Any, bindAddressIsExplicit: false, ipv6Available: false));
    }

    [Fact]
    public void ShouldFallBackToIPv4Wildcard_ExplicitWildcardAndIPv6Unavailable_ReturnsFalse()
    {
        // 明示指定の :: + IPv6 不可 → 縮小しない（fail-fast——利用者の明示意図を黙って裏切らない）。
        Assert.False(DualStackBindAddress.ShouldFallBackToIPv4Wildcard(
            IPAddress.IPv6Any, bindAddressIsExplicit: true, ipv6Available: false));
    }

    [Fact]
    public void ShouldFallBackToIPv4Wildcard_IPv6Available_ReturnsFalse()
    {
        // IPv6 が使える環境では明示・非明示を問わず縮小しない（DualMode で両受信する）。
        Assert.False(DualStackBindAddress.ShouldFallBackToIPv4Wildcard(
            IPAddress.IPv6Any, bindAddressIsExplicit: false, ipv6Available: true));
        Assert.False(DualStackBindAddress.ShouldFallBackToIPv4Wildcard(
            IPAddress.IPv6Any, bindAddressIsExplicit: true, ipv6Available: true));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void ShouldFallBackToIPv4Wildcard_NonWildcardAddress_ReturnsFalse(string address)
    {
        // IPv6 ワイルドカード以外は縮小の対象外（0.0.0.0 は元々 IPv4 のみ、特定アドレスは
        // そのアドレスファミリ単独で bind し、失敗はそのまま伝播する——従来挙動）。
        Assert.False(DualStackBindAddress.ShouldFallBackToIPv4Wildcard(
            IPAddress.Parse(address), bindAddressIsExplicit: false, ipv6Available: false));
    }

    [Fact]
    public void BuildExplicitIPv6WildcardUnavailableMessage_ContainsCauseAndAllRecoverySteps()
    {
        var message = DualStackBindAddress.BuildExplicitIPv6WildcardUnavailableMessage();

        // 原因（IPv6 スタック無効の可能性）と 3 つの復旧手段が利用者に提示されること。
        Assert.Contains("IPv6", message, StringComparison.Ordinal);
        Assert.Contains("DisabledComponents", message, StringComparison.Ordinal);
        Assert.Contains("0.0.0.0", message, StringComparison.Ordinal);
        Assert.Contains("BindAddress キーを削除", message, StringComparison.Ordinal);
    }
}
