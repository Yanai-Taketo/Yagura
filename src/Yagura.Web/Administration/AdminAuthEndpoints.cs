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
                await auditRecorder.RecordAsync(new AuditEvent(
                    OccurredAt: timeProvider.GetUtcNow(),
                    Kind: AuditEventKind.WindowsAuthenticationHandshakeFailed,
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

            // 三層防御（ADR-0011 決定 2〜4）の loopback 判定は、認可バイパスの判定
            // （IsUnauthenticatedLoopbackBypassAllowed）と同一の判定点を共有する（決定 4・
            // 委任事項 9・10）。
            var isLoopback = AdminAuthenticationExtensions.IsLoopbackAdminConnection(context);
            var attemptContext = new AdminAuthAttemptContext(context.Connection.RemoteIpAddress, isLoopback);

            var outcome = await authenticator.TryAuthenticateAsync(username, password, attemptContext, context.RequestAborted).ConfigureAwait(false);
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

                case AppAuthenticationResult.Denied:
                    // 応答種別・利用者向け文言は原因（バックオフ待機/IP レート制限/グローバル
                    // トークンバケット）で区別しない（ADR-0011 決定 3・6）。原因の別は監査記録
                    // にのみ残す（決定 9）。
                    await RecordDenialAuditAsync(auditRecorder, context, now, outcome).ConfigureAwait(false);

                    if (outcome.DenialLayer is AdminAuthDenialLayer.IpRateLimit or AdminAuthDenialLayer.GlobalBucket)
                    {
                        // 決定 5.1: レート制限層は待たせず即座に拒否し、有限 Retry-After を返す。
                        // ①②は送信元 IP 単位・プロセス全体の状態のみで判定し、ユーザー名の実在有無に
                        // 依存しない（決定 4）——非実在ユーザー名も実在アカウントも同一 IP からは同一の
                        // 429 + カウントダウンを受ける。列挙シグナルにならないため、この層に限り
                        // 待機表示（決定 6）を出す。
                        await WriteRateLimitedResponseAsync(context, outcome.WaitSeconds ?? 0).ConfigureAwait(false);
                        return;
                    }

                    // バックオフ層（決定 3 の非開示要件・列挙耐性の核心）: **非実在ユーザー名と
                    // バイト単位で同一の応答**（`?error=1`・wait パラメータなし・同一 UI 文言）に
                    // 統一する。実在アカウントがバックオフ待機中であることを、応答種別・Location
                    // ヘッダ・UI 文言のいずれからも観測できないようにする（curl の生ヘッダだけで
                    // 判別できる「計測不要の直接的な告白」経路を塞ぐ——決定 3 が名指しで排除した経路）。
                    //
                    // バックオフの効果は TryAuthenticateAsync 内の Task.Delay による**サーバ側の
                    // 応答遅延（レイテンシ）としてのみ**現れる。これは決定 3 が Phase 1 で明示的に
                    // 受け入れたタイミング非対称（「実在名は account-keyed バックオフ遅延を、非実在名は
                    // IP レート制限・グローバルバケット遅延のみを負う——遅延時間は完全には一致しない」）
                    // の範囲であり、計測して初めて分かる推論に留まる。
                    //
                    // カウントダウン表示（決定 6）を出さない点は決定 6 の当初意図から外れるが、
                    // 決定 3 は「決定 6 の文言は本決定 3 の非開示要件に従う」と明示的に決定 3 を優先
                    // させている。アクセス集中（IP/グローバル層）のカウントダウンは上の 429 経路が
                    // 引き続き提供する。監査記録（3004 reason=Backoff・cap 到達時 3006）は
                    // RecordDenialAuditAsync が層の別をサーバ側にのみ残す。
                    context.Response.Redirect("/admin/login?error=1");
                    return;

                case AppAuthenticationResult.InvalidCredentials:
                default:
                    // 失敗理由の種別は監査記録にのみ残し、利用者への応答では区別しない
                    // （ユーザー列挙耐性——ADR-0010 決定 3・security.md §4.3）。上のバックオフ層と
                    // 完全に同一の Location（`/admin/login?error=1`）を返すことで、反復試行でも
                    // 実在/非実在が応答形状から区別できないことを保証する。
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

    /// <summary>
    /// <see cref="AppAuthenticationResult.Denied"/> の監査記録（ADR-0011 決定 9）。
    /// バックオフ層は通常の失敗ログイン（3004）に加え、cap 到達時のみ 3006 を追加で記録する。
    /// IP レート制限/グローバルトークンバケット層は 3007 に一本化し、<c>Detail</c> で層を区別する
    /// （利用者応答では区別しない——決定 3）。
    /// </summary>
    private static async Task RecordDenialAuditAsync(
        IAuditRecorder auditRecorder, HttpContext context, DateTimeOffset now, AppAuthenticationOutcome outcome)
    {
        var remoteAddress = context.Connection.RemoteIpAddress?.ToString();
        var remotePort = context.Connection.RemotePort;
        var path = context.Request.Path;
        var localPort = context.Connection.LocalPort;

        if (outcome.DenialLayer == AdminAuthDenialLayer.Backoff)
        {
            await auditRecorder.RecordAsync(new AuditEvent(
                OccurredAt: now,
                Kind: AuditEventKind.AppAuthenticationLoginFailed,
                RemoteAddress: remoteAddress,
                RemotePort: remotePort,
                AttemptedPath: path,
                ReachedListenerPort: localPort,
                Detail: $"username={outcome.Username} reason=Backoff waitSeconds={outcome.WaitSeconds}"),
                context.RequestAborted).ConfigureAwait(false);

            if (outcome.BackoffCapReached)
            {
                await auditRecorder.RecordAsync(new AuditEvent(
                    OccurredAt: now,
                    Kind: AuditEventKind.AdminAuthBackoffCapReached,
                    RemoteAddress: remoteAddress,
                    RemotePort: remotePort,
                    AttemptedPath: path,
                    ReachedListenerPort: localPort,
                    Detail: $"username={outcome.Username} waitSeconds={outcome.WaitSeconds}"),
                    context.RequestAborted).ConfigureAwait(false);
            }

            return;
        }

        var layerDetail = outcome.DenialLayer == AdminAuthDenialLayer.IpRateLimit
            ? "layer=ip-rate-limit"
            : "layer=global-bucket (プロセス全体の事象)";

        await auditRecorder.RecordAsync(new AuditEvent(
            OccurredAt: now,
            Kind: AuditEventKind.AdminAuthRateLimited,
            RemoteAddress: remoteAddress,
            RemotePort: remotePort,
            AttemptedPath: path,
            ReachedListenerPort: localPort,
            Detail: $"{layerDetail} retryAfterSeconds={outcome.WaitSeconds}"),
            context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// IP レート制限・グローバルトークンバケット層の拒否応答（ADR-0011 決定 5.1）: 429 +
    /// <c>Retry-After</c> を返す（サーバ側で待たせない）。素の HTML フォーム POST から到達するため、
    /// 生の 429 だけを見せるのではなく、ログイン画面と同じ統一の待機文言（決定 3・6。原因を明かさない）
    /// を表示したうえで、カウントダウン後にログイン画面へ自動的に戻す最小限の自己完結ページを返す。
    /// </summary>
    private static async Task WriteRateLimitedResponseAsync(HttpContext context, int retryAfterSeconds)
    {
        var seconds = Math.Max(0, retryAfterSeconds);

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers["Retry-After"] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.ContentType = "text/html; charset=utf-8";

        var message = string.Format(System.Globalization.CultureInfo.InvariantCulture, Yagura.Web.Components.Common.UiText.AdminLoginWait, seconds);

        var html = $$"""
            <!doctype html>
            <html lang="ja">
            <head><meta charset="utf-8"><title>{{Yagura.Web.Components.Common.UiText.AdminLoginTitle}}</title></head>
            <body>
            <p id="yagura-wait-message">{{message}}</p>
            <p><a href="/admin/login">サインイン画面に戻る</a></p>
            <script>
            (function () {
                var remaining = {{seconds}};
                var el = document.getElementById('yagura-wait-message');
                var timer = setInterval(function () {
                    remaining -= 1;
                    if (remaining <= 0) {
                        clearInterval(timer);
                        window.location.href = '/admin/login?error=1';
                        return;
                    }
                    if (el) {
                        el.textContent = 'しばらくお待ちください。あと ' + remaining + ' 秒で再試行できます。';
                    }
                }, 1000);
            })();
            </script>
            </body>
            </html>
            """;

        await context.Response.WriteAsync(html, context.RequestAborted).ConfigureAwait(false);
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
