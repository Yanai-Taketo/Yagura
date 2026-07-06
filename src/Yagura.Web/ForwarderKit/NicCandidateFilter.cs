using System.Net;
using System.Net.NetworkInformation;

namespace Yagura.Web.ForwarderKit;

/// <summary>
/// NIC 候補の除外判定（ADR-0008 設計条件 1・委任 #6）。列挙処理（<see cref="SystemNicCandidateSource"/>）
/// から分離した純粋関数として実装し、単体テストで境界（ループバック・APIPA・IPv6 リンクローカル・
/// 停止 NIC・複数 NIC 混在）を固定できるようにする。
/// </summary>
public static class NicCandidateFilter
{
    /// <summary>IPv4 APIPA (RFC 3927) の帯域: 169.254.0.0/16。</summary>
    private static readonly byte[] ApipaPrefix = [169, 254];

    /// <summary>
    /// 指定した NIC の稼働状態が候補の対象か（<see cref="OperationalStatus.Up"/> のみを候補とする）。
    /// </summary>
    public static bool IsOperational(OperationalStatus status) => status == OperationalStatus.Up;

    /// <summary>
    /// 指定した NIC の種別が候補から除外すべきループバックか。
    /// </summary>
    public static bool IsLoopbackInterface(NetworkInterfaceType interfaceType) =>
        interfaceType == NetworkInterfaceType.Loopback;

    /// <summary>
    /// 指定したアドレスが宛先候補として適格か（ADR-0008 設計条件 1 の除外条件の実体）。
    /// 除外: ループバック（<see cref="IPAddress.IsLoopback"/>）・IPv4 APIPA（169.254.0.0/16）・
    /// IPv6 リンクローカル（<see cref="IPAddress.IsIPv6LinkLocal"/>）。
    /// </summary>
    public static bool IsEligibleAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && IsApipa(address))
        {
            return false;
        }

        if (address.IsIPv6LinkLocal)
        {
            return false;
        }

        return true;
    }

    /// <summary>指定した IPv4 アドレスが APIPA(169.254.0.0/16) 帯域か。</summary>
    private static bool IsApipa(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == ApipaPrefix[0] && bytes[1] == ApipaPrefix[1];
    }

    /// <summary>
    /// 1 個の NIC・1 個の単項アドレス情報から、候補として採用すべきかを判定する
    /// （<see cref="IsOperational"/> ・ <see cref="IsLoopbackInterface"/> ・
    /// <see cref="IsEligibleAddress"/> の複合判定。列挙側から呼ばれる唯一の入口）。
    /// </summary>
    public static bool IsCandidate(NetworkInterfaceType interfaceType, OperationalStatus status, IPAddress address)
    {
        if (IsLoopbackInterface(interfaceType))
        {
            return false;
        }

        if (!IsOperational(status))
        {
            return false;
        }

        return IsEligibleAddress(address);
    }
}
