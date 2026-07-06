namespace Yagura.Abstractions.Administration;

/// <summary>
/// 本番昇格ウィザードのセッション状態のスナップショット（画面表示用）。
/// <b>パスワードそのものは含めない</b>（configuration.md §5——資格情報はサーバ側の
/// セッション状態・画面表示のいずれにも露出させない。保持の有無のみを示す）。
/// 接続の項目・パスワードを含まない接続文字列は秘密ではなく、再開時の再表示のために含める
/// （同 §5 の資格情報の粒度）。
/// </summary>
/// <param name="InputMode">
/// 接続の入力方式（項目 / 直接入力。最後に設定した方式——<see cref="IPromotionWizardService"/>
/// の相互上書き規則を参照）。
/// </param>
/// <param name="Form">項目入力の内容（未入力は <see langword="null"/>。パスワードを含まない）。</param>
/// <param name="RawConnectionString">
/// 直接入力の接続文字列（未入力は <see langword="null"/>。パスワード系キーは設定時に
/// 拒否されるため秘密を含まない）。
/// </param>
/// <param name="ServiceAccountName">
/// サービスの実行アカウント名（Windows 統合認証で SQL Server へ接続するアカウント。
/// 画面の「接続に使うアカウント」表示と修復 SQL の実値に使う——database.md §6.1）。
/// </param>
/// <param name="HasPassword">パスワードを保持しているか（無操作タイムアウトで false に戻る）。</param>
/// <param name="ConnectionValidated">保持中の接続入力で接続検証に成功済みか。</param>
/// <param name="Disposal">旧・組み込み DB ファイルの処分の選択（未選択は <see langword="null"/>）。</param>
/// <param name="EvacuationDirectory">
/// 退避先のフォルダ（絶対パス。処分の選択が退避の場合のみ非 null——database.md §6.1
/// 「選択内容・退避先は監査記録の対象」の入力面）。
/// </param>
/// <param name="ExecuteIdempotencyToken">
/// 切替実行に使う冪等トークン（接続検証の成功後に非 null。configuration.md §7）。
/// </param>
/// <param name="Executed">切替実行済みか。</param>
/// <param name="CredentialReentryRequired">
/// 無操作タイムアウト（CF-3 仮値 15 分）等でパスワードが破棄され、再入力が必要な状態か
/// （configuration.md §5「再開時に再入力を求める」——確定済みの進行状態は失われない）。
/// </param>
public sealed record PromotionWizardSnapshot(
    PromotionConnectionInputMode InputMode,
    PromotionConnectionForm? Form,
    string? RawConnectionString,
    string ServiceAccountName,
    bool HasPassword,
    bool ConnectionValidated,
    OldDatabaseDisposal? Disposal,
    string? EvacuationDirectory,
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
