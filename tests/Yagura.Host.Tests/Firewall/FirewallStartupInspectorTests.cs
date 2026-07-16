using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Configuration;
using Yagura.Host.Firewall;

namespace Yagura.Host.Tests.Firewall;

/// <summary>
/// <see cref="FirewallStartupInspector"/> の単体テスト（CF-2。configuration.md §4.3。Issue #265）。
/// 規則の突合（欠落・取り残し・オプトアウト・照合不能）と、インストール記録の一回性転記を固定する。
/// </summary>
public sealed class FirewallStartupInspectorTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-fw-test-{Guid.NewGuid():N}");
    private readonly RecordingAuditRecorder _auditRecorder = new();
    private readonly FakeLogger<FirewallStartupInspector> _logger = new();

    public FirewallStartupInspectorTests()
    {
        Directory.CreateDirectory(_dataRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    private sealed class RecordingAuditRecorder : IAuditRecorder
    {
        public List<AuditEvent> Recorded { get; } = [];

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Recorded.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRuleReader(IReadOnlyList<FirewallRuleInfo>? rules) : IFirewallRuleReader
    {
        public IReadOnlyList<FirewallRuleInfo>? TryReadYaguraRules() => rules;
    }

    private FirewallStartupInspector CreateInspector(IReadOnlyList<FirewallRuleInfo>? rules) =>
        new(_dataRoot, new FakeRuleReader(rules), _auditRecorder, _logger);

    /// <summary>インストーラの既定 3 規則（Firewall.wxs）に相当する規則集合。</summary>
    private static List<FirewallRuleInfo> InstallerDefaultRules() =>
    [
        new("Yagura Syslog (UDP 514)", FirewallRuleInfo.UdpProtocol, "514", Enabled: true, IsInboundAllow: true),
        new("Yagura Syslog (TCP 514)", FirewallRuleInfo.TcpProtocol, "514", Enabled: true, IsInboundAllow: true),
        new("Yagura Viewer (TCP 8514)", FirewallRuleInfo.TcpProtocol, "8514", Enabled: true, IsInboundAllow: true),
    ];

    private static ResolvedYaguraConfiguration CreateConfiguration(
        int udpPort = 514,
        int tcpPort = 514,
        int httpPort = 8514,
        ViewerPublicAccess viewerPublicAccess = ViewerPublicAccess.Lan) =>
        new(
            DataRoot: Path.GetTempPath(),
            UdpBindAddress: "::",
            UdpPort: udpPort,
            UdpReceiveBufferBytes: Yagura.Ingestion.Udp.UdpSyslogListenerOptions.DefaultReceiveBufferBytes,
            TcpBindAddress: "::",
            TcpPort: tcpPort,
            DefaultRfc3164TimeZone: TimeZoneInfo.Utc,
            HttpPort: httpPort,
            ViewerPublicAccess: viewerPublicAccess,
            ViewerReverseDnsEnabled: true,
            ViewerWindowsAuthEnabled: false,
            ViewerWindowsAuthKerberosOnly: false,
            ViewerWindowsViewerGroups: [],
            ViewerWindowsAdminGroups: [],
            AdminHttpPort: 8515,
            AdminWindowsAuthEnabled: false,
            AdminWindowsAuthKerberosOnly: false,
            AdminWindowsAdminGroups: [],
            AdminAppAuthEnabled: false,
            AdminAuthRequireForLoopback: false,
            AdminRemoteBindingEnabled: false,
            AdminHttpsEnabled: false,
            AdminHttpsCertificateThumbprint: null,
            AdminHttpsPort: 8516,
            SqliteFileName: "yagura.db",
            SpoolEnabled: true,
            SpoolDirectory: Path.Combine(Path.GetTempPath(), "spool"),
            SpoolQuotaBytes: 1024,
            RetentionDays: 30,
            RetentionExecutionTimeOfDay: new TimeOnly(3, 0),
            StorageProvider: StorageProvider.Sqlite,
            SqlServerConnectionString: null,
            IngestionTlsEnabled: false,
            IngestionTlsBindAddress: "::",
            IngestionTlsPort: 6514,
            IngestionTlsCertificateThumbprint: null,
            FlowControlEnabled: true,
            FlowControlMessagesPerSecond: 10000,
            FlowControlBurstSize: 20000,
            AuditRetentionDays: 365);

    [Fact]
    public void CheckConsistency_DefaultRulesAndDefaultPorts_NoWarning()
    {
        CreateInspector(InstallerDefaultRules()).CheckConsistency(CreateConfiguration());

        Assert.DoesNotContain(_logger.Collector.GetSnapshot(), r => r.Level == LogLevel.Warning);
    }

    [Fact]
    public void CheckConsistency_PortChangedButRulesStale_WarnsWithBothFindings()
    {
        // UDP を 5514 へ変更したが規則は 514 のまま——「欠落」と「取り残し」の両方が 1023 で列挙される。
        CreateInspector(InstallerDefaultRules()).CheckConsistency(CreateConfiguration(udpPort: 5514));

        var warning = Assert.Single(_logger.Collector.GetSnapshot(), r => r.Level == LogLevel.Warning);
        Assert.Equal(ConfigurationEventIds.FirewallRuleMismatch.Id, warning.Id.Id);
        Assert.Contains("UDP 5514", warning.Message);
        Assert.Contains("Yagura Syslog (UDP 514)", warning.Message);
    }

    [Fact]
    public void CheckConsistency_NoRulesWithoutOptOutRecord_Warns()
    {
        // 規則ゼロ + オプトアウト記録なし（手動配置等）——「規則が無い」構成を警告する（Issue #265 の完了条件）。
        CreateInspector([]).CheckConsistency(CreateConfiguration());

        var warning = Assert.Single(_logger.Collector.GetSnapshot(), r => r.Level == LogLevel.Warning);
        Assert.Contains("syslog UDP 受信", warning.Message);
    }

    [Fact]
    public void CheckConsistency_NoRulesWithOptOutRecord_InfoOnly()
    {
        // インストール時にオプトアウトした環境（集中管理）には警告を浴びせない。
        File.WriteAllLines(
            Path.Combine(_dataRoot, FirewallStartupInspector.InstallationRecordFileName),
            ["[Yagura.Firewall]", "RulesRequested="]);

        CreateInspector([]).CheckConsistency(CreateConfiguration());

        Assert.DoesNotContain(_logger.Collector.GetSnapshot(), r => r.Level == LogLevel.Warning);
        Assert.Contains(_logger.Collector.GetSnapshot(), r => r.Message.Contains("オプトアウト"));
    }

    [Fact]
    public void CheckConsistency_ViewerLocalhostOnly_ViewerRuleNotRequired()
    {
        // LocalhostOnly では閲覧規則は不要（欠けていても警告しない）。UDP/TCP 分の規則のみで整合。
        var rules = new List<FirewallRuleInfo>
        {
            new("Yagura Syslog (UDP 514)", FirewallRuleInfo.UdpProtocol, "514", true, true),
            new("Yagura Syslog (TCP 514)", FirewallRuleInfo.TcpProtocol, "514", true, true),
        };

        CreateInspector(rules).CheckConsistency(
            CreateConfiguration(viewerPublicAccess: ViewerPublicAccess.LocalhostOnly));

        Assert.DoesNotContain(_logger.Collector.GetSnapshot(), r => r.Level == LogLevel.Warning);
    }

    [Fact]
    public void CheckConsistency_DisabledRule_IsTreatedAsMissing()
    {
        var rules = InstallerDefaultRules();
        rules[0] = rules[0] with { Enabled = false };

        CreateInspector(rules).CheckConsistency(CreateConfiguration());

        var warning = Assert.Single(_logger.Collector.GetSnapshot(), r => r.Level == LogLevel.Warning);
        Assert.Contains("UDP 514", warning.Message);
    }

    [Fact]
    public void CheckConsistency_ReaderUnavailable_InfoOnlyWithoutWarning()
    {
        // 照合不能（ファイアウォールサービス停止等）を不一致と偽らない。
        CreateInspector(rules: null).CheckConsistency(CreateConfiguration());

        Assert.DoesNotContain(_logger.Collector.GetSnapshot(), r => r.Level == LogLevel.Warning);
        Assert.Contains(_logger.Collector.GetSnapshot(), r => r.Message.Contains("スキップ"));
    }

    [Fact]
    public async Task Transcribe_RecordsAuditEventOnce_AndWritesMarker()
    {
        File.WriteAllLines(
            Path.Combine(_dataRoot, FirewallStartupInspector.InstallationRecordFileName),
            [
                "[Yagura.Firewall]",
                "RulesRequested=1",
                "Rule1=Yagura Syslog (UDP 514) / udp 514 / inbound allow / profile: domain,private",
            ]);

        var inspector = CreateInspector([]);
        await inspector.TranscribeInstallationRecordOnceAsync();

        var recorded = Assert.Single(_auditRecorder.Recorded);
        Assert.Equal(AuditEventKind.InstallationRecordTranscribed, recorded.Kind);
        Assert.Contains("RulesRequested=1", recorded.Detail);
        Assert.Contains("Yagura Syslog (UDP 514)", recorded.Detail);
        Assert.True(File.Exists(Path.Combine(_dataRoot, FirewallStartupInspector.TranscribedMarkerFileName)));

        // 2 回目はマーカーにより転記しない（初回起動時に 1 回だけ——configuration.md §4.3）。
        await inspector.TranscribeInstallationRecordOnceAsync();
        Assert.Single(_auditRecorder.Recorded);
    }

    [Fact]
    public async Task Transcribe_NoIniFile_DoesNothing()
    {
        await CreateInspector([]).TranscribeInstallationRecordOnceAsync();

        Assert.Empty(_auditRecorder.Recorded);
        Assert.False(File.Exists(Path.Combine(_dataRoot, FirewallStartupInspector.TranscribedMarkerFileName)));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void WindowsFirewallRuleReader_RealCom_DoesNotThrowAndFiltersToYaguraNamespace()
    {
        // 実 COM（HNetCfg.FwPolicy2）のスモーク: 例外を投げず、返る規則はすべて Yagura 名前空間で
        // あること（CI の windows ランナー・開発機とも Yagura 規則は通常 0 件 = 空リストが返る。
        // ファイアウォールサービスが無効な環境では null = 照合不能で、これも契約どおり）。
        var reader = new WindowsFirewallRuleReader(new FakeLogger<WindowsFirewallRuleReader>());

        var rules = reader.TryReadYaguraRules();

        if (rules is not null)
        {
            // 名前は必ず入る（グループ「Yagura」経由で拾われた規則は名前が Yagura 始まりとは
            // 限らないため、ここでは非空のみを固定する）。
            Assert.All(rules, r => Assert.False(string.IsNullOrEmpty(r.Name)));
        }
    }

    [Theory]
    [InlineData("514", 514, true)]
    [InlineData("514,515", 515, true)]
    [InlineData("500-600", 514, true)]
    [InlineData("*", 9999, true)]
    [InlineData("514", 5514, false)]
    [InlineData("", 514, false)]
    public void FirewallRuleInfo_CoversPort_ParsesPortSpecifications(string localPorts, int port, bool expected)
    {
        var rule = new FirewallRuleInfo("Yagura Test", FirewallRuleInfo.TcpProtocol, localPorts, true, true);

        Assert.Equal(expected, rule.CoversPort(port));
    }
}
