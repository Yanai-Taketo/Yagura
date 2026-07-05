using Yagura.Web.Administration;

namespace Yagura.Web.Tests.Administration;

/// <summary>
/// <see cref="AdminScreenAccessPolicy"/> の単体テスト（M8-4。Issue #71——管理画面の circuit 層
/// ガード。security.md §1 L-5 の覆域の限界（確立済み circuit 上の対話的ナビゲーション）を
/// 埋める判定が fail-closed であることを固定する）。
/// </summary>
public sealed class AdminScreenAccessPolicyTests
{
    private const int AdminPort = 8515;

    [Fact]
    public void HttpRequestContext_LocalPortDecides()
    {
        // prerender / 静的 SSR: 接続の実ローカルポートが唯一の真実（偽装不能——
        // ListenerPortGuardMiddleware と同じ判定根拠）。circuit 帰属より優先する。
        Assert.Equal(AdminScreenAccess.Allowed, AdminScreenAccessPolicy.Decide(AdminPort, null, AdminPort));
        Assert.Equal(AdminScreenAccess.Denied, AdminScreenAccessPolicy.Decide(8514, null, AdminPort));
        Assert.Equal(AdminScreenAccess.Denied, AdminScreenAccessPolicy.Decide(8514, true, AdminPort));
    }

    [Fact]
    public void InteractiveContext_CircuitAttributionDecides()
    {
        Assert.Equal(AdminScreenAccess.Allowed, AdminScreenAccessPolicy.Decide(null, true, AdminPort));
        Assert.Equal(AdminScreenAccess.Denied, AdminScreenAccessPolicy.Decide(null, false, AdminPort));
    }

    [Fact]
    public void UnknownAttribution_IsFailClosed()
    {
        // 帰属を判定できない場合は描画しない（fail-closed。configuration.md §1 の縮小側原則と
        // 同じ向き——実装の取得経路が失陥しても管理画面が閲覧側へ開く方向には倒れない）。
        Assert.Equal(AdminScreenAccess.Undetermined, AdminScreenAccessPolicy.Decide(null, null, AdminPort));
    }
}
