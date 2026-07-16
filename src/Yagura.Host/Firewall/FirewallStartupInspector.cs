using Microsoft.Extensions.Logging;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Configuration;
using Yagura.Host.Observability.Auditing;

namespace Yagura.Host.Firewall;

/// <summary>
/// CF-2（configuration.md §4.3。Issue #265）の Host 側実装:
/// ①リスナの実ポートと Yagura 名前空間のファイアウォール規則の突合・警告（起動時 +
/// リスナ再構成の適用時）②インストール記録（<c>firewall-rules.ini</c>）の初回起動時の
/// イベントログ転記（監査 2017）。
/// </summary>
/// <remarks>
/// <para>
/// <b>突合の設計</b>: 期待集合 = 実際に外部公開される listen（UDP 受信・TCP 受信・
/// 閲覧 HTTP〔`Viewer:PublicAccess = Lan` のときのみ——LocalhostOnly は規則不要〕・
/// TLS 受信〔有効時〕）。管理リスナは loopback 束縛のため対象外（規則を作らない設計——
/// configuration.md §4.3）。各期待 listen をカバーする**有効な受信許可規則**が Yagura
/// 名前空間に存在しなければ警告（イベント ID 1023）。逆に、どの listen にも対応しない
/// Yagura 規則（ポート変更後の取り残し）も同じ警告にまとめて列挙する。
/// </para>
/// <para>
/// <b>オプトアウトへの配慮</b>: インストール記録が「規則作成をオプトアウトした」ことを示し、
/// かつ Yagura 名前空間の規則が 1 つも無い場合は、警告ではなく情報ログに留める——GPO 等で
/// 集中管理する環境が意図してオプトアウトした構成に毎回警告を浴びせない（その環境の規則は
/// Yagura 名前空間の外にあり、本照合の可視範囲外である事実も情報ログに含める）。
/// Yagura 名前空間に規則が 1 つでもあれば通常の突合を行う。
/// </para>
/// <para>
/// <b>能動通知（architecture.md §4.6）へは接続しない（本 Issue の判断）</b>: 不一致は構成の
/// 静的な性質であり、起動時と再構成適用時の検出で「変化した瞬間」を必ず捕捉できる——周期監視を
/// 足しても同じ警告の反復にしかならない。イベントログの警告（1023）は既定の監視構成
/// 「ソース Yagura の警告以上を通知」（security.md §4.3）に乗るため、通知の実効性は既に確保される。
/// </para>
/// <para>
/// <b>転記の一回性</b>: データルート直下のマーカーファイル（<see cref="TranscribedMarkerFileName"/>）で
/// 判定する。メタデータ領域（observability-state.json）に載せないのは、あちらが観測値
/// （カウンタ・停止イベント）の周期書き込みドメインであり、単発の転記フラグを混ぜると
/// 書き込み主体が競合するため（ObservabilityCoordinator が全体書き換えで所有）。
/// </para>
/// </remarks>
public sealed class FirewallStartupInspector
{
    /// <summary>インストーラが書くインストール記録のファイル名（installer/Firewall.wxs）。</summary>
    public const string InstallationRecordFileName = "firewall-rules.ini";

    /// <summary>転記済みマーカーのファイル名（remarks「転記の一回性」参照）。</summary>
    public const string TranscribedMarkerFileName = "firewall-rules.ini.transcribed";

    private readonly string _dataRoot;
    private readonly IFirewallRuleReader _ruleReader;
    private readonly IAuditRecorder _auditRecorder;
    private readonly ILogger<FirewallStartupInspector> _logger;

    public FirewallStartupInspector(
        string dataRoot,
        IFirewallRuleReader ruleReader,
        IAuditRecorder auditRecorder,
        ILogger<FirewallStartupInspector> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(ruleReader);
        ArgumentNullException.ThrowIfNull(auditRecorder);
        ArgumentNullException.ThrowIfNull(logger);

        _dataRoot = dataRoot;
        _ruleReader = ruleReader;
        _auditRecorder = auditRecorder;
        _logger = logger;
    }

    /// <summary>
    /// インストール記録（firewall-rules.ini）を初回起動時に 1 回だけイベントログへ転記する
    /// （configuration.md §4.3「なぜこのサーバには規則がないのか」に証跡で答える。監査 2017——
    /// イベントログ併記は FileAuditRecorder の多段が担う）。ini が無い（手動配置・MSI 以外の
    /// 導入）場合は何もしない。失敗は起動を妨げない。
    /// </summary>
    public async Task TranscribeInstallationRecordOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var iniPath = Path.Combine(_dataRoot, InstallationRecordFileName);
            var markerPath = Path.Combine(_dataRoot, TranscribedMarkerFileName);

            if (!File.Exists(iniPath) || File.Exists(markerPath))
            {
                return;
            }

