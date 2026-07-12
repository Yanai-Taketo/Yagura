using System.Net;
using Yagura.Host.Configuration;
using Yagura.Web;

namespace Yagura.Host.Tests;

/// <summary>
/// <see cref="ListenerBindPlan"/> の単体テスト（M6-1。Issue #51）。
/// </summary>
/// <remarks>
/// 完了条件「管理リスナの bind URL 構築が常に loopback」を実サーバ起動なしに検証する
/// （configuration.md §1 の不変条件・security.md §1 L-4）。
/// </remarks>
public sealed class ListenerBindPlanTests
{
    [Theory]
    [InlineData(ViewerPublicAccess.Lan)]
    [InlineData(ViewerPublicAccess.LocalhostOnly)]
    public void Create_AdminEntries_AreAlwaysLoopbackRegardlessOfViewerPublicAccess(ViewerPublicAccess viewerPublicAccess)
    {
        var configuration = CreateConfiguration(viewerPublicAccess, adminHttpPort: 8515);

        var entries = ListenerBindPlan.Create(configuration);

        var adminEntries = entries.Where(e => e.Kind == ListenerKind.Admin).ToList();

        Assert.Equal(2, adminEntries.Count);
        Assert.All(adminEntries, e => Assert.False(e.IsAnyIP, "管理リスナの bind 先はワイルドカードであってはならない。"));
        Assert.Contains(adminEntries, e => Equals(e.Address, IPAddress.Loopback));
        Assert.Contains(adminEntries, e => Equals(e.Address, IPAddress.IPv6Loopback));
        Assert.All(adminEntries, e => Assert.Equal(8515, e.Port));
    }

    [Fact]
    public void Create_ViewerPublicAccessLan_ViewerEntryIsAnyIP()
    {
        var configuration = CreateConfiguration(ViewerPublicAccess.Lan, adminHttpPort: 8515);

        var entries = ListenerBindPlan.Create(configuration);

        var viewerEntries = entries.Where(e => e.Kind == ListenerKind.Viewer).ToList();

        var viewerEntry = Assert.Single(viewerEntries);
        Assert.True(viewerEntry.IsAnyIP);
        Assert.Null(viewerEntry.Address);
        Assert.Equal(8514, viewerEntry.Port);
    }

    [Fact]
    public void Create_ViewerPublicAccessLocalhostOnly_ViewerEntriesAreLoopbackBothStacks()
    {
        var configuration = CreateConfiguration(ViewerPublicAccess.LocalhostOnly, adminHttpPort: 8515);

        var entries = ListenerBindPlan.Create(configuration);

        var viewerEntries = entries.Where(e => e.Kind == ListenerKind.Viewer).ToList();

        Assert.Equal(2, viewerEntries.Count);
        Assert.All(viewerEntries, e => Assert.False(e.IsAnyIP));
        Assert.Contains(viewerEntries, e => Equals(e.Address, IPAddress.Loopback));
        Assert.Contains(viewerEntries, e => Equals(e.Address, IPAddress.IPv6Loopback));
    }

    [Fact]
    public void Create_AdminPortHonorsConfiguredValue()
    {
        var configuration = CreateConfiguration(ViewerPublicAccess.Lan, adminHttpPort: 19999);

        var entries = ListenerBindPlan.Create(configuration);

        var adminEntries = entries.Where(e => e.Kind == ListenerKind.Admin).ToList();
        Assert.All(adminEntries, e => Assert.Equal(19999, e.Port));
    }

    [Fact]
    public void Create_AdminRemoteBindingEnabled_AddsAnyIpHttpsEntryAlongsideLoopback()
    {
        // ADR-0010 Phase 2 決定 1・4: リモートバインド有効時は loopback 2 エントリを置き換えず、
        // 別ポートへの AnyIP + HTTPS エントリを追加する（loopback 経由の復旧が常に残る）。
        var configuration = CreateConfiguration(
            ViewerPublicAccess.Lan,
            adminHttpPort: 8515,
            adminRemoteBindingEnabled: true,
            adminHttpsPort: 8516);

        var entries = ListenerBindPlan.Create(configuration);

        var adminEntries = entries.Where(e => e.Kind == ListenerKind.Admin).ToList();
        Assert.Equal(3, adminEntries.Count);

        var loopbackEntries = adminEntries.Where(e => !e.RequiresHttps).ToList();
        Assert.Equal(2, loopbackEntries.Count);
        Assert.All(loopbackEntries, e => Assert.False(e.IsAnyIP));
        Assert.All(loopbackEntries, e => Assert.Equal(8515, e.Port));

        var remoteEntry = Assert.Single(adminEntries, e => e.RequiresHttps);
        Assert.True(remoteEntry.IsAnyIP);
        Assert.Equal(8516, remoteEntry.Port);
    }

