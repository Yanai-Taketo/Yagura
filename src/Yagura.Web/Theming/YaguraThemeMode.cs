namespace Yagura.Web.Theming;

/// <summary>
/// テーマ選択の 3 態（ui.md §2.4）。既定はライトで、手動選択を尊重する
/// （<see cref="System"/> は利用者が明示的に選んだ場合のみ有効になる）。
/// </summary>
public enum YaguraThemeMode
{
    /// <summary>ライトテーマ（既定）。</summary>
    Light,

    /// <summary>ダークテーマ（ライトと同格。ADR-0003 決定 4）。</summary>
    Dark,

    /// <summary>OS（ブラウザ）のテーマ設定に追従する（opt-in。ui.md §2.4）。</summary>
    System,
}
