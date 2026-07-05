namespace Yagura.Abstractions.Administration;

/// <summary>
/// 本番昇格ウィザードのセッション状態のスナップショット（画面表示用）。
/// <b>接続文字列そのものは含めない</b>（configuration.md §5——資格情報はサーバ側の
/// セッション状態・画面表示のいずれにも露出させない。保持の有無のみを示す）。
/// </summary>
/// <param name="HasConnectionString">接続文字列を保持しているか（無操作タイムアウトで false に戻る）。</param>
/// <param name="ConnectionValidated">保持中の接続文字列で接続検証に成功済みか。</param>
/// <param name="Disposal">旧・組み込み DB ファイルの処分の選択（未選択は <see langword="null"/>）。</param>
/// <param name="ExecuteIdempotencyToken">
/// 切替実行に使う冪等トークン（接続検証の成功後に非 null。configuration.md §7）。
/// </param>
/// <param name="Executed">切替実行済みか。</param>
/// <param name="CredentialReentryRequired">
/// 無操作タイムアウト（CF-3 仮値 15 分）等で接続文字列が破棄され、再入力が必要な状態か
/// （configuration.md §5「再開時に再入力を求める」——確定済みの進行状態は失われない）。
/// </param>
public sealed record PromotionWizardSnapshot(
    bool HasConnectionString,
    bool ConnectionValidated,
    OldDatabaseDisposal? Disposal,
    string? ExecuteIdempotencyToken,
    bool Executed,
    bool CredentialReentryRequired);

/// <summary>
/// 旧・組み込み DB ファイルの処分方法（database.md §6.1「旧・組み込み DB ファイルは残置しない」
/// ——ADR-0004 決定 5）。
/// </summary>
public enum OldDatabaseDisposal
{
    /// <summary>明示の退避（利用者が指定する場所への移動。保護責任は退避完了をもって利用者へ移転）。</summary>
    Evacuate,

    /// <summary>削除。</summary>
    Delete,
}
