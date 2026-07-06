using System.Net;

namespace Yagura.Web.ReverseDns;

/// <summary>
/// 逆引き（PTR）の下位解決 API の抽象（ADR-0007 決定 4 のテスト固定点）。
/// </summary>
/// <remarks>
/// 「オフ時・対象帯域外の IP にクエリを発しない」ことの単体テストによる固定
/// （security.md §1.1）は、解決 API の呼び出しを本インターフェースの実装 1 点に集約し、
/// テストが偽実装で「呼ばれないこと」を検証する構造で実現する。
/// <see cref="ReverseDnsResolver"/> 以外から本インターフェースを呼び出さないこと。
/// </remarks>
public interface IReverseDnsLookup
{
    /// <summary>
    /// 指定 IP アドレスの逆引きホスト名を取得する。
    /// </summary>
    /// <returns>ホスト名。PTR 未登録（該当なし）の場合は <c>null</c>。</returns>
    /// <exception cref="System.Net.Sockets.SocketException">未登録以外の解決失敗（DNS 障害等）。</exception>
    Task<string?> QueryPtrAsync(IPAddress address, CancellationToken cancellationToken);
}
