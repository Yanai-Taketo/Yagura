using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;

namespace Yagura.Web.Administration;

/// <summary>
/// 閲覧 UI ログイン/ログアウトの HTTP エンドポイント（ADR-0010 Phase 4 決定 7・SEC-9）。
/// </summary>
/// <remarks>
/// <para>
/// <b>管理側（<see cref="AdminAuthEndpoints"/>）と同型だが閲覧リスナ帰属を持たない</b>: これらの
/// エンドポイントは <see cref="ListenerPortGuardEndpointMetadata.Admin"/> を付与しない——閲覧リスナ
/// （8514）で未認証のまま到達できる必要があるため（管理ログイン <c>/admin/login/*</c> は管理リスナ帰属で
/// :8514 では 404 になり、閲覧者のログイン経路にならない）。ルートは <c>/login</c> 系（<c>/admin/login</c> 系とは別）。
/// </para>
/// <para>
/// <b>役割判定（決定 7・SEC-9）</b>: Windows ログインは AD グループ所属で役割を決める——閲覧の
/// 管理グループ（<c>Viewer:...:AdminGroups</c>）または 544 に該当すれば管理セッション（管理 ⊇ 閲覧）、
/// 閲覧グループ（<c>Viewer:...:ViewerGroups</c>）に該当すれば閲覧セッション、いずれにも非所属なら拒否。
/// アプリ独自ログイン（<c>/login/app</c>）は管理役割のみ（決定 5）——閲覧リスナ経由でも管理セッションを
/// 発行し、管理 ⊇ 閲覧で閲覧に到達する（:8514 では管理機能の導線は出さない——<c>MainLayout</c> の
/// <c>IsAdminListener</c> ガード）。
/// </para>
/// </remarks>
internal static class ViewerAuthEndpoints
{
    /// <summary>閲覧ログイン成功後の遷移先（閲覧ダッシュボード）。</summary>
    private const string ViewerHomePath = "/";

    /// <summary>Windows ログインで判定された役割。</summary>
    private enum WindowsRole
    {
        /// <summary>閲覧/管理いずれのグループにも非所属——拒否。</summary>
        None,

        /// <summary>閲覧グループ所属——閲覧セッション。</summary>
        Viewer,

        /// <summary>管理グループ（または 544）所属——管理セッション（管理 ⊇ 閲覧）。</summary>
        Admin,
    }

    public static void MapViewerAuthEndpoints(
        this IEndpointRouteBuilder endpoints, bool windowsAuthEnabled, bool appAuthAvailable)
    {
        if (windowsAuthEnabled)
        {
            // Windows 認証が無効な構成では Negotiate スキーム自体が未登録のため、[Authorize(Negotiate)] を
            // 課したまま登録すると到達時に 500 になる（管理側 MapWindowsLogin と同じ条件付き登録）。
            MapViewerWindowsLogin(endpoints);
        }

        if (appAuthAvailable)
        {
            MapViewerAppLogin(endpoints);
        }

        MapViewerLogout(endpoints);
    }

