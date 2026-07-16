namespace Yagura.Host.Firewall;

/// <summary>
/// Windows ファイアウォールの受信規則 1 件分の、突合に必要な属性（CF-2。Issue #265）。
/// </summary>
/// <param name="Name">規則名（Yagura 名前空間 = 「Yagura」始まり）。</param>
/// <param name="Protocol">IP プロトコル番号（6 = TCP / 17 = UDP。それ以外は突合対象外）。</param>
/// <param name="LocalPorts">ローカルポート指定の生文字列（例 <c>"514"</c>・<c>"514,515"</c>・<c>"*"</c>）。</param>
/// <param name="Enabled">規則が有効か。</param>
/// <param name="IsInboundAllow">受信（inbound）の許可（allow）規則か。</param>
public sealed record FirewallRuleInfo(
    string Name,
    int Protocol,
    string? LocalPorts,
    bool Enabled,
    bool IsInboundAllow)
{
    /// <summary>TCP のプロトコル番号。</summary>
    public const int TcpProtocol = 6;

    /// <summary>UDP のプロトコル番号。</summary>
    public const int UdpProtocol = 17;

    /// <summary>
    /// この規則が指定ポートをカバーするか（<c>*</c>・カンマ区切り・範囲 <c>a-b</c> を解釈する。
    /// 解釈できないトークンは不一致として無視する——保証力を偽らない）。
    /// </summary>
    public bool CoversPort(int port)
    {
        if (string.IsNullOrWhiteSpace(LocalPorts))
        {
            return false;
        }

        foreach (var token in LocalPorts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "*")
            {
                return true;
            }

            var rangeSeparator = token.IndexOf('-');
            if (rangeSeparator > 0
                && int.TryParse(token[..rangeSeparator], out var low)
                && int.TryParse(token[(rangeSeparator + 1)..], out var high))
            {
                if (port >= low && port <= high)
                {
                    return true;
                }

                continue;
            }

            if (int.TryParse(token, out var single) && single == port)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Yagura 名前空間のファイアウォール受信規則の読み取り口（テスト用の差し替え点。
/// 実体は <see cref="WindowsFirewallRuleReader"/>）。
/// </summary>
public interface IFirewallRuleReader
{
    /// <summary>
    /// Yagura 名前空間（規則名が「Yagura」始まり、またはグループ「Yagura」）の受信規則を列挙する。
    /// ファイアウォール API へ到達できない場合は <see langword="null"/> を返す（呼び出し側は
    /// 照合不能として情報ログに留める——照合できないことを不一致と偽らない）。
    /// </summary>
    IReadOnlyList<FirewallRuleInfo>? TryReadYaguraRules();
}
