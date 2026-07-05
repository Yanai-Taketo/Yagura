using Microsoft.Extensions.Logging;

namespace Yagura.Host.Observability.Auditing;

/// <summary>
/// Windows イベントログの 3000 番台（拒否・セキュリティ事象）採番表（security.md §4.3。
/// SEC-5 の初版。M6-2。Issue #52）。
/// </summary>
/// <remarks>
/// <para>
/// <b>additive-only の凍結対象</b>: security.md §4.3「一度公開した ID の意味とレベルは変えない」
/// に従う。本クラスへ新しい ID を追加する場合は、必ず security.md §4.3 の ID 表へ同じ PR で
/// 追記すること（意味の変更・転用は行わない）。
/// </para>
/// </remarks>
public static class AuditEventIds
{
    /// <summary>
    /// 閲覧リスナに到達した管理系要求の拒否（security.md §1 L-3b）。レベルは警告。
    /// </summary>
    public static readonly EventId ViewerListenerAdminRequestRejected = new(3001, "ViewerListenerAdminRequestRejected");
}
