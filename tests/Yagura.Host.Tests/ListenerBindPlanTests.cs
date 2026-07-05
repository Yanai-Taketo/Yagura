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

    private static ResolvedYaguraConfiguration CreateConfiguration(ViewerPublicAccess viewerPublicAccess, int adminHttpPort) =>
        new(
            DataRoot: Path.GetTempPath(),
            UdpBindAddress: "0.0.0.0",
            UdpPort: 514,
            TcpBindAddress: "0.0.0.0",
            TcpPort: 514,
            HttpPort: 8514,
            ViewerPublicAccess: viewerPublicAccess,
            AdminHttpPort: adminHttpPort,
            SqliteFileName: "yagura.db",
            SpoolEnabled: true,
            SpoolDirectory: Path.Combine(Path.GetTempPath(), "spool"),
            SpoolQuotaBytes: 1024,
            RetentionDays: null,
            RetentionExecutionTimeOfDay: new TimeOnly(3, 0),
            StorageProvider: StorageProvider.Sqlite,
            SqlServerConnectionString: null);
}
