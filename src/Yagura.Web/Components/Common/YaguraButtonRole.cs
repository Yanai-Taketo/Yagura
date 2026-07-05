namespace Yagura.Web.Components.Common;

/// <summary>
/// ボタンの役割（ui.md §3.1 ボタン規約の実装形）。
/// </summary>
public enum YaguraButtonRole
{
    /// <summary>通常の操作（アウトラインボタン）。</summary>
    Normal,

    /// <summary>
    /// 主ボタン（primary 塗り）。<b>1 ビューに 1 つまで</b>——ビュー = 同時に視認される単位
    /// （ページ本体・ダイアログ・ウィザードの 1 ステップはそれぞれ別のビュー。ui.md §3.1）。
    /// </summary>
    Primary,

    /// <summary>
    /// 破壊的操作（削除・昇格確定等）。state-error 系 + <b>確認ダイアログ必須</b>
    /// （ui.md §3.1——<see cref="YaguraButton"/> がダイアログ表示を内蔵し、確認なしで
    /// 実行できない構造にする）。
    /// </summary>
    Destructive,
}
