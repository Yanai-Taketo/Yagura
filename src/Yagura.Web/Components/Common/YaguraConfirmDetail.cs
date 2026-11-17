namespace Yagura.Web.Components.Common;

/// <summary>確認ダイアログの補足の 1 項目（何を・どこへ等。要約と要確認事項の下に並ぶ）。</summary>
/// <param name="Label">項目名。</param>
/// <param name="Value">内容。</param>
public sealed record YaguraConfirmDetail(string Label, string Value);
