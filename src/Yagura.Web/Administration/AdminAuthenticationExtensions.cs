using System.Security.Claims;
using System.Security.Principal;
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
                // SecurePolicy は既定値（SameAsRequest）のまま変更しない——意図的な判断
                // （ADR-0010 Phase 2）。管理リスナの loopback ポートは平文 HTTP のままであり
                // （決定 4。loopback の HTTPS 化は引き続きスコープ外）、リモートバインドは
                // HTTPS 必須（fail-closed）。SameAsRequest なら、リモート HTTPS 経由で発行された
                // Cookie には Secure 属性が付き、loopback HTTP 経由の認証（RequireForLoopback=true
                // 時）は localhost 限定トラフィックでネットワークを経由しないため平文のままで
                // 成立する。ここで SecurePolicy=Always に固定すると loopback HTTP 経由の認証済み
                // 管理操作が機能しなくなるため、既定値を維持する。
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
                IsWindowsAdministrator(context.User) ||
                IsAppAuthenticated(context.User) ||
                IsUnauthenticatedLoopbackBypassAllowed(context)));

        return services;
    }

    /// <summary>
    /// 指定した <see cref="ClaimsPrincipal"/> が Windows 統合認証で <c>BUILTIN\Administrators</c>
    /// として認証されているかどうか（ADR-0010 決定 5・検証 2 の SID クレーム判定）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>「Windows 統合認証で確立された identity か」は <see cref="WindowsIdentity"/> 型で判定する</b>
    /// （認証スキーム/パッケージ名の文字列一致では判定しない）。SSPI が Kerberos を交渉した場合、生成される
    /// <see cref="WindowsIdentity"/> の <c>AuthenticationType</c> は <c>"Kerberos"</c>（NTLM の場合は
    /// <c>"NTLM"</c>）になり、認証スキーム名 <c>"Negotiate"</c>
    /// （<see cref="NegotiateDefaults.AuthenticationScheme"/>）とは一致しない。スキーム名の文字列一致で
    /// 判定すると Kerberos ログオンの管理者を取りこぼす（issue #235。Kerberos-only モードでは全 Windows 管理者が
    /// 該当し、事実上ロックアウトされていた——実機診断で <c>AuthenticationType='Kerberos'</c> かつ 544 クレーム
    /// 保持でも判定 false になることを確認）。<see cref="WindowsIdentity"/> 型そのものが「Windows 統合認証で
    /// 確立された identity」の確実な指標であり（アプリ独自認証は <see cref="ClaimsIdentity"/> を用いる。
    /// <see cref="IsAppAuthenticated"/> 参照）、Kerberos/NTLM/Negotiate のパッケージ名文字列に依存しない。
    /// </para>
    /// <para>
    /// <b>不変条件</b>: 本判定は「<see cref="WindowsIdentity"/> を principal へ載せるのは Negotiate ハンドラのみ」
    /// という前提に依る。将来別の認証スキームが <see cref="WindowsIdentity"/> を注入すると管理者認可が黙って
    /// 広がるため、そのような拡張時は本判定の見直しを要する。型・クレームとも
    /// <see cref="ClaimsPrincipal.Identities"/> 全体を走査して観点を揃える（primary identity のみを見る非対称を
    /// 避ける——将来 identity を追加する改修での再発を防ぐ）。<c>IsAuthenticated</c> の併記は匿名/ゲストの
    /// <see cref="WindowsIdentity"/>（<see cref="WindowsIdentity.GetAnonymous"/> 相当）を除外するため。
    /// </para>
    /// <para>
    /// <b>射程の限界（issue #235）</b>: 本判定が通すのは「<c>S-1-5-32-544</c>（<c>BUILTIN\Administrators</c>）を
    /// 保持する Windows 主体」に限る。AD グループマッピング（<c>security.md</c> §3・SEC-9 未実装）で「管理」役割を
    /// 得たローカル Administrators 非所属の AD ユーザーや、階層管理で Domain Admins をローカル Administrators から
    /// 外した構成では、正規の管理者でも本判定を通らない。「Kerberos ロックアウト解消」＝「Windows 管理者認可が
    /// 一般に正しくなった」ではない。SEC-9 着手時に判定範囲（および S4U 要否）を再評価する。
    /// </para>
    /// </remarks>
    public static bool IsWindowsAdministrator(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        // 認可の SID モデルは Windows 固有（非 Windows では管理者と判定しない＝安全側。CA1416 ガードも兼ねる）。
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        // 型・クレームとも全 identity を走査して観点を揃える（primary identity のみを見る非対称を避ける）。
        // 認証済み WindowsIdentity が 1 つでもあり、かつ 544 クレームを持てば管理者。
        foreach (var identity in user.Identities)
        {
            if (identity is WindowsIdentity { IsAuthenticated: true })
            {
                return HasBuiltinAdministratorsSid(user);
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="user"/> が <c>BUILTIN\Administrators</c>（<see cref="BuiltinAdministratorsSid"/>）の
    /// グループ SID クレームを持つか（ADR-0010 決定 5・検証 2 の SID 判定）。型に依存しない純粋関数として
    /// 切り出し、<see cref="ClaimsPrincipal"/> だけで（AD 実環境なしに）単体テスト可能にする（issue #235。
    /// <see cref="WindowsIdentity"/> 型ゲートの正パスは実 Windows トークンを要するため lab 統合検証に委ねる）。
    /// </summary>
    internal static bool HasBuiltinAdministratorsSid(ClaimsPrincipal user) =>
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
    /// 未認証のまま <see cref="AdminPolicyName"/> を通す例外条件（ADR-0010 決定 1）:
    /// 現在の接続が管理リスナの<b>loopback 束縛ポート</b>（<c>Admin:HttpPort</c>）経由であり、
    /// かつ loopback 認証 opt-in（<c>Admin:Authentication:RequireForLoopback</c>）が無効な場合。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>なぜここで判定するか（HTTP エンドポイント向け。circuit 向けは <c>AdminScreenAccessPolicy</c>
    /// が同じ判定を独立に行う）</b>: <c>RequireAssertion</c> に渡る
    /// <see cref="AuthorizationHandlerContext.Resource"/> は、
    /// <c>AuthorizationMiddleware</c> の既定動作（<c>SuppressUseHttpContextAsAuthorizationResource</c>
    /// が既定 <see langword="false"/>）により現在の <see cref="HttpContext"/> そのものである
    /// （dotnet/aspnetcore の <c>AuthorizationMiddleware.InvokeAsync</c> ソース確認。確認日
    /// 2026-07-10）——追加の <c>IHttpContextAccessor</c> 注入なしに接続の実ローカルポートへ
    /// 到達できる。
    /// </para>
    /// <para>
    /// <b>ADR-0010 Phase 2 決定 1 の実装</b>: 「既定は現状（loopback 無認証）を維持する。
    /// リモート経由の管理操作は常に認証必須」——<c>Admin:Authentication:RequireForLoopback</c> は
    /// その名のとおり loopback 面にのみ作用し、リモート HTTPS ポート（別ポート。
    /// ADR-0010 Phase 2）経由の接続はこの例外条件の対象外（<c>httpContext.Connection.LocalPort</c>
    /// が loopback ポートと一致しない）ため、常にフルの認可判定（Windows 管理者 or アプリ認証済み）
    /// を要求する。
    /// </para>
    /// </remarks>
    private static bool IsUnauthenticatedLoopbackBypassAllowed(AuthorizationHandlerContext context)
    {
        if (context.Resource is not HttpContext httpContext)
        {
            // Resource が HttpContext として取得できない場合は fail-closed
            // （configuration.md §1 の縮小側原則と同じ向き——判定不能を許可側へ倒さない）。
            return false;
        }

        var runtimeOptions = httpContext.RequestServices.GetService<AdminAuthenticationRuntimeOptions>();
        var adminListenerPort = httpContext.RequestServices.GetService<YaguraAdminListenerPort>();

        if (runtimeOptions is null || adminListenerPort is null)
        {
            return false;
        }

        return !runtimeOptions.RequireAuthentication &&
            httpContext.Connection.LocalPort == adminListenerPort.Port;
    }

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
