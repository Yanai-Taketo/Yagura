namespace Yagura.Web.Components.Common;

/// <summary>
/// 証明書の期限切れ・秘密鍵読取不可を、保存を拒否する事象として扱うか警告に留めるかの区分。
/// <see cref="YaguraCertificateSelectionPanel"/> のバッジ・注記の色を決めるだけで、実際に保存を
/// 拒否するかどうかは呼び出し側の保存前検証（各 AdminService）が決める——本区分は表示の一致を
/// 保証するのみで、検証ロジックの代わりにはならない。
/// </summary>
public enum YaguraCertificateIssueSeverity
{
    /// <summary>警告のみ（選択・保存とも妨げない想定の画面で使う）。</summary>
    Warning,

    /// <summary>拒否事象（選択はできても保存時に拒否される想定の画面で使う）。</summary>
    Error,
}
