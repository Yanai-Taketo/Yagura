using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yagura.Abstractions.Auditing;

namespace Yagura.Web.Administration;

/// <summary>
/// 管理 UI 認証（ADR-0010 Phase 1）の DI/パイプライン組み込み。
/// </summary>
/// <remarks>
/// <para>
/// <b>共存は複数の ASP.NET Core 認証スキームとして構成する</b>（ADR-0010 決定 3）:
/// Windows 統合認証は <see cref="NegotiateDefaults.AuthenticationScheme"/>（<c>"Negotiate"</c>）、
/// アプリ独自認証は <see cref="AppAuthenticationScheme"/>（Cookie）の 2 スキームを並立させる。
/// セッション・トークンは方式間で共有しない——Negotiate は接続単位の透過認証（Cookie を発行しない）、
/// アプリ独自認証は専用 Cookie（<see cref="AppAuthenticationScheme"/>）のみに依存する。
/// </para>
/// <para>
/// <b>ロックアウトは方式ごとに独立</b>: Windows 統合認証は OS/AD のロックアウトに依拠し、本製品は
/// 一切関与しない。アプリ独自認証のロックアウトは <c>IAppAdminAuthenticator</c>
/// （<c>Yagura.Host.Administration.AdminAuthentication.AppAdminAuthenticationService</c>）が
/// 独立に管理する——一方の失敗がもう一方の可否に影響しない（決定 3）。
/// </para>
/// <para>
/// <b>認可（誰を管理者とするか。決定 5）</b>: Windows 統合認証は well-known SID
/// <c>S-1-5-32-544</c>（<c>BUILTIN\Administrators</c>）のグループ SID クレーム
/// （<see cref="ClaimTypes.GroupSid"/>）を判定に使う——ADR-0010 検証 2 で確認済みの公式パターン
/// （<c>RequireClaim("...claims/groupsid", "S-1-5-32-544")</c>）をそのまま採用する。AD グループへの
/// マッピング拡張（SEC-9）は AD 実環境（lab）での検証を要するため Phase 1 のスコープ外とし、
/// 既定（ローカル Administrators のみ）のまま実装する（ADR-0010 委任事項 8 の未消化分として
/// security.md に申し送る）。アプリ独自認証で作成したアカウントは常に「管理」役割のみを持つ
/// （決定 5）——<see cref="AppAuthenticationScheme"/> で認証済みであること自体が管理権限の根拠。
/// </para>
/// <para>
/// <b>ログイン画面の振る舞い（委任事項 9）</b>: 選択式とする——<c>/admin/login</c> に「Windows で
/// サインイン」（<c>/admin/login/windows</c> への遷移。到達時点で Negotiate の 401 チャレンジが
/// 自動発火し、ブラウザが透過的に資格情報を提示する）と、アプリ独自 ID/パスワードの入力フォーム
/// （<c>/admin/login/app</c> への POST）を併記する。自動試行（両方式を暗黙に順に試す）は、
/// 「どちらの資格情報を今使おうとしているか」を利用者が把握しにくくする（特に Windows 統合認証は
/// ブラウザのダイアログ/透過認証という別チャネルの UX を持つ）ため採らない。
/// </para>
/// <para>
/// <b>Kerberos-only モード（委任事項 12 のライブ検証結果）</b>: <c>NegotiateOptions</c> 自体には
/// NTLM を無効化する組み込みオプションは存在しない（dotnet/aspnetcore の
/// <c>NegotiateOptions.cs</c>/<c>NegotiateHandler.cs</c> を確認。確認日 2026-07-10——推測ではなく
/// ソース確認済み）。<see cref="NegotiateHandler"/> 内部では交渉されたプロトコルが
/// <c>NegotiateState.Protocol</c>（<c>"NTLM"</c>/<c>"Kerberos"</c>）として保持されるが公開 API では
/// 露出しない。そのため Phase 1 は、ADR-0010 検証 1 が既に確認した「<c>Authorization: Negotiate
/// &lt;Base64&gt;</c> の Base64 デコード結果の先頭バイトで Kerberos/NTLM を判別できる」手法
/// （NTLM トークンは ASCII 署名 <c>"NTLMSSP\0"</c> で始まる——NTLM 公開仕様の既知の事実）を、
/// Negotiate ハンドラの<b>手前</b>のミドルウェア（<see cref="KerberosOnlyFilterMiddleware"/>）で
/// 適用し、NTLM トークンを検出した場合はハンドラに渡さず 403 で拒否する（OS ポリシー
/// <c>LmCompatibilityLevel</c> への委譲は、Yagura が管理しないマシン全体設定を変更することになり
/// 採らない——アプリ層で完結させる）。
/// </para>
/// </remarks>
public static class AdminAuthenticationExtensions
{
    /// <summary>アプリ独自 ID/パスワード認証の Cookie 認証スキーム名。</summary>
    public const string AppAuthenticationScheme = "YaguraAppAuth";

    /// <summary>管理 UI 到達に必要な認可ポリシー名（Windows 管理者 SID または AppAuth 認証済み）。</summary>
    public const string AdminPolicyName = "YaguraAdminAccess";

    /// <summary><c>BUILTIN\Administrators</c> の well-known SID（ADR-0010 決定 5・検証 2）。</summary>
    public const string BuiltinAdministratorsSid = "S-1-5-32-544";

    /// <summary>
    /// ログイン画面のパス。未認証で到達できなければならない唯一の管理画面
    /// （<c>AdminScreenLayout</c>・<c>YaguraAdminExtensions</c> の認可除外対象、および
    /// Cookie 認証の <c>LoginPath</c> の 3 箇所で同じ値を参照する——単一の正とする）。
    /// </summary>
    public const string LoginPath = "/admin/login";