    [Fact]
    public void Create_AdminRemoteBindingDisabled_NoHttpsEntry()
    {
        var configuration = CreateConfiguration(ViewerPublicAccess.Lan, adminHttpPort: 8515);

        var entries = ListenerBindPlan.Create(configuration);

        Assert.DoesNotContain(entries, e => e.RequiresHttps);
    }

    [Fact]
    public void Create_AdminRemoteBindingWithOsAssignedPort_ResolvesToConcretePort()
    {
        // ポート 0(OS 採番。テスト用)指定時、ListenerBindPlan が具体ポートへ確定させて返すこと
        // (PR #224 の実バグ回帰: 0 のまま返すと Program 側のポートガード
        // (UseYaguraListenerPortGuard/YaguraAdminListenerPort)が実ポートを認識できず、
        // リモート HTTPS 経由の到達が全て 404 になる——ResolvePortForAnyIP のコメント参照)。
        var configuration = CreateConfiguration(
            ViewerPublicAccess.Lan,
            adminHttpPort: 8515,
            adminRemoteBindingEnabled: true,
            adminHttpsPort: 0);

        var entries = ListenerBindPlan.Create(configuration);

        var remoteEntry = Assert.Single(entries, e => e.RequiresHttps);
        Assert.True(remoteEntry.Port > 0, "OS 採番(0)指定でも具体ポートへ解決されること。");
    }

    private static ResolvedYaguraConfiguration CreateConfiguration(
        ViewerPublicAccess viewerPublicAccess,
        int adminHttpPort,
        bool adminRemoteBindingEnabled = false,
        int adminHttpsPort = 8516) =>
        new(
            DataRoot: Path.GetTempPath(),
            UdpBindAddress: "0.0.0.0",
            UdpPort: 514,
            UdpReceiveBufferBytes: Yagura.Ingestion.Udp.UdpSyslogListenerOptions.DefaultReceiveBufferBytes,
            TcpBindAddress: "0.0.0.0",
            TcpPort: 514,
            DefaultRfc3164TimeZone: TimeZoneInfo.Utc,
            HttpPort: 8514,
            ViewerPublicAccess: viewerPublicAccess,
            ViewerReverseDnsEnabled: true,
            ViewerWindowsAuthEnabled: false,
            ViewerWindowsAuthKerberosOnly: false,
            ViewerWindowsViewerGroups: Array.Empty<string>(),
            ViewerWindowsAdminGroups: Array.Empty<string>(),
            AdminHttpPort: adminHttpPort,
            AdminWindowsAuthEnabled: false,
            AdminWindowsAuthKerberosOnly: false,
            AdminWindowsAdminGroups: Array.Empty<string>(),
            AdminAppAuthEnabled: false,
            AdminAuthRequireForLoopback: false,
            AdminRemoteBindingEnabled: adminRemoteBindingEnabled,
            AdminHttpsEnabled: adminRemoteBindingEnabled,
            AdminHttpsCertificateThumbprint: adminRemoteBindingEnabled ? new string('A', 40) : null,
            AdminHttpsPort: adminHttpsPort,
            SqliteFileName: "yagura.db",
            SpoolEnabled: true,
            SpoolDirectory: Path.Combine(Path.GetTempPath(), "spool"),
            SpoolQuotaBytes: 1024,
            RetentionDays: null,
            RetentionExecutionTimeOfDay: new TimeOnly(3, 0),
            StorageProvider: StorageProvider.Sqlite,
            SqlServerConnectionString: null,
            IngestionTlsEnabled: false,
            IngestionTlsBindAddress: "0.0.0.0",
            IngestionTlsPort: 6514,
            IngestionTlsCertificateThumbprint: null);
}
