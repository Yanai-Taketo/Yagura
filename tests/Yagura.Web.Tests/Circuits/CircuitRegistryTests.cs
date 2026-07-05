using Yagura.Web.Circuits;

namespace Yagura.Web.Tests.Circuits;

/// <summary>
/// <see cref="CircuitRegistry"/> の単体テスト（M8-4。Issue #71。security.md §2.2）。
/// </summary>
public sealed class CircuitRegistryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 6, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CountsPerListener_UnknownAttributionCountsAsViewer()
    {
        var registry = new CircuitRegistry();
        registry.Register(NewRecord("viewer-1", isAdmin: false));
        registry.Register(NewRecord("admin-1", isAdmin: true));
        registry.Register(NewRecord("unknown-1", isAdmin: null));

        // 上限はリスナごと（security.md §2.2）。帰属不明は閲覧側に数える（安全側——
        // 管理枠を不明な circuit に食わせない）。
        Assert.Equal(2, registry.Count(adminListener: false));
        Assert.Equal(1, registry.Count(adminListener: true));
    }

    [Fact]
    public async Task RequestDisconnect_DeliversToSubscriberAndReportsAcceptance()
    {
        var registry = new CircuitRegistry();
        var record = NewRecord("c1", isAdmin: false);
        registry.Register(record);

        string? deliveredReason = null;
        record.Context.TerminationRequested += reason =>
        {
            deliveredReason = reason;
            return Task.CompletedTask;
        };

        var accepted = await registry.RequestDisconnectAsync("c1", CircuitTerminationReasons.DisconnectedByAdministrator);

        Assert.True(accepted);
        Assert.Equal(CircuitTerminationReasons.DisconnectedByAdministrator, deliveredReason);
    }

    [Fact]
    public async Task RequestDisconnect_UnknownCircuitOrNoSubscriber_IsNotAccepted()
    {
        var registry = new CircuitRegistry();
        registry.Register(NewRecord("no-subscriber", isAdmin: false));

        // 対象が存在しない。
        Assert.False(await registry.RequestDisconnectAsync("missing", "reason"));

        // 購読者（circuit 内の CircuitGovernor）が未登録——協調方式の限界を結果で観測できる。
        Assert.False(await registry.RequestDisconnectAsync("no-subscriber", "reason"));
    }

    [Fact]
    public async Task ReclaimIdle_UsesShortTimeoutForAdmin_AndLongTimeoutForViewer()
    {
        // SEC-8 仮値: 管理 = 30 分（短め。放置された管理画面のロックアウト防止）/
        // 閲覧 = 8 時間（長め。掲示用途を殺さない）。security.md §2.2。
        var registry = new CircuitRegistry();

        var admin = NewRecord("admin", isAdmin: true);
        var viewer = NewRecord("viewer", isAdmin: false);
        registry.Register(admin);
        registry.Register(viewer);

        var reclaimedIds = new List<string>();
        admin.Context.TerminationRequested += _ => { reclaimedIds.Add("admin"); return Task.CompletedTask; };
        viewer.Context.TerminationRequested += _ => { reclaimedIds.Add("viewer"); return Task.CompletedTask; };

        // 1 時間後: 管理側だけが回収対象（30 分超過）。閲覧側（8 時間未満）は残る。
        var reclaimed = await registry.ReclaimIdleAsync(T0 + TimeSpan.FromHours(1));
        Assert.Equal(["admin"], reclaimedIds);
        Assert.Single(reclaimed);

        // 9 時間後: 閲覧側も回収対象になる。
        await registry.ReclaimIdleAsync(T0 + TimeSpan.FromHours(9));
        Assert.Contains("viewer", reclaimedIds);
    }

    [Fact]
    public async Task ReclaimIdle_ActivityResetsIdleClock()
    {
        // SEC-8 の「操作」= inbound activity。活動があれば回収されない。
        var registry = new CircuitRegistry();
        var viewer = NewRecord("viewer", isAdmin: false);
        registry.Register(viewer);

        var reclaimedCount = 0;
        viewer.Context.TerminationRequested += _ => { reclaimedCount++; return Task.CompletedTask; };

        registry.RecordActivity("viewer", T0 + TimeSpan.FromHours(7));

        // 確立から 8 時間超だが、最終活動からは 1 時間——回収しない。
        var reclaimed = await registry.ReclaimIdleAsync(T0 + TimeSpan.FromHours(8));
        Assert.Empty(reclaimed);
        Assert.Equal(0, reclaimedCount);
    }

    [Fact]
    public async Task Unregister_RemovesFromSnapshotAndCounts()
    {
        var registry = new CircuitRegistry();
        registry.Register(NewRecord("c1", isAdmin: false));

        registry.Unregister("c1");

        Assert.Empty(registry.Snapshot());
        Assert.Equal(0, registry.Count(adminListener: false));
        Assert.False(await registry.RequestDisconnectAsync("c1", "reason"));
    }

    private static CircuitRecord NewRecord(string id, bool? isAdmin) =>
        new(id, "127.0.0.1", isAdmin, T0, new YaguraCircuitContext { IsAdminListener = isAdmin });
}
