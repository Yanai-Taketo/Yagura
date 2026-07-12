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
/// 管理 UI ログイン/ログアウトの HTTP エンドポイント（ADR-0010 Phase 1・委任事項 9）。
/// </summary>
/// <remarks>
/// <para>
/// <b>Razor Components ページではなく素の minimal API である理由</b>: アプリ独自認証の Cookie
/// サインインは <c>HttpContext.SignInAsync</c> による応答ヘッダ（<c>Set-Cookie</c>）の設定を要するが、
/// Blazor Interactive Server の circuit は SignalR 接続上でレンダリングを多重化するため、
/// 確立済み circuit からは通常の HTTP 応答ヘッダを直接操作できない（Microsoft Learn の
/// Blazor 認証ガイドが Identity UI 連携で採る「サインインは通常の Razor Pages/MVC エンドポイント、
/// 表示は Blazor コンポーネント」という分離パターンと同型）。ログイン画面
/// （<c>AdminLoginScreen.razor</c>）自体は通常の Razor Components ページとして提供し、
/// フォームの送信先だけを本クラスの素の POST エンドポイントに向ける。
/// </para>
/// <para>
/// <b>CSRF 対策（委任事項 4）</b>: <c>/admin/login/app</c>・<c>/admin/logout</c> は
/// <see cref="IAntiforgery.ValidateRequestAsync"/> で明示検証する（Blazor の
/// <c>UseAntiforgery()</c> ミドルウェアは既定で minimal API エンドポイントを自動検証対象に
/// しないため、ハンドラ内で明示的に呼ぶ）。
/// </para>
/// </remarks>
internal static class AdminAuthEndpoints
{
    public static void MapAdminAuthEndpoints(this IEndpointRouteBuilder endpoints, bool windowsAuthEnabled)
    {
        if (windowsAuthEnabled)
        {
            // Windows 認証が無効な構成では Negotiate スキーム自体が登録されないため、
            // このエンドポイントに [Authorize(AuthenticationSchemes=Negotiate)] を課したまま
            // 登録すると到達時に例外（500）になる。ログイン画面も Windows 認証が有効な場合
            // にのみこの経路へリンクするため、無効時は登録自体を省略する。
            MapWindowsLogin(endpoints);
        }

        MapAppLogin(endpoints);
        MapLogout(endpoints);
        MapInvalidateAllSessions(endpoints);
    }

