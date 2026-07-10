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
    private static readonly IReadOnlyList<int> AdminPorts = [AdminPort];

    [Fact]
    public void HttpRequestContext_LocalPortDecides()
    {
        // prerender / 静的 SSR: 接続の実ローカルポートが唯一の真実（偽装不能——
        // ListenerPortGuardMiddleware と同じ判定根拠）。circuit 帰属より優先する。
        Assert.Equal(AdminScreenAccess.Allowed, AdminScreenAccessPolicy.Decide(AdminPort, null, AdminPorts));
        Assert.Equal(AdminScreenAccess.Denied, AdminScreenAccessPolicy.Decide(8514, null, AdminPorts));
        Assert.Equal(AdminScreenAccess.Denied, AdminScreenAccessPolicy.Decide(8514, true, AdminPorts));
    }

    [Fact]
    public void HttpRequestContext_MultiplePorts_AnyConfiguredPortDecides()
    {
        // ADR-0010 Phase 2: リモートバインド有効時は loopback ポートに加えリモート HTTPS ポートも
        // 管理リスナとして認識する（YaguraAdminListenerPort.Ports の複数値対応）。
        IReadOnlyList<int> multiplePorts = [8515, 8516];

        Assert.Equal(AdminScreenAccess.Allowed, AdminScreenAccessPolicy.Decide(8515, null, multiplePorts));
        Assert.Equal(AdminScreenAccess.Allowed, AdminScreenAccessPolicy.Decide(8516, null, multiplePorts));
        Assert.Equal(AdminScreenAccess.Denied, AdminScreenAccessPolicy.Decide(8514, null, multiplePorts));
    }

    [Fact]
    public void InteractiveContext_CircuitAttributionDecides()
    {
        Assert.Equal(AdminScreenAccess.Allowed, AdminScreenAccessPolicy.Decide(null, true, AdminPorts));
        Assert.Equal(AdminScreenAccess.Denied, AdminScreenAccessPolicy.Decide(null, false, AdminPorts));
    }

    [Fact]
    public void UnknownAttribution_IsFailClosed()
    {
        // 帰属を判定できない場合は描画しない（fail-closed。configuration.md §1 の縮小側原則と
        // 同じ向き——実装の取得経路が失陥しても管理画面が閲覧側へ開く方向には倒れない）。
        Assert.Equal(AdminScreenAccess.Undetermined, AdminScreenAccessPolicy.Decide(null, null, AdminPorts));
    }

    // ---- IsAuthenticationSatisfied（ADR-0010 決定 1・2。loopback 認証 opt-in 実効時の第二段判定） ----

    [Theory]
    [InlineData(false, false, false, true)] // 認証不要なら常に充足
    [InlineData(false, false, true, true)]
    [InlineData(true, true, false, true)]   // ログイン画面自身は判定対象外
    [InlineData(true, false, true, true)]   // 認証要求 + 未ログイン画面 + 認可済み → 充足
    [InlineData(true, false, false, false)] // 認証要求 + 未ログイン画面 + 未認可 → 不充足
    public void IsAuthenticationSatisfied_FollowsTruthTable(
        bool authenticationRequired, bool isLoginRoute, bool isAuthorizedUser, bool expected)
    {
        Assert.Equal(
            expected,
            AdminScreenAccessPolicy.IsAuthenticationSatisfied(authenticationRequired, isLoginRoute, isAuthorizedUser));
    }
}
