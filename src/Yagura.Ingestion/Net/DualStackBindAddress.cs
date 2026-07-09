using System.Net;
using System.Net.Sockets;

namespace Yagura.Ingestion.Net;

/// <summary>
/// UDP/TCP 受信リスナの bind 先解釈と、送信元アドレス表現の正規化を集約する（Issue #133）。
/// </summary>
/// <remarks>
/// <para>
/// <b>bind 先の解釈</b>: <c>BindAddress</c> が IPv6 ワイルドカード（<c>::</c> =
/// <see cref="IPAddress.IPv6Any"/>）のときのみ、<c>Socket.DualMode</c> を有効にした単一ソケットで
/// IPv4/IPv6 の両方を受信する（Windows は DualMode を標準サポート——Program.cs の Kestrel 側と
/// 同じ仕組み。<see cref="Yagura.Host.ListenerBindPlan"/> の remarks 参照）。それ以外の明示指定
/// （<c>0.0.0.0</c> = IPv4 ワイルドカードのみ、特定の IPv4/IPv6 アドレス）は、指定されたアドレス
/// ファミリ単独のソケットで bind する——<c>0.0.0.0</c> 明示指定は IPv4 専用にとどまる後方互換の
/// 逃げ道として維持する（Issue #133 の設計判断）。
/// </para>
/// <para>
/// <b>送信元アドレスの表現</b>: DualMode ソケットで IPv4 の送信元から受信すると、
/// <c>RemoteEndPoint.Address</c> は IPv4-mapped IPv6（<c>::ffff:x.x.x.x</c>）として現れる。
/// これをそのまま <see cref="Yagura.Ingestion.Udp.RawDatagram.SourceAddress"/> へ書き込むと、
/// 同一の IPv4 送信元が「0.0.0.0 既定時代のログ」と「DualMode 化後のログ」で異なる文字列表現に
/// 分裂し、検索・送信元別集計（<see cref="Yagura.Storage.SourceActivity"/>）・逆引き
/// （<see cref="Yagura.Web.ReverseDns.ReverseDnsResolver"/>）の一致判定が壊れる。
/// <see cref="Yagura.Web.ReverseDns.ReverseDnsResolver"/> が既に採用している
/// 「IPv4-mapped IPv6 は <see cref="IPAddress.MapToIPv4"/> で正規化してから扱う」
/// （ADR-0007 決定 2 の境界ケース）と同じ規約を受信段にも適用し、IPv4 送信元は常に
/// ドット区切り表記で記録する。
/// </para>
/// </remarks>
internal static class DualStackBindAddress
{
    /// <summary>
    /// <paramref name="bindAddress"/> が IPv6 ワイルドカード（<c>::</c>）か——真なら
    /// DualMode ソケットで bind すべきであることを表す。
    /// </summary>
    public static bool IsIPv6Wildcard(IPAddress bindAddress)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);
        return bindAddress.AddressFamily == AddressFamily.InterNetworkV6 && bindAddress.Equals(IPAddress.IPv6Any);
    }

    /// <summary>
    /// 送信元アドレスを正規化する: IPv4-mapped IPv6（<c>::ffff:x.x.x.x</c>）は
    /// <see cref="IPAddress.MapToIPv4"/> で純粋な IPv4 表現へ変換する。それ以外はそのまま返す。
    /// </summary>
    public static IPAddress NormalizeSourceAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }
}