    /// <summary>
    /// 認証セッションの緊急全失効（ADR-0013 決定 2）。セッション世代番号をバンプして発行済みの全 Cookie を
    /// 即時無効化する（退職者・漏洩疑い時の全ログアウト、および Windows 権限剥奪の即時反映手段）。
    /// </summary>
    /// <remarks>
    /// antiforgery 検証必須。<see cref="AdminAuthenticationExtensions.AdminPolicyName"/> の認可を課す——
    /// loopback 無認証（既定）では loopback バイパスで管理者が実行でき、認証必須構成では認証済み管理者のみ。
    /// バンプは DC 非依存のローカル操作。実行者自身の Cookie も旧世代になり次要求で無効化されるため
    /// ログイン画面へ誘導する（ログイン経路自体は殺さない——app 認証・Windows 再ログイン・手編集は生存）。
    /// </remarks>
    private static void MapInvalidateAllSessions(IEndpointRouteBuilder endpoints)
    {
        var endpoint = endpoints.MapPost("/admin/sessions/invalidate-all", async (
            HttpContext context,
            IAntiforgery antiforgery,
            IAdminSessionGenerationStore generationStore,
            IAuditRecorder auditRecorder,
            TimeProvider timeProvider) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
            }
            catch (AntiforgeryValidationException)
            {
                context.Response.Redirect("/admin/auth-setup?error=csrf");
                return;
            }

            var newGeneration = generationStore.Bump();

            var (scheme, principal) = AuditActorResolver.Resolve(context.User);
            await auditRecorder.RecordAsync(new AuditEvent(
                OccurredAt: timeProvider.GetUtcNow(),
                Kind: AuditEventKind.AdminSessionsInvalidated,
                RemoteAddress: context.Connection.RemoteIpAddress?.ToString(),
                RemotePort: context.Connection.RemotePort,
                ReachedListenerPort: context.Connection.LocalPort,
                Detail: $"generation={newGeneration}",
                AuthenticationScheme: scheme,
                AuthenticatedPrincipal: principal),
                CancellationToken.None).ConfigureAwait(false);

            context.Response.Redirect("/admin/login");
        });

        endpoint
            .WithMetadata(ListenerPortGuardEndpointMetadata.Admin)
            .RequireAuthorization(AdminAuthenticationExtensions.AdminPolicyName);
    }

    /// <summary>
    /// Windows 統合認証のログイン経路（ADR-0013 決定 1・4）。選択式ログインの Windows 経路として、
    /// Negotiate チャレンジをこの 2 エンドポイントにのみ閉じ込める（他の管理画面へ波及させない）。
    /// </summary>
    /// <remarks>
    /// <b>2 段構え（login CSRF 対策。ADR-0013 決定 4）</b>: GET は Negotiate で認証し
    /// <c>BUILTIN\Administrators</c>（544）判定に合格したら**副作用なしの確認画面**（antiforgery トークン付き
    /// フォーム）を表示するだけに留める。認証セッション Cookie の発行（<c>SignInAsync</c>）は、利用者が
    /// 明示的に確認フォームを送信した POST でのみ行う——Negotiate の透過性ゆえに攻撃者ページが GET を
    /// 強制しても、antiforgery トークンを持たない POST は成立せず Cookie 植え付け（login CSRF/セッション固定）が
    /// 起こらない。
    /// </remarks>
    private static void MapWindowsLogin(IEndpointRouteBuilder endpoints)
    {
        // GET: Negotiate 認証 + 544 判定 → 確認画面（副作用なし）。
        var getEndpoint = endpoints.MapGet("/admin/login/windows", async (
            HttpContext context,
            IAntiforgery antiforgery,
            IAuditRecorder auditRecorder,
            WindowsGroupAuthorizationOptions groups,
            TimeProvider timeProvider) =>
        {
            var user = context.User;

            // SEC-9（ADR-0010 決定 5・委任事項 8）: 既定の 544 判定に加え、設定された管理グループ SID との
            // 交差も管理者と認可する（Admin:Authentication:Windows:AdminGroups）。
            if (!AdminAuthenticationExtensions.IsWindowsAdministrator(user, groups.AdminGroupSids))
            {
                await RecordWindowsAuthorizationDeniedAsync(auditRecorder, context, timeProvider).ConfigureAwait(false);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync(
                    "Windows 認証には成功しましたが、BUILTIN\\Administrators に所属していないため管理 UI へアクセスできません。",
                    context.RequestAborted).ConfigureAwait(false);
                return;
            }

            var tokens = antiforgery.GetAndStoreTokens(context);
            await WriteWindowsLoginConfirmPageAsync(context, user.Identity?.Name ?? string.Empty, tokens.RequestToken ?? string.Empty).ConfigureAwait(false);
        });

        getEndpoint
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = NegotiateDefaults.AuthenticationScheme })
            .WithMetadata(ListenerPortGuardEndpointMetadata.Admin);

        // POST: antiforgery 検証 + Negotiate 再認証 + 544 再判定 → SignInAsync（認証セッション Cookie 発行）。
        var postEndpoint = endpoints.MapPost("/admin/login/windows", async (
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
                context.Response.Redirect("/admin/login?error=csrf");
                return;
            }

            var user = context.User;
            if (!AdminAuthenticationExtensions.IsWindowsAdministrator(user, groups.AdminGroupSids))
            {
                // 確認フォーム送信時点で再度 Negotiate 認証済み。ここで 544 でないのは、GET と POST の間に
                // 権限が変わった等の稀なケース——ADR-0010 決定 5 の混同回避に従い監査のうえ拒否する。
                await RecordWindowsAuthorizationDeniedAsync(auditRecorder, context, timeProvider).ConfigureAwait(false);
                context.Response.Redirect("/admin/login?error=1");
                return;
            }

            var principalName = user.Identity?.Name ?? string.Empty;
            var principal = AdminAuthenticationExtensions.CreateAdminSessionPrincipal(
                AdminAuthenticationExtensions.WindowsAuthMethod, principalName, generationStore.CurrentGeneration);

            var now = timeProvider.GetUtcNow();
            var props = AdminAuthenticationExtensions.BuildSessionSignInProperties(
                now, AdminAuthenticationExtensions.WindowsSessionAbsoluteLifetime, allowRefresh: false);

            await context.SignInAsync(AdminAuthenticationExtensions.AppAuthenticationScheme, principal, props).ConfigureAwait(false);

            // サインイン成功の監査（ADR-0010 決定 6・ADR-0013 決定 3: 「成功」= 認証セッション発行時点）。
            await auditRecorder.RecordAsync(new AuditEvent(
                OccurredAt: now,
                Kind: AuditEventKind.AdminLoginSucceeded,
                RemoteAddress: context.Connection.RemoteIpAddress?.ToString(),
                RemotePort: context.Connection.RemotePort,
                ReachedListenerPort: context.Connection.LocalPort,
                Detail: "scheme=windows",
                AuthenticationScheme: "windows",
                AuthenticatedPrincipal: principalName),
                CancellationToken.None).ConfigureAwait(false);

            context.Response.Redirect("/admin");
        });

        postEndpoint
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = NegotiateDefaults.AuthenticationScheme })
            .WithMetadata(ListenerPortGuardEndpointMetadata.Admin);
    }

    private static async Task RecordWindowsAuthorizationDeniedAsync(
        IAuditRecorder auditRecorder, HttpContext context, TimeProvider timeProvider)
    {
        // Negotiate は成立したが BUILTIN\Administrators ではない（認証成功 ≠ 管理権限。ADR-0010 決定 5）。
        // 事象種別は AdminAuthorizationDenied（3008）——握手失敗（3003）と切り分けて記録する（issue #237）。
        await auditRecorder.RecordAsync(new AuditEvent(
            OccurredAt: timeProvider.GetUtcNow(),
            Kind: AuditEventKind.AdminAuthorizationDenied,
            RemoteAddress: context.Connection.RemoteIpAddress?.ToString(),
            RemotePort: context.Connection.RemotePort,
            AttemptedPath: context.Request.Path,
            ReachedListenerPort: context.Connection.LocalPort,
            Detail: "authenticated-but-not-administrator",
            AuthenticationScheme: "windows",
            AuthenticatedPrincipal: context.User.Identity?.Name),
            context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Windows ログインの確認画面（副作用なし・antiforgery トークン付きフォーム）。</summary>
    private static async Task WriteWindowsLoginConfirmPageAsync(HttpContext context, string principalName, string antiforgeryToken)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        var name = System.Net.WebUtility.HtmlEncode(principalName);
        var token = System.Net.WebUtility.HtmlEncode(antiforgeryToken);
        var html = $$"""
            <!doctype html>
            <html lang="ja">
            <head><meta charset="utf-8"><title>{{Yagura.Web.Components.Common.UiText.AdminLoginTitle}}</title></head>
            <body>
            <p>Windows 認証に成功しました: <strong>{{name}}</strong></p>
            <p>この資格情報で管理 UI にサインインします。</p>
            <form method="post" action="/admin/login/windows">
            <input type="hidden" name="__RequestVerificationToken" value="{{token}}" />
            <button type="submit">サインインを続ける</button>
            </form>
            <p><a href="/admin/login">サインイン画面に戻る</a></p>
            </body>
            </html>
            """;
        await context.Response.WriteAsync(html, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>アプリ独自 ID/パスワード認証のログイン POST。</summary>
    private static void MapAppLogin(IEndpointRouteBuilder endpoints)
    {
        var endpoint = endpoints.MapPost("/admin/login/app", (
            HttpContext context,
            IAntiforgery antiforgery,
            IAppAdminAuthenticator authenticator,
            IAdminSessionGenerationStore generationStore,
            IAuditRecorder auditRecorder,
            TimeProvider timeProvider) =>
            // 列挙耐性・三層防御の応答整形は閲覧ログイン（/login/app）と共有する（AppLoginEndpointHandler）。
            // 管理経路の差分はログインパス・成功時遷移先・成功監査 ID のみ。
            AppLoginEndpointHandler.HandleAsync(
                context, antiforgery, authenticator, generationStore, auditRecorder, timeProvider,
                loginPath: AdminAuthenticationExtensions.LoginPath,
                loginTitle: Yagura.Web.Components.Common.UiText.AdminLoginTitle,
                successRedirect: "/admin",
                successAuditKind: AuditEventKind.AdminLoginSucceeded,
                successDetail: "scheme=app"));

        endpoint.WithMetadata(ListenerPortGuardEndpointMetadata.Admin);
        // 認証未確立の状態で到達する経路のため、YaguraAdminExtensions 側で
        // AdminPolicyName の RequireAuthorization を付与しない例外扱いにする
        // （AdminLoginExemptRoutes 参照）。
    }

    private static void MapLogout(IEndpointRouteBuilder endpoints)
    {
        var endpoint = endpoints.MapPost("/admin/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
            }
            catch (AntiforgeryValidationException)
            {
                context.Response.Redirect("/admin/login");
                return;
            }

            await context.SignOutAsync(AdminAuthenticationExtensions.AppAuthenticationScheme).ConfigureAwait(false);
            context.Response.Redirect("/admin/login");
        });

        endpoint.WithMetadata(ListenerPortGuardEndpointMetadata.Admin);
    }
}
