namespace Yagura.Web.Components.Common;

/// <summary>
/// 状態帯（ui.md §3.1 状態帯・§5.1）の 3 状態。状態色トークン（state-ok / state-warning /
/// state-error）と 1 対 1 に対応し、これ以外の状態を追加しない（意味の固定。ui.md §2.1）。
/// </summary>
public enum YaguraStatusKind
{
    /// <summary>稼働中（state-ok）: 受信リスナが開いており、観測窓内に破棄・障害事象がない。</summary>
    Ok,

    /// <summary>警告あり（state-warning）: スプール退避の継続・上限接近・縮退運転等。</summary>
    Warning,

    /// <summary>異常あり（state-error）: 観測窓内の破棄・保存先障害の継続・受信リスナ未開設等。</summary>
    Error,
}
