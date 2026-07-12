using System.Security.Claims;
using Yagura.Web.Administration;

namespace Yagura.Web.Tests.Administration;

/// <summary>
/// 閲覧 UI 認証（ADR-0010 Phase 4 決定 7）+ AD グループマッピング（SEC-9）の役割判定・セッション組み立ての
/// 単体テスト。<see cref="ClaimsPrincipal"/> だけで（AD 実環境なしに）検証できる純粋関数を対象にする——
/// <see cref="System.Security.Principal.WindowsIdentity"/> 型ゲートの正パスは実 Windows トークンを要するため
/// lab 統合検証に委ねる（issue #235 と同じ流儀）。
/// </summary>
public sealed class ViewerAuthorizationTests
{
    private static IReadOnlySet<string> SidSet(params string[] sids) =>
        new HashSet<string>(sids, StringComparer.OrdinalIgnoreCase);

    private static ClaimsPrincipal WithGroupSids(params string[] sids)
    {
        var identity = new ClaimsIdentity("test");
        foreach (var sid in sids)
        {
            identity.AddClaim(new Claim(ClaimTypes.GroupSid, sid));
        }

        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public void HasAnyGroupSid_TrueWhenTokenGroupIntersectsConfiguredSet()
    {
        var user = WithGroupSids("S-1-5-21-1-2-3-1001", "S-1-5-21-1-2-3-1002");
        Assert.True(AdminAuthenticationExtensions.HasAnyGroupSid(user, SidSet("S-1-5-21-1-2-3-1002")));
    }

    [Fact]
    public void HasAnyGroupSid_CaseInsensitiveOnSidHexParts()
    {
        // SID 文字列の比較は大文字小文字を区別しない（解決段の HashSet も OrdinalIgnoreCase）。
        var user = WithGroupSids("S-1-5-21-1-2-3-1001");
        Assert.True(AdminAuthenticationExtensions.HasAnyGroupSid(user, SidSet("s-1-5-21-1-2-3-1001")));
    }

    [Fact]
    public void HasAnyGroupSid_FalseWhenNoIntersectionOrEmptyConfig()
    {
        var user = WithGroupSids("S-1-5-21-1-2-3-1001");
        Assert.False(AdminAuthenticationExtensions.HasAnyGroupSid(user, SidSet("S-1-5-21-1-2-3-9999")));
        Assert.False(AdminAuthenticationExtensions.HasAnyGroupSid(user, SidSet()));
    }

    [Fact]
    public void HasAnyGroupSid_IgnoresNonGroupSidClaimTypes()
    {
        // GroupSid 以外のクレーム型（例: Name）が偶然同じ値でも交差判定に混ぜない。
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(ClaimTypes.Name, "S-1-5-21-1-2-3-1001"));
        var user = new ClaimsPrincipal(identity);
        Assert.False(AdminAuthenticationExtensions.HasAnyGroupSid(user, SidSet("S-1-5-21-1-2-3-1001")));
    }

    [Fact]
    public void CreateViewerSessionPrincipal_CarriesViewerSessionMethodAndGeneration_NotAdminSession()
    {
        var principal = AdminAuthenticationExtensions.CreateViewerSessionPrincipal(
            AdminAuthenticationExtensions.WindowsAuthMethod, "YAGURA\\viewer1", generation: 7);

        Assert.True(AdminAuthenticationExtensions.IsViewerSessionAuthenticated(principal));
        Assert.False(AdminAuthenticationExtensions.IsAdminSessionAuthenticated(principal));
        Assert.Equal("YAGURA\\viewer1", principal.Identity?.Name);
        Assert.Equal(
            AdminAuthenticationExtensions.WindowsAuthMethod,
            principal.FindFirst(AdminAuthenticationExtensions.AuthMethodClaimType)?.Value);
        Assert.Equal("7", principal.FindFirst(AdminAuthenticationExtensions.SessionGenerationClaimType)?.Value);
    }

    [Fact]
    public void IsViewingAllowed_TrueForBothAdminAndViewerSessions()
    {
        // 管理 ⊇ 閲覧（決定 7）: 管理セッションも閲覧セッションも閲覧できる。
        var adminSession = AdminAuthenticationExtensions.CreateAdminSessionPrincipal(
            AdminAuthenticationExtensions.AppAuthMethod, "admin1", 0);
        var viewerSession = AdminAuthenticationExtensions.CreateViewerSessionPrincipal(
            AdminAuthenticationExtensions.WindowsAuthMethod, "YAGURA\\viewer1", 0);

        Assert.True(AdminAuthenticationExtensions.IsViewingAllowed(adminSession));
        Assert.True(AdminAuthenticationExtensions.IsViewingAllowed(viewerSession));
    }

    [Fact]
    public void IsViewingAllowed_FalseForAnonymous()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.False(AdminAuthenticationExtensions.IsViewingAllowed(anonymous));
    }

    [Fact]
    public void IsViewerSessionAuthenticated_FalseForAdminSessionAndAnonymous()
    {
        var adminSession = AdminAuthenticationExtensions.CreateAdminSessionPrincipal(
            AdminAuthenticationExtensions.AppAuthMethod, "admin1", 0);
        Assert.False(AdminAuthenticationExtensions.IsViewerSessionAuthenticated(adminSession));
        Assert.False(AdminAuthenticationExtensions.IsViewerSessionAuthenticated(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Fact]
    public void IsWindowsAdministrator_WithGroups_FalseForNonWindowsIdentity_EvenWithMatchingGroupSid()
    {
        // 型ゲート（issue #235）: WindowsIdentity 型でない principal は、設定グループ SID や 544 を
        // クレームとして持っていても管理者と判定しない（Cookie/ClaimsIdentity 経由の偽装を防ぐ）。
        var withConfiguredGroupSid = WithGroupSids("S-1-5-21-1-2-3-1500");
        Assert.False(AdminAuthenticationExtensions.IsWindowsAdministrator(
            withConfiguredGroupSid, SidSet("S-1-5-21-1-2-3-1500")));

        var with544 = WithGroupSids(AdminAuthenticationExtensions.BuiltinAdministratorsSid);
        Assert.False(AdminAuthenticationExtensions.IsWindowsAdministrator(with544, SidSet()));
    }

    [Fact]
    public void IsWindowsViewer_FalseForNonWindowsIdentity_EvenWithMatchingGroupSid()
    {
        var withViewerGroupSid = WithGroupSids("S-1-5-21-1-2-3-2000");
        Assert.False(AdminAuthenticationExtensions.IsWindowsViewer(
            withViewerGroupSid, SidSet("S-1-5-21-1-2-3-2000")));
    }
}