            var lines = await File.ReadAllLinesAsync(iniPath, cancellationToken).ConfigureAwait(false);
            var contentSummary = string.Join(" | ", lines
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('[') && !l.StartsWith(';')));

            await _auditRecorder.RecordAsync(
                new AuditEvent(
                    OccurredAt: DateTimeOffset.UtcNow,
                    Kind: AuditEventKind.InstallationRecordTranscribed,
                    RemoteAddress: null,
                    RemotePort: null,
                    Detail: $"インストール記録（{InstallationRecordFileName}）の転記: {contentSummary}"),
                CancellationToken.None).ConfigureAwait(false);

            await File.WriteAllTextAsync(
                markerPath,
                $"transcribed at {DateTimeOffset.UtcNow:O}\n",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "インストール記録（firewall-rules.ini）の転記に失敗しました（次回起動時に再試行されます）。");
        }
    }

    /// <summary>
    /// 期待する外部公開 listen とファイアウォール規則を突合し、不一致を警告する
    /// （起動時とリスナ再構成の適用時に呼ばれる。configuration.md §4.3「起動時とポート変更の
    /// 適用時に検出して警告する」）。
    /// </summary>
    public void CheckConsistency(ResolvedYaguraConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var rules = _ruleReader.TryReadYaguraRules();
        if (rules is null)
        {
            // 照合不能（ファイアウォールサービス停止等）。不一致と偽らず情報ログに留める。
            _logger.LogInformation("ファイアウォール規則を読み取れないため、規則の突合（CF-2）をスキップしました。");
            return;
        }

        var expected = BuildExpectedListens(configuration);
        var optedOut = InstallationRecordShowsOptOut();

        if (rules.Count == 0 && optedOut)
        {
            _logger.LogInformation(
                "ファイアウォール規則の作成はインストール時にオプトアウトされています（インストール記録より）。" +
                "Yagura 名前空間の規則が存在しないため突合は行いません——集中管理された規則は本照合の対象外です。");
            return;
        }

        var findings = new List<string>();

        foreach (var (protocol, port, label) in expected)
        {
            var covered = rules.Any(r =>
                r.Enabled && r.IsInboundAllow && r.Protocol == protocol && r.CoversPort(port));
            if (!covered)
            {
                var protocolName = protocol == FirewallRuleInfo.UdpProtocol ? "UDP" : "TCP";
                findings.Add($"{label}（{protocolName} {port}）に対応する有効な受信許可規則がありません");
            }
        }

        foreach (var rule in rules.Where(r => r.Enabled && r.IsInboundAllow))
        {
            var matchesAny = expected.Any(e => rule.Protocol == e.Protocol && rule.CoversPort(e.Port));
            if (!matchesAny && rule.Protocol is FirewallRuleInfo.TcpProtocol or FirewallRuleInfo.UdpProtocol)
            {
                findings.Add($"規則「{rule.Name}」（ポート {rule.LocalPorts}）はどのリスナにも対応していません（ポート変更後の取り残しの可能性）");
            }
        }

        if (findings.Count > 0)
        {
            // ファイアウォールでの drop は観測の完全な死角（configuration.md §4.3）——
            // 「沈黙して受信できないだけ」を作らないための警告（イベント ID 1023）。
            _logger.LogWarning(
                ConfigurationEventIds.FirewallRuleMismatch,
                "[firewall-rule-mismatch] リスナの実ポートと Yagura 名前空間のファイアウォール規則に不一致があります: {Findings}。" +
                "この状態では該当ポートへの受信がファイアウォールで破棄され、Yagura のカウンタにも現れません" +
                "（configuration.md §4.3。規則の作成・修正手順は利用者ガイド参照）。",
                string.Join(" / ", findings));
        }
    }

    /// <summary>
    /// 期待する外部公開 listen の集合を構成から導出する（remarks「突合の設計」参照）。
    /// </summary>
    internal static IReadOnlyList<(int Protocol, int Port, string Label)> BuildExpectedListens(
        ResolvedYaguraConfiguration configuration)
    {
        var expected = new List<(int, int, string)>
        {
            (FirewallRuleInfo.UdpProtocol, configuration.UdpPort, "syslog UDP 受信"),
            (FirewallRuleInfo.TcpProtocol, configuration.TcpPort, "syslog TCP 受信"),
        };

        if (configuration.ViewerPublicAccess == ViewerPublicAccess.Lan)
        {
            expected.Add((FirewallRuleInfo.TcpProtocol, configuration.HttpPort, "閲覧 UI"));
        }

        if (configuration.IngestionTlsEnabled && configuration.IngestionTlsCertificateThumbprint is not null)
        {
            expected.Add((FirewallRuleInfo.TcpProtocol, configuration.IngestionTlsPort, "syslog TLS 受信"));
        }

        return expected;
    }

    private bool InstallationRecordShowsOptOut()
    {
        try
        {
            var iniPath = Path.Combine(_dataRoot, InstallationRecordFileName);
            if (!File.Exists(iniPath))
            {
                return false;
            }

            // Firewall.wxs: RulesRequested=1 が作成、空・0 がオプトアウト。
            foreach (var line in File.ReadAllLines(iniPath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("RulesRequested=", StringComparison.OrdinalIgnoreCase))
                {
                    var value = trimmed["RulesRequested=".Length..].Trim();
                    return value is not "1";
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
