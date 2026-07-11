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
    }

    /// <summary>
    /// Windows 統合認証のログイン到達点。<see cref="AuthorizeAttribute"/> により
    /// Negotiate スキームでの認証が要求される——未認証で到達すると 401 チャレンジが発火し、
    /// ブラウザが透過的に資格情報を提示する（委任事項 9「選択式」ログイン画面の Windows 経路）。
    /// </summary>
    private static void MapWindowsLogin(IEndpointRouteBuilder endpoints)
    {
        var endpoint = endpoints.MapGet("/admin/login/windows", async (
            HttpContext context,
            IAuditRecorder auditRecorder,
            TimeProvider timeProvider) =>
        {
            var user = context.User;

            if (!AdminAuthenticationExtensions.IsWindowsAdministrator(user))
            {
                // Negotiate 自体は成功した（未認証ならこのハンドラに到達する前に 401
                // チャレンジで止まる——[Authorize] の効果）が、BUILTIN\Administrators では
                // ない。「認証されている」ことと「管理権限を持つ」ことを混同しない
                // （ADR-0010 決定 5 が却下した選択肢 (c)）——監査記録のうえで拒否する。
                // 事象種別は AdminAuthorizationDenied（3006）: 認証は成立しているため、握手失敗
                // （WindowsAuthenticationHandshakeFailed=3003）とは別 Kind で記録し、運用者が Kind だけで
                // 「握手失敗」と「認証成功だが権限不足」を切り分けられるようにする（issue #237）。
                await auditRecorder.RecordAsync(new AuditEvent(
                    OccurredAt: timeProvider.GetUtcNow(),
                    Kind: AuditEventKind.AdminAuthorizationDenied,
                    RemoteAddress: context.Connection.RemoteIpAddress?.ToString(),
                    RemotePort: context.Connection.RemotePort,
                    AttemptedPath: context.Request.Path,
                    ReachedListenerPort: context.Connection.LocalPort,
                    Detail: "authenticated-but-not-administrator",
                    AuthenticationScheme: "windows",
                    AuthenticatedPrincipal: user.Identity?.Name),
                    context.RequestAborted).ConfigureAwait(false);

                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync(
                    "Windows 認証には成功しましたが、BUILTIN\\Administrators に所属していないため管理 UI へアクセスできません。",
                    context.RequestAborted).ConfigureAwait(false);
                return;
            }

            // サインイン成功の監査記録（ADR-0010 決定 6「誰が」欄の実効化の起点。ID 2008）。
            // CancellationToken.None: クライアント切断で監査記録自体を打ち切らない
            // （ForwarderKit ダウンロード等の既存 2000 番台と同じ判断。ADR-0004 決定 7）。
            await auditRecorder.RecordAsync(new AuditEvent(
                OccurredAt: timeProvider.GetUtcNow(),
                Kind: AuditEventKind.AdminLoginSucceeded,
                RemoteAddress: context.Connection.RemoteIpAddress?.ToString(),
                RemotePort: context.Connection.RemotePort,
                ReachedListenerPort: context.Connection.LocalPort,
                Detail: "scheme=windows",
                AuthenticationScheme: "windows",
                AuthenticatedPrincipal: user.Identity?.Name),
                CancellationToken.None).ConfigureAwait(false);

            context.Response.Redirect("/admin");
        });

        endpoint
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = NegotiateDefaults.AuthenticationScheme })
            .WithMetadata(ListenerPortGuardEndpointMetadata.Admin);
    }

    /// <summary>アプリ独自 ID/パスワード認証のログイン POST。</summary>
    private static void MapAppLogin(IEndpointRouteBuilder endpoints)
    {
        var endpoint = endpoints.MapPost("/admin/login/app", async (
            HttpContext context,
            IAntiforgery antiforgery,
            IAppAdminAuthenticator authenticator,
            IAuditRecorder auditRecorder,
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

            var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            var username = form["username"].ToString();
            var password = form["password"].ToString();

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                context.Response.Redirect("/admin/login?error=1");
                return;
            }

            var outcome = await authenticator.TryAuthenticateAsync(username, password, context.RequestAborted).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();

            switch (outcome.Result)
            {
                case AppAuthenticationResult.Success:
                    var identity = new ClaimsIdentity(AdminAuthenticationExtensions.AppAuthenticationScheme);
                    identity.AddClaim(new Claim(ClaimTypes.Name, outcome.Username));
                    var principal = new ClaimsPrincipal(identity);
                    await context.SignInAsync(AdminAuthenticationExtensions.AppAuthenticationScheme, principal).ConfigureAwait(false);

                    // サインイン成功の監査記録（ADR-0010 決定 6「誰が」欄の実効化の起点。ID 2008）。
                    await auditRecorder.RecordAsync(new AuditEvent(
                        OccurredAt: now,
                        Kind: AuditEventKind.AdminLoginSucceeded,
                        RemoteAddress: context.Connection.RemoteIpAddress?.ToString(),
                        RemotePort: context.Connection.RemotePort,
                        ReachedListenerPort: context.Connection.LocalPort,
                        Detail: "scheme=app",
                        AuthenticationScheme: "app",
                        AuthenticatedPrincipal: outcome.Username),
                        CancellationToken.None).ConfigureAwait(false);

                    context.Response.Redirect("/admin");
                    return;

                case AppAuthenticationResult.LockedOutNow:
                    await auditRecorder.RecordAsync(new AuditEvent(
                        OccurredAt: now,
                        Kind: AuditEventKind.AdminAccountLockedOut,
                        RemoteAddress: context.Connection.RemoteIpAddress?.ToString(),
                        RemotePort: context.Connection.RemotePort,
                        AttemptedPath: context.Request.Path,
                        ReachedListenerPort: context.Connection.LocalPort,
                        Detail: $"username={outcome.Username} lockoutUntilUtc={outcome.LockoutUntilUtc:O}"),
                        context.RequestAborted).ConfigureAwait(false);
                    context.Response.Redirect("/admin/login?error=1");
                    return;

                case AppAuthenticationResult.LockedOut:
                case AppAuthenticationResult.InvalidCredentials:
                default:
                    // 失敗理由の種別は監査記録にのみ残し、利用者への応答では区別しない
                    // （ユーザー列挙耐性——ADR-0010 決定 3・security.md §4.3）。
                    await auditRecorder.RecordAsync(new AuditEvent(
                        OccurredAt: now,
                        Kind: AuditEventKind.AppAuthenticationLoginFailed,
                        RemoteAddress: context.Connection.RemoteIpAddress?.ToString(),
                        RemotePort: context.Connection.RemotePort,
                        AttemptedPath: context.Request.Path,
                        ReachedListenerPort: context.Connection.LocalPort,
                        Detail: $"username={outcome.Username} reason={outcome.Result}"),
                        context.RequestAborted).ConfigureAwait(false);
                    context.Response.Redirect("/admin/login?error=1");
                    return;
            }
        });

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