    /// <summary>
    /// 認証スキーム・認可ポリシーを DI へ登録する。<paramref name="windowsAuthEnabled"/>/
    /// <paramref name="appAuthEnabled"/> がいずれも無効でも <c>AddAuthentication</c>/
    /// <c>AddAuthorization</c> 自体は常に呼ぶ（ミドルウェアの配線を一様にする——無効なスキームは
    /// 単に登録されないだけで、認証パイプライン自体が有害にはならない）。
    /// </summary>
    public static IServiceCollection AddYaguraAdminAuthentication(
        this IServiceCollection services,
        bool windowsAuthEnabled,
        bool kerberosOnly,
        bool appAuthEnabled)
    {
        ArgumentNullException.ThrowIfNull(services);

        var authBuilder = services.AddAuthentication(options =>
        {
            // 既定スキームは AppAuth（Cookie）——通常の要求は Cookie の有無で認証状態を判定する。
            // Negotiate は個別エンドポイント（/admin/login/windows）で明示的にスキーム指定して
            // チャレンジする（決定 3「選択式」の実装。委任事項 9）。
            options.DefaultAuthenticateScheme = AppAuthenticationScheme;
            options.DefaultSignInScheme = AppAuthenticationScheme;
        });

        if (windowsAuthEnabled)
        {
            authBuilder.AddNegotiate(options =>
            {
                if (kerberosOnly)
                {
                    // NTLM トークン自体は KerberosOnlyFilterMiddleware がハンドラへ渡す前に
                    // 遮断するため、ハンドラ内部の資格情報永続化設定は NTLM 経路を実質使わない。
                    // それでも「NTLM 資格情報を保持しない」を明示しておく（多層防御）。
                    options.PersistNtlmCredentials = false;
                }

                options.Events = new NegotiateEvents
                {
                    OnAuthenticationFailed = context =>
                    {
                        // Negotiate ハンドシェイクのプロトコルレベル失敗（トークン不正・SPN 不一致等）
                        // の監査記録（ADR-0010 決定 6。イベント ID 3003）。
                        var auditRecorder = context.HttpContext.RequestServices.GetService<IAuditRecorder>();
                        var timeProvider = context.HttpContext.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;

                        if (auditRecorder is not null)
                        {
                            _ = auditRecorder.RecordAsync(new AuditEvent(
                                OccurredAt: timeProvider.GetUtcNow(),
                                Kind: AuditEventKind.WindowsAuthenticationHandshakeFailed,
                                RemoteAddress: context.HttpContext.Connection.RemoteIpAddress?.ToString(),
                                RemotePort: context.HttpContext.Connection.RemotePort,
                                AttemptedPath: context.HttpContext.Request.Path,
                                ReachedListenerPort: context.HttpContext.Connection.LocalPort,
                                Detail: $"handshake-failed: {context.Exception.GetType().Name}"));
                        }

                        return Task.CompletedTask;
                    },
                };
            });
        }

        if (appAuthEnabled)
        {
            authBuilder.AddCookie(AppAuthenticationScheme, options =>
            {
                options.Cookie.Name = "Yagura.AdminAuth";
                // loopback/管理リスナのみが対象であり HTTPS は Phase 1 のスコープ外
                // （ADR-0010 決定 1・4。リモートバインドは Phase 2）——SameSite=Strict のみを課す。
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.HttpOnly = true;
                options.LoginPath = LoginPath;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                // API 的な POST エンドポイント（/admin/login/app）はリダイレクトではなく
                // ステータスコードで応答したいため、既定のリダイレクト挙動を上書きしない
                // （LoginPath への 302 のままでよい——通常のブラウザ遷移を想定）。
            });
        }

        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicyName, policy => policy.RequireAssertion(context =>
                IsWindowsAdministrator(context.User) || IsAppAuthenticated(context.User)));

        return services;
    }

    /// <summary>
    /// 指定した <see cref="ClaimsPrincipal"/> が Windows 統合認証で <c>BUILTIN\Administrators</c>
    /// として認証されているかどうか（ADR-0010 決定 5・検証 2 の SID クレーム判定）。
    /// </summary>
    public static bool IsWindowsAdministrator(ClaimsPrincipal user) =>
        string.Equals(user.Identity?.AuthenticationType, NegotiateDefaults.AuthenticationScheme, StringComparison.Ordinal) &&
        (user.Identity?.IsAuthenticated ?? false) &&
        user.HasClaim(ClaimTypes.GroupSid, BuiltinAdministratorsSid);

    /// <summary>
    /// 指定した <see cref="ClaimsPrincipal"/> がアプリ独自認証で認証済みかどうか（決定 5:
    /// アプリ独自認証アカウントは常に「管理」役割のみを持つため、認証済みであること自体が
    /// 管理権限の根拠になる）。
    /// </summary>
    public static bool IsAppAuthenticated(ClaimsPrincipal user) =>
        string.Equals(user.Identity?.AuthenticationType, AppAuthenticationScheme, StringComparison.Ordinal) &&
        (user.Identity?.IsAuthenticated ?? false);

    /// <summary>
    /// 認証パイプラインをアプリケーションビルダーへ組み込む
    /// （<c>UseRouting</c> 後・エンドポイント実行前。<c>UseYaguraListenerPortGuard</c> と同じ順序制約）。
    /// </summary>
    public static IApplicationBuilder UseYaguraAdminAuthentication(this IApplicationBuilder app, bool kerberosOnly)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (kerberosOnly)
        {
            // Negotiate ハンドラより前段で NTLM トークンを遮断する（クラスの remarks 参照）。
            app.UseMiddleware<KerberosOnlyFilterMiddleware>();
        }

        app.UseAuthentication();
        app.UseAuthorization();

        return app;
    }
}
