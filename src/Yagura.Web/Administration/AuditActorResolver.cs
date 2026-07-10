using System.Security.Claims;

namespace Yagura.Web.Administration;

/// <summary>
/// 監査記録の「誰が」欄（<c>AuditEvent.AuthenticationScheme</c>/<c>AuthenticatedPrincipal</c>。
/// ADR-0010 決定 6）へ渡す値を <see cref="ClaimsPrincipal"/> から導出する（PR #217 レビュー
/// 指摘 1 の実装——方式識別子の表記を 1 箇所に固定する）。
/// </summary>
/// <remarks>
/// 方式識別子は ADR-0010 決定 3 の「命名空間つき表記」の接頭辞: <c>"windows"</c>（Windows 統合
/// 認証 = Negotiate スキーム）/ <c>"app"</c>（アプリ独自 ID/パスワード = YaguraAppAuth スキーム）。
/// 未認証（既定の loopback 無認証を含む）は両方 <see langword="null"/>——監査記録の「誰が」欄は
/// 接続元アドレスのみが実効値になる（security.md §4.1 の射程限定のとおり）。
/// </remarks>
public static class AuditActorResolver
{
    /// <summary>認証済み利用者の（方式識別子, 利用者名）を導出する。未認証は (null, null)。</summary>
    public static (string? Scheme, string? Principal) Resolve(ClaimsPrincipal? user)
    {
        if (user is null)
        {
            return (null, null);
        }

        if (AdminAuthenticationExtensions.IsWindowsAdministrator(user))
        {
            return ("windows", user.Identity?.Name);
        }

        if (AdminAuthenticationExtensions.IsAppAuthenticated(user))
        {
            return ("app", user.Identity?.Name);
        }

        return (null, null);
    }
}
