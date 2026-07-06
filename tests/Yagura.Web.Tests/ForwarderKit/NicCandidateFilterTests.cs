using System.Net;
using System.Net.NetworkInformation;
using Yagura.Web.ForwarderKit;

namespace Yagura.Web.Tests.ForwarderKit;

/// <summary>
/// <see cref="NicCandidateFilter"/> の境界テスト（ADR-0008 委任 #6——除外条件（ループバック・
/// リンクローカル/APIPA・無効化 NIC）と複数 NIC 混在環境の判定を固定する）。
/// </summary>
public sealed class NicCandidateFilterTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void IsEligibleAddress_Loopback_Excluded(string address)
    {
        Assert.False(NicCandidateFilter.IsEligibleAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("169.254.0.1")]
    [InlineData("169.254.255.254")]
    public void IsEligibleAddress_Apipa_Excluded(string address)
    {
        Assert.False(NicCandidateFilter.IsEligibleAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public void IsEligibleAddress_IPv6LinkLocal_Excluded()
    {
        Assert.False(NicCandidateFilter.IsEligibleAddress(IPAddress.Parse("fe80::1")));
    }

    [Theory]
    [InlineData("192.168.1.10")]
    [InlineData("10.0.0.5")]
    [InlineData("203.0.113.9")]
    public void IsEligibleAddress_NormalIPv4_Included(string address)
    {
        Assert.True(NicCandidateFilter.IsEligibleAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public void IsEligibleAddress_GlobalIPv6_Included()
    {
        Assert.True(NicCandidateFilter.IsEligibleAddress(IPAddress.Parse("2001:db8::1")));
    }

    [Fact]
    public void IsOperational_Up_True()
    {
        Assert.True(NicCandidateFilter.IsOperational(OperationalStatus.Up));
    }

    [Theory]
    [InlineData(OperationalStatus.Down)]
    [InlineData(OperationalStatus.Unknown)]
    [InlineData(OperationalStatus.Testing)]
    [InlineData(OperationalStatus.Dormant)]
    [InlineData(OperationalStatus.NotPresent)]
    [InlineData(OperationalStatus.LowerLayerDown)]
    public void IsOperational_NotUp_False(OperationalStatus status)
    {
        Assert.False(NicCandidateFilter.IsOperational(status));
    }

    [Fact]
    public void IsLoopbackInterface_LoopbackType_True()
    {
        Assert.True(NicCandidateFilter.IsLoopbackInterface(NetworkInterfaceType.Loopback));
    }

    [Fact]
    public void IsLoopbackInterface_Ethernet_False()
    {
        Assert.False(NicCandidateFilter.IsLoopbackInterface(NetworkInterfaceType.Ethernet));
    }

    [Fact]
    public void IsCandidate_LoopbackInterfaceWithNormalAddress_Excluded()
    {
        // ループバック NIC はアドレス自体が到達可能な値でも除外する(NIC 種別が優先)。
        Assert.False(NicCandidateFilter.IsCandidate(
            NetworkInterfaceType.Loopback, OperationalStatus.Up, IPAddress.Parse("192.168.1.1")));
    }

    [Fact]
    public void IsCandidate_DownEthernetWithNormalAddress_Excluded()
    {
        Assert.False(NicCandidateFilter.IsCandidate(
            NetworkInterfaceType.Ethernet, OperationalStatus.Down, IPAddress.Parse("192.168.1.1")));
    }

    [Fact]
    public void IsCandidate_UpEthernetWithApipaAddress_Excluded()
    {
        Assert.False(NicCandidateFilter.IsCandidate(
            NetworkInterfaceType.Ethernet, OperationalStatus.Up, IPAddress.Parse("169.254.1.1")));
    }

    [Fact]
    public void IsCandidate_UpEthernetWithNormalAddress_Included()
    {
        Assert.True(NicCandidateFilter.IsCandidate(
            NetworkInterfaceType.Ethernet, OperationalStatus.Up, IPAddress.Parse("192.168.1.1")));
    }

    [Fact]
    public void IsCandidate_MultipleNicsMixed_OnlyEligibleOnesPass()
    {
        // 複数 NIC 混在環境の判定(ADR-0008 委任 #6)を、実 NIC 列挙に依存せず固定する:
        // 有効な物理 NIC・ループバック・無効化された NIC・APIPA のみの NIC が混在する状況を
        // 模した入力の組を判定させ、期待どおりに取捨選択されることを確認する。
        var inputs = new[]
        {
            (Type: NetworkInterfaceType.Ethernet, Status: OperationalStatus.Up, Address: IPAddress.Parse("10.0.0.5")),
            (Type: NetworkInterfaceType.Loopback, Status: OperationalStatus.Up, Address: IPAddress.Parse("127.0.0.1")),
            (Type: NetworkInterfaceType.Ethernet, Status: OperationalStatus.Down, Address: IPAddress.Parse("10.0.0.6")),
            (Type: NetworkInterfaceType.Ethernet, Status: OperationalStatus.Up, Address: IPAddress.Parse("169.254.10.10")),
            (Type: NetworkInterfaceType.Wireless80211, Status: OperationalStatus.Up, Address: IPAddress.Parse("192.168.50.20")),
        };

        var results = inputs.Select(i => NicCandidateFilter.IsCandidate(i.Type, i.Status, i.Address)).ToList();

        Assert.Equal([true, false, false, false, true], results);
    }
}
