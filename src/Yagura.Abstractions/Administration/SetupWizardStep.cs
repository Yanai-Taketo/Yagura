namespace Yagura.Abstractions.Administration;

/// <summary>
/// 初期セットアップウィザードのステップ構成（M8-4 の骨格。画面の分割・名称は ui.md の管轄
/// ——configuration.md §3 の総称「ウィザード（管理画面）」の初期セットアップ分）。
/// </summary>
/// <remarks>
/// ステップの追加は additive に行う（確定済みステップの再開位置がずれないよう、
/// 既存値の並び替え・転用はしない）。
/// </remarks>
public enum SetupWizardStep
{
    /// <summary>受信設定（UDP/TCP ポート。configuration.md §4.1。既定 514）。</summary>
    Reception,

    /// <summary>閲覧・管理リスナ設定（閲覧ポート・公開範囲・管理ポート。configuration.md §4.2）。</summary>
    ViewerAccess,

    /// <summary>保持期間（Retention:Days。database.md DB-1 既定 30 日）。</summary>
    Retention,

    /// <summary>
    /// 確認（このステップの確定で設定ファイルの現在内容を読み込み、適用用の冪等トークンを
    /// 発行する——configuration.md §3「読み込み → 変更 → 検証 → 保存」の「読み込み」時点）。
    /// </summary>
    Review,
}
