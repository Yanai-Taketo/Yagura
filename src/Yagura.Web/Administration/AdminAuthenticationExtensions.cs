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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Yagura.Abstractions.Administration;
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
    /// <summary>
    /// 認証成立後の単一認証セッション Cookie のスキーム名（ADR-0013 決定 1）。
    /// Windows 統合認証・アプリ独自認証のいずれで認証しても、成立後はこの単一 Cookie に統一する
    /// （方式の区別は <see cref="AuthMethodClaimType"/> クレームで保持する——スキーム名は方式を含意しない）。
    /// </summary>
    /// <remarks>
    /// スキーム名の文字列値は歴史的経緯から <c>"YaguraAppAuth"</c> のまま据え置く——値の変更は全既存
    /// Cookie の再ログインを要し churn が大きいわりに内部識別子であり利用者に露出しない。中立名
    /// （<c>YaguraAdminAuth</c> 等）への改称は ADR-0013 委任事項 1 の後続作業として先送りする
    /// （監査の方式区別はスキーム名でなく <see cref="AuthMethodClaimType"/> クレームで担保するため、
    /// 名称据え置きでも決定 5 の分離は成立する）。
    /// </remarks>
    public const string AppAuthenticationScheme = "YaguraAppAuth";

    /// <summary>
    /// 認証セッションの認証方式（<c>"windows"</c>/<c>"app"</c>）を保持するクレーム型（ADR-0013 決定 5）。
    /// 監査「誰が」欄の方式区別（decision 6）はこのクレームから導出し、Cookie スキーム名には依存しない。
    /// </summary>
    public const string AuthMethodClaimType = "yagura:auth_method";

    /// <summary>
    /// 「管理セッションである」ことを示す標識クレーム型（ADR-0013 決定 5）。認可（<see cref="AdminPolicyName"/>）は
    /// このクレームの存在を正規の判定根拠にする——欠落時は fail-closed（管理者として認可しない）。
    /// </summary>
    public const string AdminSessionClaimType = "yagura:admin_session";

    /// <summary>
    /// 「閲覧セッションである」ことを示す標識クレーム型（ADR-0010 Phase 4 決定 7）。閲覧認可
    /// （<see cref="ViewerPolicyName"/>）は <see cref="AdminSessionClaimType"/>（管理 ⊇ 閲覧）
    /// または本クレームの存在を正規の判定根拠にする——欠落時は fail-closed（閲覧者として認可しない）。
    /// </summary>
    /// <remarks>
    /// <b>Cookie は管理と共用の単一スキーム</b>（オーナー決定 2026-07-12。ADR-0013 決定 1 の単一 Cookie
    /// モデルを踏襲）: Windows ログインで「閲覧」役割にマップされた利用者は本クレームを持つ Cookie を得る。
    /// Cookie は host スコープ（ポート非依存）ゆえ、管理 Cookie（<see cref="AdminSessionClaimType"/>）は
    /// 閲覧リスナ（8514）でも閲覧できる＝管理 ⊇ 閲覧が自然に成立し、閲覧 Cookie は管理リスナ（8515）では
    /// <see cref="AdminSessionClaimType"/> 欠落で拒否される（fail-closed）。役割の独立失効が必要になった
    /// 場合は別 Cookie スキーム（ADR-0013 方式 (c)）を再評価する。
    /// </remarks>
    public const string ViewerSessionClaimType = "yagura:viewer_session";

    /// <summary>
    /// セッション世代番号を保持するクレーム型（ADR-0013 決定 2）。緊急全失効は現世代番号のバンプで行い、
    /// 各要求で現世代と fail-closed 照合する（旧世代 Cookie は無効）。
    /// </summary>
    public const string SessionGenerationClaimType = "yagura:session_gen";

    /// <summary>認証方式クレーム値: Windows 統合認証（Negotiate）。</summary>
    public const string WindowsAuthMethod = "windows";

    /// <summary>認証方式クレーム値: アプリ独自 ID/パスワード認証。</summary>
    public const string AppAuthMethod = "app";

    /// <summary>
    /// Windows 由来認証セッション Cookie の絶対寿命（ADR-0013 決定 2。仮値 1 時間・sliding 無効）。
    /// 544 判定をログイン時に凍結する方式 B の失効遅延をこの寿命で有界化する。設定での短縮可否は
    /// 実装 PR の後続（委任事項 2。SEC 番号確定）——本 Phase は仮値固定。
    /// </summary>
    public static readonly TimeSpan WindowsSessionAbsoluteLifetime = TimeSpan.FromHours(1);

    /// <summary>アプリ独自認証セッション Cookie の絶対寿命（従来どおり 8 時間・sliding 有効）。</summary>
    public static readonly TimeSpan AppSessionAbsoluteLifetime = TimeSpan.FromHours(8);

    /// <summary>
    /// 認証成立後の管理セッション <see cref="ClaimsPrincipal"/> を組み立てる（ADR-0013 決定 1・5）。
    /// Windows・アプリのいずれの経路も、認証成立点でこの単一の Cookie principal を発行する。
    /// </summary>
    /// <param name="authMethod"><see cref="WindowsAuthMethod"/> または <see cref="AppAuthMethod"/>。</param>
    /// <param name="principalName">主体名（Windows は <c>DOMAIN\user</c>、アプリはユーザー名）。</param>
    /// <param name="generation">発行時点のセッション世代番号（<see cref="IAdminSessionGenerationStore.CurrentGeneration"/>）。</param>
    public static ClaimsPrincipal CreateAdminSessionPrincipal(string authMethod, string principalName, int generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authMethod);
        var identity = new ClaimsIdentity(AppAuthenticationScheme);
        identity.AddClaim(new Claim(ClaimTypes.Name, principalName ?? string.Empty));
        identity.AddClaim(new Claim(AuthMethodClaimType, authMethod));
        identity.AddClaim(new Claim(AdminSessionClaimType, "1"));
        identity.AddClaim(new Claim(SessionGenerationClaimType, generation.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// 認証成立後の<b>閲覧</b>セッション <see cref="ClaimsPrincipal"/> を組み立てる（ADR-0010 Phase 4 決定 7）。
    /// <see cref="CreateAdminSessionPrincipal"/> と同一の単一 Cookie スキームで運ばれるが、標識クレームが
    /// <see cref="ViewerSessionClaimType"/>（管理でなく閲覧）である点だけが異なる——閲覧グループにマップ
    /// された Windows ログインでのみ発行する（管理グループ・544 は <see cref="CreateAdminSessionPrincipal"/> を使う。
    /// 管理 ⊇ 閲覧のため管理セッションも閲覧できる）。
    /// </summary>
    /// <param name="authMethod">現状は <see cref="WindowsAuthMethod"/> のみ（閲覧の主経路。決定 7）。</param>
    /// <param name="principalName">主体名（Windows は <c>DOMAIN\user</c>）。</param>
    /// <param name="generation">発行時点のセッション世代番号（緊急全失効の照合基準。ADR-0013 決定 2）。</param>
    public static ClaimsPrincipal CreateViewerSessionPrincipal(string authMethod, string principalName, int generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authMethod);
        var identity = new ClaimsIdentity(AppAuthenticationScheme);
        identity.AddClaim(new Claim(ClaimTypes.Name, principalName ?? string.Empty));
        identity.AddClaim(new Claim(AuthMethodClaimType, authMethod));
        identity.AddClaim(new Claim(ViewerSessionClaimType, "1"));
        identity.AddClaim(new Claim(SessionGenerationClaimType, generation.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// 認証成立後の Cookie サインインプロパティ（ADR-0013 決定 2 の絶対寿命・sliding 制御）。
    /// Windows 由来は <paramref name="allowRefresh"/>=false（sliding 無効）で短い絶対寿命
    /// （<see cref="WindowsSessionAbsoluteLifetime"/>）、アプリ由来は allowRefresh=true（scheme の
    /// sliding 有効。<see cref="AppSessionAbsoluteLifetime"/>）。管理・閲覧の両ログイン経路が共有する。
    /// </summary>
    public static Microsoft.AspNetCore.Authentication.AuthenticationProperties BuildSessionSignInProperties(
        DateTimeOffset now, TimeSpan absoluteLifetime, bool allowRefresh) => new()
        {
            IsPersistent = false,
            AllowRefresh = allowRefresh,
            IssuedUtc = now,
            ExpiresUtc = now + absoluteLifetime,
        };

    /// <summary>管理 UI 到達に必要な認可ポリシー名（Windows 管理者 SID または AppAuth 認証済み）。</summary>
    public const string AdminPolicyName = "YaguraAdminAccess";

    /// <summary>
    /// 閲覧 UI 到達に必要な認可ポリシー名（ADR-0010 Phase 4 決定 7）。閲覧リスナ（8514）経由・および
    /// リモート管理 HTTPS ポート（ADR-0010 Phase 2）経由の要求に実効化し、閲覧セッション
    /// （<see cref="ViewerSessionClaimType"/>）または管理セッション（<see cref="AdminSessionClaimType"/>。
    /// 管理 ⊇ 閲覧）を要求する。<b>対象外は管理リスナの loopback 束縛ポート経由のみ</b>——そこはローカル復旧/
    /// 全アクセス経路として管理面の規則に委ねる。リモート面まで含めて絞るのは、リモートバインド有効時に
    /// 閲覧ログが無認証で読めるのを防ぐため（<see cref="IsViewerAccessAllowed"/> の remarks 参照）。
    /// </summary>
    public const string ViewerPolicyName = "YaguraViewerAccess";

    /// <summary><c>BUILTIN\Administrators</c> の well-known SID（ADR-0010 決定 5・検証 2）。</summary>
    public const string BuiltinAdministratorsSid = "S-1-5-32-544";

    /// <summary>
    /// ログイン画面のパス。未認証で到達できなければならない唯一の管理画面
    /// （<c>AdminScreenLayout</c>・<c>YaguraAdminExtensions</c> の認可除外対象、および
    /// Cookie 認証の <c>LoginPath</c> の 3 箇所で同じ値を参照する——単一の正とする）。
    /// </summary>
    public const string LoginPath = "/admin/login";

    /// <summary>
    /// 閲覧 UI ログイン画面のパス（ADR-0010 Phase 4）。閲覧リスナ経由で未認証のまま到達できなければ
    /// ならない唯一の閲覧画面（<c>MainLayout</c> の circuit 層 viewer ガードの認可除外対象、
    /// Cookie 認証の viewer 向け <c>OnRedirectToLogin</c> の誘導先、および閲覧認可を課さない
    /// 例外ルートの 3 箇所で同じ値を参照する——単一の正とする）。管理ログイン（<see cref="LoginPath"/>=
    /// <c>/admin/login</c>）とは別ルートであり、管理リスナ帰属を持たない（閲覧リスナで到達できる）。
    /// </summary>
    public const string ViewerLoginPath = "/login";

    /// <summary>
    /// 認証スキーム・認可ポリシーを DI へ登録する。<paramref name="windowsAuthEnabled"/>/
    /// <paramref name="appAuthEnabled"/> がいずれも無効でも <c>AddAuthentication</c>/
    /// <c>AddAuthorization</c> 自体は常に呼ぶ（ミドルウェアの配線を一様にする——無効なスキームは
    /// 単に登録されないだけで、認証パイプライン自体が有害にはならない）。
    /// </summary>
    /// <param name="viewerWindowsAuthEnabled">
    /// 閲覧 UI の Windows 統合認証（<c>Viewer:Authentication:Windows:Enabled</c>。ADR-0010 Phase 4 決定 7）。
    /// <see langword="true"/> の場合、認証スキーム（Negotiate・Cookie）を管理側と<b>共用</b>で登録する
    /// （スキームは WebApplication 単位で 1 つ——管理・閲覧の両リスナが同じ Negotiate/Cookie を使う。
    /// 役割の区別は Cookie のクレーム（<see cref="AdminSessionClaimType"/>/<see cref="ViewerSessionClaimType"/>）で行う）。
    /// </param>
    /// <param name="viewerKerberosOnly">
    /// 閲覧 UI の Kerberos-only モード（<c>Viewer:Authentication:Windows:KerberosOnly</c>）。NTLM 資格情報の
    /// 非永続化（多層防御）に反映する。実際の NTLM 遮断はポート別の <see cref="KerberosOnlyFilterMiddleware"/> が担う。
    /// </param>
    public static IServiceCollection AddYaguraAdminAuthentication(
        this IServiceCollection services,
        bool windowsAuthEnabled,
        bool kerberosOnly,
        bool appAuthEnabled,
        bool viewerWindowsAuthEnabled = false,
        bool viewerKerberosOnly = false)
    {
        ArgumentNullException.ThrowIfNull(services);

        // SEC-9 の解決済みグループ SID 集合（名 → SID 解決は Windows 専用ゆえ Host が解決して上書き登録する）。
        // ここでは既定として空集合を登録し、ログインエンドポイント（WindowsGroupAuthorizationOptions を DI 要求する）が
        // グループ未構成のテスト・ハーネスでも解決できるようにする（Host は AddSingleton で実値を後勝ちで上書きする）。
        services.TryAddSingleton(WindowsGroupAuthorizationOptions.Empty);

        // 認証スキームは WebApplication 単位で 1 つ。管理・閲覧のいずれかが Windows 統合認証を要求すれば
        // Negotiate を、いずれかが何らかの認証を要求すれば認証セッション Cookie を登録する（ADR-0013 決定 1 の
        // 単一 Cookie モデルを閲覧へも共用——オーナー決定 2026-07-12）。
        var negotiateNeeded = windowsAuthEnabled || viewerWindowsAuthEnabled;
        var cookieNeeded = windowsAuthEnabled || appAuthEnabled || viewerWindowsAuthEnabled;

        var authBuilder = services.AddAuthentication(options =>
        {
            // 既定スキームは認証セッション Cookie——通常の要求はこの Cookie の有無で認証状態を判定する
            // （ADR-0013 決定 1）。Negotiate は選択式ログインの Windows 経路（/admin/login/windows）
            // でのみ明示スキーム指定でチャレンジし、他の管理画面へは波及させない（決定 3・委任事項 9）。
            options.DefaultAuthenticateScheme = AppAuthenticationScheme;
            options.DefaultSignInScheme = AppAuthenticationScheme;
            // 既定チャレンジスキームも Cookie に固定する（ADR-0013 決定 1）——未認証の管理要求は
            // 選択式ログイン画面（/admin/login）へ 302 誘導し、Negotiate の自動チャレンジに落とさない。
            // これを明示しないと、Windows 認証のみの構成では唯一登録された Negotiate へフォールバックし、
            // 全画面で資格情報ダイアログが出る（かつ #252 の 401 ループの一因になっていた）。
            options.DefaultChallengeScheme = AppAuthenticationScheme;
        });

        if (negotiateNeeded)
        {
            authBuilder.AddNegotiate(options =>
            {
                // ハンドラ内部の NTLM 資格情報の非永続化は<b>管理側</b> Kerberos-only にのみ連動させる
                // （SEC-9 レビュー指摘）。Negotiate ハンドラは WebApplication 単位で 1 つ（管理・閲覧で共用）
                // であり、ここを閲覧側 Kerberos-only にも連動させると、管理側が NTLM を許容している構成で
                // 管理 NTLM の接続内資格情報永続化が失われ、要求ごとに握手をやり直す劣化を招く。閲覧側の
                // NTLM 遮断は KerberosOnlyFilterMiddleware がポート別にハンドラ手前で行うため、この設定に
                // 依存しない（閲覧 NTLM はハンドラへ到達しない）。管理側は NTLM 遮断（middleware）に加えた多層防御。
                if (kerberosOnly)
                {
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

        // 認証セッション Cookie スキームは、Windows 統合認証・アプリ独自認証のいずれか一方でも
        // 有効なら登録する（ADR-0013 決定 1）。従来はアプリ独自認証有効時のみ登録しており、
        // Windows 認証のみの構成で Cookie スキームが未登録 → 既定認証スキームが解決できず匿名 →
        // 401 ループ、というのが #252 の直接原因だった。
        if (cookieNeeded)
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
                // 既定（scheme レベル）の絶対寿命・sliding はアプリ独自認証セッション向け（8 時間）。
                // Windows 由来セッションは発行時に AuthenticationProperties で短い絶対寿命・sliding 無効へ
                // 上書きする（ADR-0013 決定 2。AdminAuthEndpoints の SignInAsync 参照）。
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                // API 的な POST エンドポイント（/admin/login/app）はリダイレクトではなく
                // ステータスコードで応答したいため、既定のリダイレクト挙動を上書きしない
                // （LoginPath への 302 のままでよい——通常のブラウザ遷移を想定）。

                // セッション世代番号の fail-closed 照合（ADR-0013 決定 2 の緊急全失効）。
                // Cookie に焼き込まれた世代番号が現世代と一致しなければ principal を破棄する
                // （旧世代 Cookie は無効）。世代番号クレーム自体が欠落している Cookie も同様に拒否する。
                options.Events.OnValidatePrincipal = context =>
                {
                    var store = context.HttpContext.RequestServices.GetService<IAdminSessionGenerationStore>();
                    if (store is null)
                    {
                        // 世代ストア未配線は fail-closed（セッションを無効化）。
                        context.RejectPrincipal();
                        return Task.CompletedTask;
                    }

                    var genClaim = context.Principal?.FindFirst(SessionGenerationClaimType)?.Value;
                    if (genClaim is null ||
                        !int.TryParse(genClaim, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var cookieGen) ||
                        cookieGen != store.CurrentGeneration)
                    {
                        context.RejectPrincipal();
                    }

                    return Task.CompletedTask;
                };

                // 未認証で認可ポリシーに弾かれたときのログイン誘導先はリスナ帰属で分ける
                // （ADR-0010 Phase 4）: 管理リスナ帰属ポート経由は既定（LoginPath=/admin/login）、
                // 閲覧リスナ経由は閲覧ログイン（/login）——閲覧リスナでは /admin/login が
                // ListenerPortGuard により 404 になるため、そこへ誘導すると行き止まりになる。
                // 判定は接続の実ローカルポート（クライアントが偽装できない値）で行う。
                options.Events.OnRedirectToLogin = context =>
                {
                    // 既定の Cookie ハンドラは AJAX/API 要求（<c>X-Requested-With: XMLHttpRequest</c>）には 302 でなく
                    // 401 を返す。本上書きでもその挙動を保つ（302 リダイレクトを前提にしない非ブラウザ管理クライアント
                    // 向けの互換——auth-wiring レビュー指摘）。ブラウザ遷移（#252/#255 の経路）はこのヘッダを持たない。
                    if (string.Equals(
                        context.Request.Headers[Microsoft.Net.Http.Headers.HeaderNames.XRequestedWith],
                        "XMLHttpRequest",
                        StringComparison.Ordinal))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }

                    var adminPort = context.HttpContext.RequestServices.GetService<YaguraAdminListenerPort>();
                    var onAdminListener = adminPort is not null && adminPort.Contains(context.HttpContext.Connection.LocalPort);

                    // 管理リスナ帰属は既定の誘導先（context.RedirectUri = LoginPath + ReturnUrl）を維持する
                    // （#252/#255 で確立した管理ログイン挙動を変えない）。閲覧リスナは /login へ誘導する
                    // （ReturnUrl は付さない——閲覧ログイン成功後は "/" へ固定遷移する）。
                    context.Response.Redirect(onAdminListener ? context.RedirectUri : ViewerLoginPath);
                    return Task.CompletedTask;
                };
            });
        }

        // 認可の正規判定根拠は「管理セッションクレームを持つ認証セッション Cookie」（ADR-0013 決定 5）。
        // Windows 由来セッションも認証成立後は Cookie（ClaimsIdentity）で運ばれ WindowsIdentity 型を
        // 持たないため、Cookie 搬送要求の認可を IsWindowsAdministrator（型ゲート）に依存させない——
        // IsWindowsAdministrator はログイン時の Cookie 発行判定（/admin/login/windows）でのみ使う。
        // クレーム欠落時は fail-closed（IsAdminSessionAuthenticated が false を返す）。世代番号の照合は
        // Cookie ハンドラの OnValidatePrincipal が先に行い、旧世代・世代欠落の Cookie はここへ到達しない。
        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicyName, policy => policy.RequireAssertion(context =>
                IsAdminSessionAuthenticated(context.User) ||
                IsUnauthenticatedLoopbackBypassAllowed(context)))
            // 閲覧認可（ADR-0010 Phase 4 決定 7）。閲覧リスナ帰属ポート経由の要求にのみ実効化し、
            // 管理 ⊇ 閲覧で管理・閲覧いずれのセッションも通す。本ポリシーは閲覧認証有効時のみ
            // 閲覧ルートへ付与される（MapYaguraWebViewer 参照）ため、assertion 自体は「有効か」を
            // 再確認せず、リスナ帰属と閲覧可否のみで判定する。
            .AddPolicy(ViewerPolicyName, policy => policy.RequireAssertion(IsViewerAccessAllowed));

        return services;
    }

    /// <summary>
    /// 閲覧認可ポリシー（<see cref="ViewerPolicyName"/>）の判定（ADR-0010 Phase 4 決定 7）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>管理リスナの loopback 束縛ポート経由の閲覧画面のみ本ポリシーの対象外とする</b>: 閲覧ルート
    /// （Razor Components ページ・CSV）は管理リスナからも到達できる（ui.md §4）。管理リスナの <b>loopback</b>
    /// 面はローカルの復旧・全アクセス経路（<c>Admin:...:RequireForLoopback</c> 既定 false と整合）であり、
    /// ここは無条件に通して管理面の規則に委ねる。
    /// </para>
    /// <para>
    /// <b>リモート管理 HTTPS ポート（別ポート。ADR-0010 Phase 2）経由は閲覧セッションを要求する</b>
    /// （田中・クリスのレビュー指摘）: <see cref="YaguraAdminListenerPort.Ports"/> にはリモート HTTPS ポートも
    /// 含まれるため、「管理帰属ポートなら無条件 allow」だと <c>RemoteBinding</c> + 閲覧認証を同時に有効化した
    /// 構成で、リモート面に到達した<b>未認証</b>クライアントが閲覧ルート（ログ本体・CSV 全文）を素通りで読める。
    /// 閲覧認証を有効化してもこの面が閉じないのは false sense of security であり、閲覧認証有効時はリモート面にも
    /// 閲覧セッションを要求する（判定は <see cref="IsLoopbackAdminConnection"/>——loopback 管理ポートのみ true）。
    /// </para>
    /// <para>
    /// <b>閲覧リスナ経由も管理 ⊇ 閲覧のセッションを要求する</b>（<see cref="IsViewingAllowed"/>）。
    /// <see cref="HttpContext"/> が取得できない場合は fail-closed（拒否）。
    /// </para>
    /// </remarks>
    private static bool IsViewerAccessAllowed(AuthorizationHandlerContext context)
    {
        if (context.Resource is not HttpContext httpContext)
        {
            // Resource が HttpContext として取得できない場合は fail-closed（判定不能を許可側へ倒さない）。
            return false;
        }

        if (IsLoopbackAdminConnection(httpContext))
        {
            // 管理リスナの loopback 束縛ポート経由の閲覧画面のみ本ポリシーの対象外（管理面の規則に従う）。
            // リモート管理 HTTPS ポート経由は下の閲覧セッション判定へ落とす（未認証読み取りを塞ぐ）。
            return true;
        }

        // 閲覧リスナ経由・リモート管理 HTTPS 経由 → 管理 ⊇ 閲覧のセッションを要求する。
        return IsViewingAllowed(context.User);
    }

    /// <summary>
    /// 認証成立後の管理セッション（<see cref="AdminSessionClaimType"/> クレームを持つ認証済み Cookie）かどうか
    /// （ADR-0013 決定 5）。Windows・アプリのいずれで認証しても認証成立後はこの判定を通る。標識クレームの
    /// 欠落時は false（fail-closed——クレーム喪失で管理者へ昇格させない）。
    /// </summary>
    public static bool IsAdminSessionAuthenticated(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return (user.Identity?.IsAuthenticated ?? false) && user.HasClaim(c => c.Type == AdminSessionClaimType);
    }

    /// <summary>
    /// 認証成立後の<b>閲覧</b>セッション（<see cref="ViewerSessionClaimType"/> クレームを持つ認証済み Cookie）か
    /// どうか（ADR-0010 Phase 4 決定 7）。標識クレーム欠落時は false（fail-closed）。
    /// </summary>
    public static bool IsViewerSessionAuthenticated(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return (user.Identity?.IsAuthenticated ?? false) && user.HasClaim(c => c.Type == ViewerSessionClaimType);
    }

    /// <summary>
    /// 閲覧が許可されるセッションかどうか（ADR-0010 Phase 4 決定 7 の「管理 ⊇ 閲覧」）: 管理セッション
    /// （<see cref="IsAdminSessionAuthenticated"/>）または閲覧セッション（<see cref="IsViewerSessionAuthenticated"/>）の
    /// いずれか。閲覧認可ポリシー（<see cref="ViewerPolicyName"/>）・<c>MainLayout</c> の circuit 層ガードが使う。
    /// </summary>
    public static bool IsViewingAllowed(ClaimsPrincipal user) =>
        IsAdminSessionAuthenticated(user) || IsViewerSessionAuthenticated(user);

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
    public static bool IsWindowsAdministrator(ClaimsPrincipal user) =>
        IsWindowsAdministrator(user, EmptyGroupSids);

    /// <summary>
    /// <paramref name="user"/> が Windows 統合認証で「管理」役割に該当するか（ADR-0010 決定 5・検証 2 +
    /// SEC-9・委任事項 8）。既定の <c>BUILTIN\Administrators</c>（544）判定に<b>加えて</b>、
    /// <paramref name="additionalAdminGroupSids"/>（<c>Admin:Authentication:Windows:AdminGroups</c> の解決済み
    /// SID 集合）との交差も管理者と認可する（544 判定を置き換えず追加する。射程の限界を論じた本メソッドの
    /// 単一引数版の remarks も参照）。
    /// </summary>
    public static bool IsWindowsAdministrator(ClaimsPrincipal user, IReadOnlySet<string> additionalAdminGroupSids)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(additionalAdminGroupSids);

        // 認可の SID モデルは Windows 固有（非 Windows では管理者と判定しない＝安全側。CA1416 ガードも兼ねる）。
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        // 「Windows 統合認証で確立された identity か」は WindowsIdentity 型で判定する（issue #235。
        // スキーム名文字列一致では Kerberos ログオンの管理者を取りこぼす）。認証済み WindowsIdentity が
        // 1 つでもあり、かつ 544 または設定された管理グループ SID を持てば管理者。
        return HasAuthenticatedWindowsIdentity(user) &&
            (HasBuiltinAdministratorsSid(user) || HasAnyGroupSid(user, additionalAdminGroupSids));
    }

    /// <summary>
    /// <paramref name="user"/> が Windows 統合認証で「閲覧」役割に該当するか（ADR-0010 Phase 4 決定 7・SEC-9）:
    /// 認証済み <see cref="WindowsIdentity"/> を持ち、かつ <paramref name="viewerGroupSids"/>
    /// （<c>Viewer:Authentication:Windows:ViewerGroups</c> の解決済み SID 集合）と交差する。閲覧ログインの
    /// 役割判定に使う——管理役割（544/管理グループ）は <see cref="IsWindowsAdministrator(ClaimsPrincipal, IReadOnlySet{string})"/>
    /// で先に判定し、管理でなければ本判定へ落とす（管理 ⊇ 閲覧）。
    /// </summary>
    public static bool IsWindowsViewer(ClaimsPrincipal user, IReadOnlySet<string> viewerGroupSids)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(viewerGroupSids);

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return HasAuthenticatedWindowsIdentity(user) && HasAnyGroupSid(user, viewerGroupSids);
    }

    /// <summary>認証済みの <see cref="WindowsIdentity"/> を（全 identity 走査で）1 つでも持つか。</summary>
    private static bool HasAuthenticatedWindowsIdentity(ClaimsPrincipal user)
    {
        // 非 Windows では WindowsIdentity は存在しない（CA1416 ガードも兼ねる——呼び出し側も
        // 二重に OperatingSystem.IsWindows() を通すが、本メソッド単体で分析器を満たすため再掲する）。
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        foreach (var identity in user.Identities)
        {
            if (identity is WindowsIdentity { IsAuthenticated: true })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>グループマッピング未構成時の空集合（<see cref="IsWindowsAdministrator(ClaimsPrincipal)"/> の既定）。</summary>
    private static readonly IReadOnlySet<string> EmptyGroupSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="user"/> が <c>BUILTIN\Administrators</c>（<see cref="BuiltinAdministratorsSid"/>）の
    /// グループ SID クレームを持つか（ADR-0010 決定 5・検証 2 の SID 判定）。型に依存しない純粋関数として
    /// 切り出し、<see cref="ClaimsPrincipal"/> だけで（AD 実環境なしに）単体テスト可能にする（issue #235。
    /// <see cref="WindowsIdentity"/> 型ゲートの正パスは実 Windows トークンを要するため lab 統合検証に委ねる）。
    /// </summary>
    internal static bool HasBuiltinAdministratorsSid(ClaimsPrincipal user) =>
        user.HasClaim(ClaimTypes.GroupSid, BuiltinAdministratorsSid);

    /// <summary>
    /// <paramref name="user"/> のトークンのグループ SID クレーム（<see cref="ClaimTypes.GroupSid"/>——
    /// <see cref="WindowsIdentity.Groups"/> がネスト展開済みで載せる推移的グループ）の集合が、
    /// <paramref name="groupSids"/>（設定で指定された役割グループの解決済み SID 集合）と交差するか（SEC-9）。
    /// 型に依存しない純粋関数として、<see cref="ClaimsPrincipal"/> だけで（AD 実環境なしに）単体テスト可能にする。
    /// </summary>
    /// <remarks>
    /// 照合は SID 文字列で行う（名の表記ゆれ・別名解決の非決定性を認可判定から排除する——
    /// <see cref="WindowsGroupAuthorizationOptions"/> の remarks）。<paramref name="groupSids"/> の比較は
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> を想定する（SID 文字列は大文字小文字を区別しない
    /// 16 進・十進表記のため。解決段の <c>HashSet</c> も同じ比較器で構築する）。
    /// </remarks>
    public static bool HasAnyGroupSid(ClaimsPrincipal user, IReadOnlySet<string> groupSids)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(groupSids);

        if (groupSids.Count == 0)
        {
            return false;
        }

        foreach (var claim in user.Claims)
        {
            if (string.Equals(claim.Type, ClaimTypes.GroupSid, StringComparison.Ordinal) &&
                groupSids.Contains(claim.Value))
            {
                return true;
            }
        }

        return false;
    }

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
        if (runtimeOptions is null)
        {
            return false;
        }

        return !runtimeOptions.RequireAuthentication && IsLoopbackAdminConnection(httpContext);
    }

    /// <summary>
    /// 現在の接続が管理リスナの<b>loopback 束縛ポート</b>（<c>Admin:HttpPort</c>）経由かどうかを
    /// 判定する単一の判定点（ADR-0011 決定 4）。<see cref="IsUnauthenticatedLoopbackBypassAllowed"/>
    /// （認可バイパスの判定）に加え、三層防御（IP レート制限・グローバルトークンバケット・
    /// アカウント単位バックオフのキー分離）の loopback 判定も本メソッドを共有する——
    /// 「loopback の定義が層ごとにずれない」ことを、判定ロジックの単一箇所化で保証する
    /// （ADR-0011 委任事項 9・10）。<c>RequireAuthentication</c>（loopback 認証 opt-in）の考慮は
    /// 含まない——それは「認証そのものの要否」であり、ここで扱う「失敗試行対策としての追加の
    /// 遅延・拒否の要否」とは独立の軸（ADR-0011 決定 4）。
    /// </summary>
    public static bool IsLoopbackAdminConnection(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var adminListenerPort = httpContext.RequestServices.GetService<YaguraAdminListenerPort>();
        if (adminListenerPort is null)
        {
            return false;
        }

        return httpContext.Connection.LocalPort == adminListenerPort.Port;
    }

    /// <summary>
    /// 認証パイプラインをアプリケーションビルダーへ組み込む
    /// （<c>UseRouting</c> 後・エンドポイント実行前。<c>UseYaguraListenerPortGuard</c> と同じ順序制約）。
    /// </summary>
    public static IApplicationBuilder UseYaguraAdminAuthentication(
        this IApplicationBuilder app, bool kerberosOnly, bool viewerKerberosOnly = false)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (kerberosOnly || viewerKerberosOnly)
        {
            // Negotiate ハンドラより前段で NTLM トークンを遮断する（クラスの remarks 参照）。
            // 管理・閲覧のどちらの Kerberos-only が有効でも配線し、実際にどのリスナで遮断するかは
            // ミドルウェアが接続の実ローカルポート（管理帰属か否か）で判定する（ポート別 opt-in）。
            app.UseMiddleware<KerberosOnlyFilterMiddleware>(kerberosOnly, viewerKerberosOnly);
        }

        app.UseAuthentication();
        app.UseAuthorization();

        return app;
    }
}
