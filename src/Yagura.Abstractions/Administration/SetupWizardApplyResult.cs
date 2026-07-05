namespace Yagura.Abstractions.Administration;

/// <summary>
/// 初期セットアップウィザードの適用結果。
/// </summary>
/// <param name="Outcome">適用の結果種別。</param>
/// <param name="ChangedKeys">変更された設定キーの一覧（<see cref="Outcome"/> が適用系のとき）。</param>
/// <param name="RequiredEffect">
/// 変更の反映に必要なアクションの最大（configuration.md §3「反映方式を項目の属性として持つ。
/// UI は変更時にそれを利用者へ表示する」の引き受け）。
/// </param>
public sealed record SetupWizardApplyResult(
    WizardApplyOutcome Outcome,
    IReadOnlyList<string> ChangedKeys,
    ConfigurationApplyEffect RequiredEffect)
{
    /// <summary>同一の冪等トークンによる再送だったか（configuration.md §7 の一回性保証）。</summary>
    public bool AlreadyApplied => Outcome == WizardApplyOutcome.AlreadyApplied;
}

/// <summary>確定操作（適用・切替実行）の結果種別。</summary>
public enum WizardApplyOutcome
{
    /// <summary>適用に成功した。</summary>
    Applied,

    /// <summary>同一の冪等トークンによる再送のため、再適用せず前回の結果を返した。</summary>
    AlreadyApplied,

    /// <summary>
    /// 設定ファイルが読み込み後に外部変更されていたため保存を中止した
    /// （configuration.md §3 の楽観的な競合検出。上書きせず再読み込みを促す）。
    /// </summary>
    Conflict,

    /// <summary>冪等トークンが現在のセッションと一致しない（期限切れ・別セッションのトークン）。</summary>
    InvalidToken,
}

/// <summary>
/// 設定変更の反映に必要なアクション（configuration.md §3 の 3 分類。
/// <c>Yagura.Host.Configuration.ConfigurationReloadEffect</c> の横断契約側の表現——
/// Yagura.Abstractions はどの Yagura プロジェクトにも依存しないため独立に定義し、
/// 実装側で対応づける）。
/// </summary>
public enum ConfigurationApplyEffect
{
    /// <summary>即時反映。</summary>
    Immediate,

    /// <summary>リスナ再構成（接続の瞬断を伴う）。</summary>
    ListenerReconfiguration,

    /// <summary>サービス再起動（受信断を伴う）。</summary>
    RestartRequired,
}