    private static void MapViewerWindowsLogin(IEndpointRouteBuilder endpoints)
    {
        // GET: Negotiate 認証 + 役割判定 → 確認画面（副作用なし。login CSRF 対策は管理側 ADR-0013 決定 4 と同型）。
        var getEndpoint = endpoints.MapGet("/login/windows", async (
            HttpContext context,
            IAntiforgery antiforgery,
            IAuditRecorder auditRecorder,
            WindowsGroupAuthorizationOptions groups,
            TimeProvider timeProvider) =>
        {
            var user = context.User;
            var role = ResolveWindowsRole(user, groups);

            if (role == WindowsRole.None)
            {
                await RecordViewerAuthorizationDeniedAsync(auditRecorder, context, timeProvider).ConfigureAwait(false);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync(
                    "Windows 認証には成功しましたが、閲覧を許可された AD グループに所属していないため" +
                    "閲覧 UI へアクセスできません。",
                    context.RequestAborted).ConfigureAwait(false);
                return;
            }

            var tokens = antiforgery.GetAndStoreTokens(context);
            await WriteViewerLoginConfirmPageAsync(
                context, user.Identity?.Name ?? string.Empty, role, tokens.RequestToken ?? string.Empty).ConfigureAwait(false);
        });

        getEndpoint.RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = NegotiateDefaults.AuthenticationScheme });

        // POST: antiforgery 検証 + Negotiate 再認証 + 役割再判定 → SignInAsync（認証セッション Cookie 発行）。
        var postEndpoint = endpoints.MapPost("/login/windows", async (
            HttpContext context,
            IAntiforgery antiforgery,
            IAdminSessionGenerationStore generationStore,
            IAuditRecorder auditRecorder,
            WindowsGroupAuthorizationOptions groups,
            TimeProvider timeProvider) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
            }
            catch (AntiforgeryValidationException)
            {
                context.Response.Redirect($"{AdminAuthenticationExtensions.ViewerLoginPath}?error=csrf");
                return;
            }

            var user = context.User;
            var role = ResolveWindowsRole(user, groups);
            if (role == WindowsRole.None)
            {
                await RecordViewerAuthorizationDeniedAsync(auditRecorder, context, timeProvider).ConfigureAwait(false);
                context.Response.Redirect($"{AdminAuthenticationExtensions.ViewerLoginPath}?error=1");
                return;
            }

            var principalName = user.Identity?.Name ?? string.Empty;
            var generation = generationStore.CurrentGeneration;

            // 管理役割は管理セッション（管理 ⊇ 閲覧）、閲覧役割は閲覧セッションを発行する（ADR-0010 決定 7）。
            var principal = role == WindowsRole.Admin
                ? AdminAuthenticationExtensions.CreateAdminSessionPrincipal(
                    AdminAuthenticationExtensions.WindowsAuthMethod, principalName, generation)
                : AdminAuthenticationExtensions.CreateViewerSessionPrincipal(
                    AdminAuthenticationExtensions.WindowsAuthMethod, principalName, generation);

            var now = timeProvider.GetUtcNow();
            // Windows 由来セッションは短い絶対寿命・sliding 無効（ADR-0013 決定 2。544/グループ失効の遅延を有界化）。
            var props = AdminAuthenticationExtensions.BuildSessionSignInProperties(
                now, AdminAuthenticationExtensions.WindowsSessionAbsoluteLifetime, allowRefresh: false);

            await context.SignInAsync(AdminAuthenticationExtensions.AppAuthenticationScheme, principal, props).ConfigureAwait(false);

            await auditRecorder.RecordAsync(new AuditEvent(
                OccurredAt: now,
                Kind: AuditEventKind.ViewerLoginSucceeded,
                RemoteAddress: context.Connection.RemoteIpAddress?.ToString(),
                RemotePort: context.Connection.RemotePort,
                ReachedListenerPort: context.Connection.LocalPort,
                Detail: role == WindowsRole.Admin ? "scheme=windows role=admin" : "scheme=windows role=viewer",
                AuthenticationScheme: "windows",
                AuthenticatedPrincipal: principalName),
                CancellationToken.None).ConfigureAwait(false);

            context.Response.Redirect(ViewerHomePath);
        });

        postEndpoint.RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = NegotiateDefaults.AuthenticationScheme });
    }

    /// <summary>
    /// アプリ独自 ID/パスワードの閲覧ログイン POST（<c>/login/app</c>）。管理役割のみ（決定 5）——共有の
    /// <see cref="AppLoginEndpointHandler"/> で管理セッションを発行し、管理 ⊇ 閲覧で閲覧に到達する。
    /// 応答整形（列挙耐性・三層防御）は管理ログインとバイト単位で共有する。
    /// </summary>
    private static void MapViewerAppLogin(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/login/app", (
            HttpContext context,
            IAntiforgery antiforgery,
            IAppAdminAuthenticator authenticator,
            IAdminSessionGenerationStore generationStore,
            IAuditRecorder auditRecorder,
            TimeProvider timeProvider) =>
            AppLoginEndpointHandler.HandleAsync(
                context, antiforgery, authenticator, generationStore, auditRecorder, timeProvider,
                loginPath: AdminAuthenticationExtensions.ViewerLoginPath,
                loginTitle: Yagura.Web.Components.Common.UiText.ViewerLoginTitle,
                successRedirect: ViewerHomePath,
                successAuditKind: AuditEventKind.ViewerLoginSucceeded,
                successDetail: "scheme=app role=admin"));
    }

    private static void MapViewerLogout(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
            }
            catch (AntiforgeryValidationException)
            {
                context.Response.Redirect(AdminAuthenticationExtensions.ViewerLoginPath);
                return;
            }

            await context.SignOutAsync(AdminAuthenticationExtensions.AppAuthenticationScheme).ConfigureAwait(false);
            context.Response.Redirect(AdminAuthenticationExtensions.ViewerLoginPath);
        });
    }

    /// <summary>
    /// Windows ログインの役割判定（ADR-0010 決定 7・SEC-9）: 管理グループ（<c>Viewer:...:AdminGroups</c> または 544）
    /// が優先（管理 ⊇ 閲覧）、次いで閲覧グループ（<c>Viewer:...:ViewerGroups</c>）、いずれも非所属なら
    /// <see cref="WindowsRole.None"/>。
    /// </summary>
    private static WindowsRole ResolveWindowsRole(ClaimsPrincipal user, WindowsGroupAuthorizationOptions groups)
    {
        if (AdminAuthenticationExtensions.IsWindowsAdministrator(user, groups.ViewerAdminGroupSids))
        {
            return WindowsRole.Admin;
        }

        if (AdminAuthenticationExtensions.IsWindowsViewer(user, groups.ViewerGroupSids))
        {
            return WindowsRole.Viewer;
        }

        return WindowsRole.None;
    }

    private static async Task RecordViewerAuthorizationDeniedAsync(
        IAuditRecorder auditRecorder, HttpContext context, TimeProvider timeProvider)
    {
        // Negotiate は成立したが閲覧/管理いずれのグループにも非所属（認証成功 ≠ 閲覧権限。決定 7・SEC-9）。
        // 管理側の AdminAuthorizationDenied（3008）と対をなす ViewerAuthorizationDenied（3009）。
        await auditRecorder.RecordAsync(new AuditEvent(
            OccurredAt: timeProvider.GetUtcNow(),
            Kind: AuditEventKind.ViewerAuthorizationDenied,
            RemoteAddress: context.Connection.RemoteIpAddress?.ToString(),
            RemotePort: context.Connection.RemotePort,
            AttemptedPath: context.Request.Path,
            ReachedListenerPort: context.Connection.LocalPort,
            Detail: "authenticated-but-not-in-viewer-or-admin-group",
            AuthenticationScheme: "windows",
            AuthenticatedPrincipal: context.User.Identity?.Name),
            context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>閲覧 Windows ログインの確認画面（副作用なし・antiforgery トークン付きフォーム）。</summary>
    private static async Task WriteViewerLoginConfirmPageAsync(
        HttpContext context, string principalName, WindowsRole role, string antiforgeryToken)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        var name = System.Net.WebUtility.HtmlEncode(principalName);
        var token = System.Net.WebUtility.HtmlEncode(antiforgeryToken);
        var roleLabel = role == WindowsRole.Admin ? "管理（閲覧を含む）" : "閲覧";
        var html = $$"""
            <!doctype html>
            <html lang="ja">
            <head><meta charset="utf-8"><title>{{Yagura.Web.Components.Common.UiText.ViewerLoginTitle}}</title></head>
            <body>
            <p>Windows 認証に成功しました: <strong>{{name}}</strong>（役割: {{roleLabel}}）</p>
            <p>この資格情報で閲覧 UI にサインインします。</p>
            <form method="post" action="/login/windows">
            <input type="hidden" name="__RequestVerificationToken" value="{{token}}" />
            <button type="submit">サインインを続ける</button>
            </form>
            <p><a href="/login">サインイン画面に戻る</a></p>
            </body>
            </html>
            """;
        await context.Response.WriteAsync(html, context.RequestAborted).ConfigureAwait(false);
    }
}
