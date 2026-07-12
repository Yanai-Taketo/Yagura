namespace Yagura.Web.Administration;

/// <summary>
/// AD グループ → 役割マッピング（SEC-9。ADR-0010 決定 5・7・委任事項 8）の解決済み SID 集合を
/// Web 層へ供給する DI 用ラッパー（<see cref="YaguraAdminListenerPort"/> と同じ「Host が解決した値を
/// 専用型で Web へ渡す」パターン）。
/// </summary>
/// <remarks>
/// <para>
/// <b>なぜ SID 集合を渡すか</b>: グループ名（<c>DOMAIN\Group</c>）→ SID の解決は Windows 専用 API
/// （<c>NTAccount.Translate</c>）を要し、環境（DC 到達性）にも依存するため、起動時に一度だけ解決して
/// キャッシュする（<c>Yagura.Host.Administration.AdminAuthentication.WindowsSecurityGroupResolver</c>）。
/// 認可の実照合（<see cref="AdminAuthenticationExtensions.HasAnyGroupSid"/>）は、トークンの推移的
/// グループ SID クレーム（<c>WindowsIdentity.Groups</c>——ネストは OS が展開済み。追加 LDAP 不要）と
/// 本集合の交差判定であり、SID 文字列の集合演算だけで済む（純粋・単体テスト可能）。
/// </para>
/// <para>
/// <b>照合は SID 集合で行い名では行わない</b>: 名（<c>DOMAIN\Group</c>）と SID の両形式を受理するが、
/// 解決段で全て SID へ正規化する——トークン側のグループはクレームとして SID でしか露出しないため、
/// 照合を SID に一本化することで名の表記ゆれ・別名解決の非決定性を認可判定から排除する。
/// </para>
/// </remarks>
/// <param name="AdminGroupSids">
/// 「管理」役割にマップする AD グループの SID 集合（<c>Admin:Authentication:Windows:AdminGroups</c>。
/// SEC-9）。管理リスナの Windows ログイン判定で、既定の <c>BUILTIN\Administrators</c>（544）に<b>加えて</b>
/// この集合との交差も管理者と認可する（<see cref="AdminAuthenticationExtensions.IsWindowsAdministrator(System.Security.Claims.ClaimsPrincipal, System.Collections.Generic.IReadOnlySet{string})"/>）。
/// </param>
/// <param name="ViewerGroupSids">
/// 「閲覧」役割にマップする AD グループの SID 集合（<c>Viewer:Authentication:Windows:ViewerGroups</c>。
/// ADR-0010 Phase 4）。閲覧リスナの Windows ログインで、この集合との交差を「閲覧」役割として認可する。
/// </param>
/// <param name="ViewerAdminGroupSids">
/// 閲覧リスナ経由のログインで「管理」役割にマップする AD グループの SID 集合
/// （<c>Viewer:Authentication:Windows:AdminGroups</c>）。所属者は管理セッションを得て管理 ⊇ 閲覧で
/// 閲覧できる（かつ管理リスナへも到達できる——Cookie は host スコープ）。閲覧リスナの Windows ログインで
/// 544 とこの集合を管理者判定に用いる。
/// </param>
public sealed record WindowsGroupAuthorizationOptions(
    IReadOnlySet<string> AdminGroupSids,
    IReadOnlySet<string> ViewerGroupSids,
    IReadOnlySet<string> ViewerAdminGroupSids)
{
    /// <summary>
    /// グループマッピング未構成（SEC-9 の既定——グループ指定なし）。管理側は 544 判定のみ、
    /// 閲覧側は該当グループがないため誰も認可されない（閲覧認証を有効化したのにグループ未指定の
    /// 誤設定は Program 起動時に警告する——自己ロックアウト注意）。
    /// </summary>
    public static readonly WindowsGroupAuthorizationOptions Empty = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}
