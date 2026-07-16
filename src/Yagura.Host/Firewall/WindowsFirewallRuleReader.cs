using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Yagura.Host.Firewall;

/// <summary>
/// Windows ファイアウォール（WFP）の規則を COM（<c>HNetCfg.FwPolicy2</c> = <c>INetFwPolicy2</c>）で
/// 読み取る実体（CF-2。Issue #265）。
/// </summary>
/// <remarks>
/// 読み取り専用——本クラスは規則を作成・変更しない（規則の作成はインストーラの責務。
/// configuration.md §4.3）。COM への参照は遅延バインディング（<c>dynamic</c>）とし、
/// 相互運用アセンブリ（NetFwTypeLib）への参照を持ち込まない。ファイアウォールサービス停止等で
/// COM が失敗した場合は <see langword="null"/> を返し、呼び出し側が「照合不能」として扱う。
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsFirewallRuleReader : IFirewallRuleReader
{
    private readonly ILogger<WindowsFirewallRuleReader> _logger;

    public WindowsFirewallRuleReader(ILogger<WindowsFirewallRuleReader> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<FirewallRuleInfo>? TryReadYaguraRules()
    {
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType is null)
            {
                return null;
            }

            dynamic policy = Activator.CreateInstance(policyType)!;
            var results = new List<FirewallRuleInfo>();

            foreach (dynamic rule in policy.Rules)
            {
                string? name = rule.Name as string;
                string? grouping = null;
                try
                {
                    grouping = rule.Grouping as string;
                }
                catch
                {
                    // Grouping が取得できない規則は名前判定のみで続行する。
                }

                var isYaguraNamespace =
                    (name is not null && name.StartsWith("Yagura", StringComparison.OrdinalIgnoreCase))
                    || string.Equals(grouping, "Yagura", StringComparison.OrdinalIgnoreCase);
                if (!isYaguraNamespace || name is null)
                {
                    continue;
                }

                // NET_FW_RULE_DIRECTION: 1 = In / NET_FW_ACTION: 1 = Allow。
                int direction = (int)rule.Direction;
                int action = (int)rule.Action;
                int protocol = (int)rule.Protocol;
                bool enabled = (bool)rule.Enabled;
                string? localPorts = null;
                try
                {
                    localPorts = rule.LocalPorts as string;
                }
                catch
                {
                    // ICMP 等ポート概念のないプロトコルでは LocalPorts の取得が失敗し得る。
                }

                results.Add(new FirewallRuleInfo(
                    name,
                    protocol,
                    localPorts,
                    enabled,
                    IsInboundAllow: direction == 1 && action == 1));
            }

            return results;
        }
        catch (Exception ex)
        {
            // ファイアウォールサービス停止・COM 障害等。照合不能を不一致と偽らない——
            // 呼び出し側が情報ログに留める（FirewallConsistencyChecker 参照）。
            _logger.LogInformation(
                ex,
                "ファイアウォール規則の読み取りに失敗したため、規則の突合（CF-2）をスキップします。");
            return null;
        }
    }
}
