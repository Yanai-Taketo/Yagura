namespace Yagura.Web.Components.Common;

/// <summary>
/// 設定・システム由来の警告と案内（ui.md §5.4）の区分。状態色トークンの意味固定
/// （ui.md §2.1）に従い、警告（state-warning）と情報（state-info）のみを持つ
/// ——異常（state-error）は状態帯・通知（トースト）側の管轄。
/// </summary>
public enum YaguraNoticeSeverity
{
    /// <summary>情報（state-info）: 案内・昇格提案。</summary>
    Info,

    /// <summary>警告（state-warning）: 一時保管への退避中・上限接近・縮退運転。</summary>
    Warning,
}
