namespace Yagura.Host.Configuration;

/// <summary>
/// 設定変更前後の比較結果（configuration.md §3「反映方式を項目の属性として持つ」の
/// 「UI は変更時にそれを利用者へ表示する」を支える計算結果）。
/// </summary>
/// <param name="ChangedKeys">
/// 変更前後で値が異なった設定キーの一覧（JSON キーパス表記。変更順ではなく
/// <see cref="ConfigurationKeyMetadata"/> の登録順で安定させる）。
/// </param>
/// <param name="RequiredEffect">
/// <see cref="ChangedKeys"/> それぞれの反映方式のうち最大のもの（即時 &lt; リスナ再構成 &lt; 再起動。
/// 変更が 1 件もない場合は <see cref="ConfigurationReloadEffect.Immediate"/>）。
/// ウィザードが「この変更には再起動が必要です」等を表示する際の判定に使う。
/// </param>
public sealed record ConfigurationChangePlan(
    IReadOnlyList<string> ChangedKeys,
    ConfigurationReloadEffect RequiredEffect)
{
    /// <summary>変更が 1 件もない場合の既定値（比較用ヘルパ）。</summary>
    public bool HasChanges => ChangedKeys.Count > 0;
}
