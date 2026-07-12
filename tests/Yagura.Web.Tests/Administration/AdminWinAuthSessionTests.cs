using System.Security.Claims;
using Yagura.Web.Administration;

namespace Yagura.Web.Tests.Administration;

/// <summary>
/// ADR-0013（共存セッションモデル・Issue #252）の新ロジックの単体固定。
/// </summary>
/// <remarks>
/// Windows 統合認証（Negotiate）の実ハンドシェイクは OS の SSPI に依存しインプロセスでは再現できない
/// （<see cref="AdminAuthLoginEndpointTests"/> の remarks と同じ理由——conventions.md「実環境依存は lab 検証」）。
/// 本ファイルは Negotiate に依存しない「認証成立後の単一 Cookie セッション」の組み立て・判定・監査導出を固定する。
/// 実 Negotiate → Cookie 発行 → /admin 到達（200）の端到端は lab（ADR-0013 受け入れ条件 1・2）が担う。
/// </remarks>
public sealed class AdminWinAuthSessionTests
{
    [Theory]
    [InlineData("windows", "YAGURA\\Administrator")]
    [InlineData("app", "admin1")]
    public void CreateAdminSessionPrincipal_CarriesMethodSessionAndGenerationClaims(string method, string name)
    {
        var principal = AdminAuthenticationExtensions.CreateAdminSessionPrincipal(method, name, generation: 3);

        Assert.True(principal.Identity?.IsAuthenticated);
        Assert.Equal(name, principal.Identity?.Name);
        Assert.Equal(method, principal.FindFirst(AdminAuthenticationExtensions.AuthMethodClaimType)?.Value);
        Assert.Equal("1", principal.FindFirst(AdminAuthenticationExtensions.AdminSessionClaimType)?.Value);
        Assert.Equal("3", principal.FindFirst(AdminAuthenticationExtensions.SessionGenerationClaimType)?.Value);
    }

    [Fact]
    public void IsAdminSessionAuthenticated_TrueOnlyWithAdminSessionClaim()
    {
        // 管理セッションクレームを持つ認証済み Cookie principal → true（方式に依らない）。
        Assert.True(AdminAuthenticationExtensions.IsAdminSessionAuthenticated(
            AdminAuthenticationExtensions.CreateAdminSessionPrincipal(AdminAuthenticationExtensions.WindowsAuthMethod, "YAGURA\\Administrator", 0)));
        Assert.True(AdminAuthenticationExtensions.IsAdminSessionAuthenticated(
            AdminAuthenticationExtensions.CreateAdminSessionPrincipal(AdminAuthenticationExtensions.AppAuthMethod, "admin1", 0)));

        // fail-closed（ADR-0013 決定 5）: 認証済みでも標識クレームを欠く principal は管理者と認めない。
        var authenticatedNoClaim = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "x") }, AdminAuthenticationExtensions.AppAuthenticationScheme));
        Assert.False(AdminAuthenticationExtensions.IsAdminSessionAuthenticated(authenticatedNoClaim));

        // 匿名 → false。
        Assert.False(AdminAuthenticationExtensions.IsAdminSessionAuthenticated(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("app")]
    public void AuditActorResolver_DerivesSchemeFromAuthMethodClaim_NotSchemeName(string method)
    {
        var principal = AdminAuthenticationExtensions.CreateAdminSessionPrincipal(method, "user1", 0);

        var (scheme, name) = AuditActorResolver.Resolve(principal);

        Assert.Equal(method, scheme);
        Assert.Equal("user1", name);
    }

    [Fact]
    public void AuditActorResolver_FailClosed_WhenAdminSessionClaimMissing()
    {
        // 方式クレームだけあって管理セッション標識を欠く（偽装耐性）→ (null, null)。
        var forged = new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Name, "attacker"),
                new Claim(AdminAuthenticationExtensions.AuthMethodClaimType, "windows"),
            },
            AdminAuthenticationExtensions.AppAuthenticationScheme));

        var (scheme, name) = AuditActorResolver.Resolve(forged);

        Assert.Null(scheme);
        Assert.Null(name);
    }

    [Fact]
    public void AuditActorResolver_Anonymous_ReturnsNulls()
    {
        var (scheme, name) = AuditActorResolver.Resolve(new ClaimsPrincipal(new ClaimsIdentity()));
        Assert.Null(scheme);
        Assert.Null(name);
    }

    [Fact]
    public void WindowsSession_ShorterAbsoluteLifetimeThanApp()
    {
        // ADR-0013 決定 2: Windows 由来 Cookie は app より大幅に短い絶対寿命（544 失効遅延の有界化）。
        Assert.True(AdminAuthenticationExtensions.WindowsSessionAbsoluteLifetime <
            AdminAuthenticationExtensions.AppSessionAbsoluteLifetime);
    }
}
