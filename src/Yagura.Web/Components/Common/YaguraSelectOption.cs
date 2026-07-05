namespace Yagura.Web.Components.Common;

/// <summary>
/// 選択入力（<see cref="YaguraSelectField"/>）の選択肢（M8-3）。
/// </summary>
/// <param name="Value">選択値（フォーム値として往復する文字列）。</param>
/// <param name="Label">表示文言（平易語。ui.md §7）。</param>
public sealed record YaguraSelectOption(string Value, string Label);
