using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Yagura.Web.Administration;

namespace Yagura.Web.Tests.Administration;

/// <summary>
/// 閲覧認可ポリシー（<see cref="AdminAuthenticationExtensions.ViewerPolicyName"/>。ADR-0010 Phase 4 決定 7）の
/// 認可判定を、DI に登録した実ポリシーへ <see cref="IAuthorizationService"/> で問い合わせて検証する統合テスト。
/// </summary>
/// <remarks>
/// <b>レビュー指摘（田中・クリス・auth-wiring）への回帰固定</b>: 「管理帰属ポートなら無条件 allow」だと、リモート
/// 管理 HTTPS ポート（<see cref="YaguraAdminListenerPort.Ports"/> に含まれる別ポート）経由の閲覧ルートが未認証で
/// 読めてしまう。修正は「管理リスナの loopback 束縛ポート（<c>Ports[0]</c>）のみ対象外・リモート管理ポート/閲覧
/// リスナは閲覧セッション必須」。本テストがその境界を固定する。
/// </remarks>
public sealed class ViewerPolicyAuthorizationTests
{
    private const int LoopbackAdminPort = 8515;
    private const int RemoteAdminPort = 8516;
    private const int ViewerPort = 8514;

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // 閲覧認証有効の構成でスキーム・認可ポリシー（管理 + 閲覧）を登録する。
        services.AddYaguraAdminAuthentication(
            windowsAuthEnabled: false, kerberosOnly: false, appAuthEnabled: false,
            viewerWindowsAuthEnabled: true, viewerKerberosOnly: false);
        // Ports[0] = loopback 管理ポート、Ports[1] = リモート管理 HTTPS ポート（ADR-0010 Phase 2）。
        services.AddSingleton(new YaguraAdminListenerPort(new[] { LoopbackAdminPort, RemoteAdminPort }));
        return services.BuildServiceProvider();
    }

    private static HttpContext ContextOn(int localPort, ServiceProvider provider)
    {
        var ctx = new DefaultHttpContext { RequestServices = provider };
        ctx.Connection.LocalPort = localPort;
        return ctx;
    }

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());
    private static ClaimsPrincipal ViewerSession() =>
        AdminAuthenticationExtensions.CreateViewerSessionPrincipal(AdminAuthenticationExtensions.WindowsAuthMethod, "YAGURA\\v", 0);
    private static ClaimsPrincipal AdminSession() =>
        AdminAuthenticationExtensions.CreateAdminSessionPrincipal(AdminAuthenticationExtensions.AppAuthMethod, "admin1", 0);

    private static async Task<bool> AuthorizeAsync(ClaimsPrincipal user, int localPort)
    {
        using var provider = BuildProvider();
        var authz = provider.GetRequiredService<IAuthorizationService>();
        var result = await authz.AuthorizeAsync(user, ContextOn(localPort, provider), AdminAuthenticationExtensions.ViewerPolicyName);
        return result.Succeeded;
    }

    [Fact]
    public async Task LoopbackAdminPort_AllowsWithoutViewingSession()
    {
        // 管理リスナの loopback 面はローカル復旧/全アクセス経路——閲覧画面も無条件で通す（管理面の規則）。
        Assert.True(await AuthorizeAsync(Anonymous(), LoopbackAdminPort));
    }

    [Fact]
    public async Task RemoteAdminPort_Anonymous_IsDenied()
    {
        // 回帰の核心（田中・クリス）: リモート管理 HTTPS ポート経由の閲覧ルートは未認証では拒否する
        // （RemoteBinding + 閲覧認証を同時有効化した構成でログ本体・CSV が無認証で読める穴を塞ぐ）。
        Assert.False(await AuthorizeAsync(Anonymous(), RemoteAdminPort));
    }

    [Fact]
    public async Task ViewerPort_Anonymous_IsDenied()
    {
        Assert.False(await AuthorizeAsync(Anonymous(), ViewerPort));
    }

    [Fact]
    public async Task ViewerPort_ViewerSession_IsAllowed()
    {
        Assert.True(await AuthorizeAsync(ViewerSession(), ViewerPort));
    }

    [Fact]
    public async Task RemoteAdminPort_ViewerSession_IsAllowed()
    {
        // 閲覧セッションを持てばリモート管理ポート上の閲覧画面も読める（管理 ⊇ 閲覧の一部）。
        Assert.True(await AuthorizeAsync(ViewerSession(), RemoteAdminPort));
    }

    [Fact]
    public async Task RemoteAdminPort_AdminSession_IsAllowed()
    {
        // 管理セッションは管理 ⊇ 閲覧で閲覧できる。
        Assert.True(await AuthorizeAsync(AdminSession(), RemoteAdminPort));
    }
}
