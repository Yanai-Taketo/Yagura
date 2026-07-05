namespace Yagura.Storage.Auditing;

/// <summary>
/// 監査記録の事象種別（security.md §4.1・§4.3）。
/// </summary>
/// <remarks>
/// M6-2（Issue #52）時点では <see cref="ViewerListenerAdminRequestRejected"/> のみを実装する。
/// 他の値（認証失敗・origin 拒否等）は該当機能の実装時に追加する
/// （security.md §4.1 の対象一覧・§4.4「事象種別が変われば個別記録する」の前提となる区分）。
/// </remarks>
public enum AuditEventKind
{
    /// <summary>
    /// 閲覧リスナに到達した管理系要求の拒否（security.md §1 L-3b。イベント ID 3001）。
    /// </summary>
    ViewerListenerAdminRequestRejected,
}
