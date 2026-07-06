using System.Net;
using System.Net.Sockets;

namespace Yagura.Web.ReverseDns;

/// <summary>
/// <see cref="IReverseDnsLookup"/> の本番実装（<see cref="Dns.GetHostEntryAsync(string, AddressFamily, CancellationToken)"/>）。
/// </summary>
/// <remarks>
/// <para>
/// ADR-0007 の検証記録（2026-07-06 実測）に基づく制約: ①本 API は逆引きに加えて前方解決も
/// 行う（PTR は登録済みだが前方登録が欠ける名前は取得できない場合がある）、
/// ②<see cref="CancellationToken"/> は進行中の OS 解決を中断しない（打ち切りは呼び出し側
/// <see cref="ReverseDnsResolver"/> が「待つのをやめる」方式で行う）、③失敗（未登録を含む）は
/// OS の名前解決フォールバック連鎖により数秒を要し得る。
/// </para>
/// </remarks>
public sealed class SystemDnsReverseLookup : IReverseDnsLookup
{
    public async Task<string?> QueryPtrAsync(IPAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        try
        {
            // IP 文字列を渡すと逆引きになる（IPAddress を取るオーバーロードは
            // CancellationToken を受けないため string 版を使う——ADR-0007 検証記録）。
            var entry = await Dns.GetHostEntryAsync(
                address.ToString(),
                AddressFamily.Unspecified,
                cancellationToken).ConfigureAwait(false);

            return string.IsNullOrEmpty(entry.HostName) ? null : entry.HostName;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.HostNotFound)
        {
            // PTR 未登録は正常系（ADR-0007 文脈 3）——例外ではなく「名前なし」として返す。
            return null;
        }
    }
}
