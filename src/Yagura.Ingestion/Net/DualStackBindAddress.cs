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

    /// <summary>
    /// IPv6 ワイルドカード bind が成立しないとき、IPv4 ワイルドカード（<c>0.0.0.0</c>）へ
    /// 自動で縮小してよいかを判定する（Issue #133・PR #193 レビュー指摘 Major への対応）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>既定値（キー未設定 = 非明示）のときのみ縮小する</b>: IPv6 スタックが無効な環境
    /// （レジストリ <c>DisabledComponents</c>（KB929852）による無効化・NIC からの IPv6
    /// コンポーネント除外等）では、既定の <c>::</c> による DualMode ソケット作成が
    /// <see cref="SocketException"/>（<see cref="SocketError.AddressFamilyNotSupported"/>）になる。
    /// 既定構成のまま Yagura 全体（閲覧・管理 UI を含む Generic Host）が起動不能になることを
    /// 避けるため、この場合は IPv4 のみへ縮小して起動を継続し、警告ログ（イベントログに届く
    /// レベル）で明示する。
    /// </para>
    /// <para>
    /// <b>明示指定（利用者が設定ファイルに <c>::</c> を書いた）のときは縮小しない</b>:
    /// 利用者の明示意図（IPv6 でも受信する）を黙って裏切らず、原因と復旧手順を含む
    /// エラーメッセージ（<see cref="BuildExplicitIPv6WildcardUnavailableMessage"/>）で
    /// 起動を失敗させる（fail-fast）。
    /// </para>
    /// <para>
    /// <b>縮小の条件は「IPv6 が使えない」に限る</b>: ポート競合（AddressInUse）等の
    /// 別要因の bind 失敗でこの縮小を発動すると、ポート事故が黙って「IPv4 のみ受信」へ
    /// 化けてしまう。呼び出し側は <see cref="SocketError.AddressFamilyNotSupported"/> の
    /// 場合のみを縮小対象とし、それ以外の <see cref="SocketException"/> はそのまま送出する。
    /// </para>
    /// </remarks>
    /// <param name="bindAddress">解釈済みの bind アドレス。</param>
    /// <param name="bindAddressIsExplicit">bind アドレスが設定で明示指定されたか。</param>
    /// <param name="ipv6Available">この環境で IPv6 ソケットを作成できるか（<c>Socket.OSSupportsIPv6</c> の事前チェック、または bind 実試行の結果）。</param>
    public static bool ShouldFallBackToIPv4Wildcard(IPAddress bindAddress, bool bindAddressIsExplicit, bool ipv6Available)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);
        return IsIPv6Wildcard(bindAddress) && !bindAddressIsExplicit && !ipv6Available;
    }

    /// <summary>
    /// 明示指定された <c>::</c> が IPv6 不可の環境で bind できなかったときの、起動失敗
    /// エラーメッセージ（原因と復旧手順を含む）。<c>IngestionPipeline.StartListenerAsync</c> の
    /// Error ログと Generic Host の起動失敗を通じて、イベントログ・コンソールの両方へ届く。
    /// </summary>
    public static string BuildExplicitIPv6WildcardUnavailableMessage() =>
        "BindAddress '::'（IPv4/IPv6 両受信）が明示指定されていますが、この環境では IPv6 ソケットを" +
        "作成できません（OS の IPv6 スタックが無効化されている可能性があります——レジストリ" +
        " DisabledComponents による無効化・NIC の IPv6 コンポーネント無効化等）。次のいずれかで" +
        "復旧してください: (1) OS の IPv6 を有効化する、(2) 設定ファイルの BindAddress キーを" +
        "削除する（既定動作 = IPv6 不可の環境では IPv4 のみへ自動縮小して起動を継続）、" +
        "(3) BindAddress を '0.0.0.0'（IPv4 のみ）または有効な IPv4 アドレスへ変更する。";
}
