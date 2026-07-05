namespace Yagura.Abstractions.Administration;

/// <summary>
/// 本番昇格の切替実行の結果。
/// </summary>
/// <param name="Outcome">実行の結果種別（<see cref="WizardApplyOutcome"/>）。</param>
/// <param name="RequiredEffect">
/// 切替の反映に必要なアクション（M8-4 骨格の実効はサービス再起動——configuration.md §8 の
/// <c>Storage:Provider</c> 行。実行時の無瞬断切替手順（database.md §6.1 ①〜④）の実装後に
/// 変わり得る）。
/// </param>
/// <param name="Message">利用者向けの結果メッセージ（失敗時は原因の要約。秘密情報を含めない）。</param>
public sealed record PromotionApplyResult(
    WizardApplyOutcome Outcome,
    ConfigurationApplyEffect RequiredEffect,
    string Message);
