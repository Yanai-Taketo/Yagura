using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;

namespace Yagura.Web.Administration;

/// <summary>
/// アプリ独自 ID/パスワードのログイン POST の共通処理（ADR-0011 三層防御）。管理ログイン（<c>/admin/login/app</c>）と閲覧ログイン
/// （<c>/login/app</c>。ADR-0010 Phase 4——アプリ独自アカウントは管理役割のみのため閲覧リスナ経由でも
/// 管理セッションを発行し、管理 ⊇ 閲覧で閲覧できる）で共有する。
/// </summary>
/// <remarks>
/// <b>列挙耐性・三層防御の応答整形は両経路でバイト単位一致させる必要がある</b>（ADR-0011 決定 3）ため、
/// 分岐（バックオフ/IP レート制限/グローバルバケット/資格情報誤り）の応答統一ロジックを本クラスに
/// 一本化する——管理・閲覧で別実装にして片方だけ整形がずれる事故を防ぐ。経路ごとに変わるのは
/// <c>loginPath</c>（CSRF/失敗時のリダイレクト先とレート制限ページの戻り先）・<c>successRedirect</c>
/// （成功時遷移先）・成功監査の <c>successAuditKind</c>/<c>successDetail</c> のみ。
/// </remarks>
internal static class AppLoginEndpointHandler
{
    /// <summary>
    /// アプリ独自 ID/パスワードのログイン POST を処理する。成功時は単一 Cookie（管理セッション）を発行して
    /// <paramref name="successRedirect"/> へ、失敗時は原因に依らず統一の <c>{loginPath}?error=1</c> へ誘導する。
    /// </summary>
    public static async Task HandleAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IAppAdminAuthenticator authenticator,
        IAdminSessionGenerationStore generationStore,
        IAuditRecorder auditRecorder,
        TimeProvider timeProvider,
        string loginPath,
        string loginTitle,
        string successRedirect,
        AuditEventKind successAuditKind,
        string successDetail)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            context.Response.Redirect($"{loginPath}?error=csrf");
            return;
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
        var username = form["username"].ToString();
        var password = form["password"].ToString();

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            context.Response.Redirect($"{loginPath}?error=1");
            return;
        }

        // 三層防御（ADR-0011 決定 4）の loopback 判定は、認可バイパスの判定と同一の判定点を共有する。
        // 閲覧リスナ経由（:8514。管理 loopback ポートではない）は loopback 免除の対象外＝
        // IP レート制限・グローバルバケットが効く（LAN 公開面への総当たり防御。適切）。
        var isLoopback = AdminAuthenticationExtensions.IsLoopbackAdminConnection(context);
        var attemptContext = new AdminAuthAttemptContext(context.Connection.RemoteIpAddress, isLoopback);

        var outcome = await authenticator.TryAuthenticateAsync(username, password, attemptContext, context.RequestAborted).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();

        switch (outcome.Result)
        {
            case AppAuthenticationResult.Success:
                // 認証成立後は方式に依らない単一の認証セッション Cookie を発行する（ADR-0013 決定 1）。
                // アプリ独自アカウントは常に「管理」役割——管理セッションクレームを焼き込む。
                var principal = AdminAuthenticationExtensions.CreateAdminSessionPrincipal(
                    AdminAuthenticationExtensions.AppAuthMethod, outcome.Username, generationStore.CurrentGeneration);
                var appProps = AdminAuthenticationExtensions.BuildSessionSignInProperties(
                    now, AdminAuthenticationExtensions.AppSessionAbsoluteLifetime, allowRefresh: true);
                await context.SignInAsync(AdminAuthenticationExtensions.AppAuthenticationScheme, principal, appProps).ConfigureAwait(false);

                await auditRecorder.RecordAsync(new AuditEvent(
                    OccurredAt: now,
                    Kind: successAuditKind,
                    RemoteAddress: context.Connection.RemoteIpAddress?.ToString(),
                    RemotePort: context.Connection.RemotePort,
                    ReachedListenerPort: context.Connection.LocalPort,
                    Detail: successDetail,
                    AuthenticationScheme: "app",
                    AuthenticatedPrincipal: outcome.Username),
                    CancellationToken.None).ConfigureAwait(false);

                context.Response.Redirect(successRedirect);
                return;

            case AppAuthenticationResult.Denied:
                // 応答種別・利用者向け文言は原因（バックオフ待機/IP レート制限/グローバルバケット）で
                // 区別しない（ADR-0011 決定 3）。原因の別は監査記録にのみ残す。
                await RecordDenialAuditAsync(auditRecorder, context, now, outcome).ConfigureAwait(false);

                if (outcome.DenialLayer is AdminAuthDenialLayer.IpRateLimit or AdminAuthDenialLayer.GlobalBucket)
                {
                    // 決定 5.1: レート制限層は待たせず即座に拒否し、有限 Retry-After を返す。送信元 IP 単位・
                    // プロセス全体の状態のみで判定し、ユーザー名の実在有無に依存しないため列挙
                    // シグナルにならない——この層に限り待機表示を出す。
                    await WriteRateLimitedResponseAsync(context, outcome.WaitSeconds ?? 0, loginPath, loginTitle).ConfigureAwait(false);
                    return;
                }

                // バックオフ層（決定 3 の非開示要件・列挙耐性の核心）: 非実在ユーザー名とバイト単位で同一の
                // 応答（`?error=1`・wait パラメータなし・同一 UI 文言）に統一する。
                context.Response.Redirect($"{loginPath}?error=1");
                return;

            case AppAuthenticationResult.StoreUnavailable:
                // ADR-0023 決定 1: 保存先が到達不能でアプリ独自認証が一時的に使えない。
                // **InvalidCredentials と同じ扱いにしない**——資格情報の誤りと読み違えた利用者が
                // パスワードリセットを試み、それも失敗して混乱するため。
                // 監査も 3004（ログイン失敗）へ相乗りさせない（資格情報の検証に到達していない事象を
                // 混ぜると意味が曖昧になる）——専用の 3015 に記録する。
                await auditRecorder.RecordAsync(new AuditEvent(
                    OccurredAt: now,
                    Kind: AuditEventKind.AdminAccountStoreUnavailableRejected,
                    RemoteAddress: context.Connection.RemoteIpAddress?.ToString(),
                    RemotePort: context.Connection.RemotePort,
                    AttemptedPath: context.Request.Path,
                    ReachedListenerPort: context.Connection.LocalPort,
                    // 試行されたユーザー名は残す（誰が困ったかの追跡）。保存先名・例外文字列は
                    // 応答にも監査 Detail にも出さない。
                    Detail: $"username={outcome.Username}"),
                    CancellationToken.None).ConfigureAwait(false);

                context.Response.Redirect($"{loginPath}?error=store-unavailable");
                return;

            case AppAuthenticationResult.InvalidCredentials:
                // 失敗理由の種別は監査記録にのみ残し、利用者への応答では区別しない（ユーザー列挙耐性——
                // ADR-0010 決定 3）。バックオフ層と完全に同一の Location を返す。
                await auditRecorder.RecordAsync(new AuditEvent(
                    OccurredAt: now,
                    Kind: AuditEventKind.AppAuthenticationLoginFailed,
                    RemoteAddress: context.Connection.RemoteIpAddress?.ToString(),
                    RemotePort: context.Connection.RemotePort,
                    AttemptedPath: context.Request.Path,
                    ReachedListenerPort: context.Connection.LocalPort,
                    Detail: $"username={outcome.Username} reason={outcome.Result}"),
                    context.RequestAborted).ConfigureAwait(false);
                context.Response.Redirect($"{loginPath}?error=1");
                return;

            // default 節は置かない（ADR-0023 決定 1 の実装時に判明した落とし穴）: `case InvalidCredentials: default:`
            // のように同居させると、AppAuthenticationResult へ新しい値を足しても**コンパイルは通り、
            // 黙って 3004 として記録される**——網羅を外すことで、将来の追加はここでコンパイルエラーになる。
        }

        throw new InvalidOperationException(
            $"未処理の {nameof(AppAuthenticationResult)}: {outcome.Result}（列挙値の追加時は本 switch に " +
            "明示の case を足すこと——監査 ID の相乗りを防ぐため default 節は置かない）。");
    }

    /// <summary>
    /// <see cref="AppAuthenticationResult.Denied"/> の監査記録（ADR-0011 決定 9）。バックオフ層は通常の
    /// 失敗ログイン（3004）に加え cap 到達時のみ 3006 を、IP レート制限/グローバルバケット層は 3007 を記録する
    /// （利用者応答では区別しない）。
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
    /// IP レート制限・グローバルトークンバケット層の拒否応答（ADR-0011 決定 5.1）: 429 + <c>Retry-After</c> を
    /// 返し、ログイン画面と同じ統一の待機文言（原因を明かさない）を表示してカウントダウン後に
    /// <paramref name="loginPath"/> へ自動的に戻す最小限の自己完結ページを返す。
    /// </summary>
    private static async Task WriteRateLimitedResponseAsync(HttpContext context, int retryAfterSeconds, string loginPath, string loginTitle)
    {
        var seconds = Math.Max(0, retryAfterSeconds);

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers["Retry-After"] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.ContentType = "text/html; charset=utf-8";

        var message = string.Format(System.Globalization.CultureInfo.InvariantCulture, Yagura.Web.Components.Common.UiText.AdminLoginWait, seconds);
        var loginHref = System.Net.WebUtility.HtmlEncode(loginPath);
        var title = System.Net.WebUtility.HtmlEncode(loginTitle);

        var html = $$"""
            <!doctype html>
            <html lang="ja">
            <head><meta charset="utf-8"><title>{{title}}</title></head>
            <body>
            <p id="yagura-wait-message">{{message}}</p>
            <p><a href="{{loginHref}}">サインイン画面に戻る</a></p>
            <script>
            (function () {
                var remaining = {{seconds}};
                var el = document.getElementById('yagura-wait-message');
                var loginUrl = {{System.Text.Json.JsonSerializer.Serialize(loginPath + "?error=1")}};
                var timer = setInterval(function () {
                    remaining -= 1;
                    if (remaining <= 0) {
                        clearInterval(timer);
                        window.location.href = loginUrl;
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
}
