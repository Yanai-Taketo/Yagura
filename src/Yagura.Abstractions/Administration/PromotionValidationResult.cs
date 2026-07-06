namespace Yagura.Abstractions.Administration;

/// <summary>
/// SQL Server 接続検証の結果（database.md §6.1 準備フェーズ）。
/// </summary>
/// <param name="Success">接続検証に成功したか。</param>
/// <param name="Message">
/// 利用者向けの結果メッセージ（失敗時は原因の要約。<b>接続文字列・パスワードを含めない</b>）。
/// </param>
/// <param name="CredentialRequired">
/// パスワードまたは接続の入力が未設定・破棄済みのため検証を実行できなかった場合
/// <see langword="true"/>（configuration.md §5 の再入力要求）。
/// </param>
/// <param name="FailureKind">
/// 失敗の分類（database.md §6.1——原因別の次の一手の表示に使う。成功時は
/// <see cref="PromotionConnectionFailureKind.None"/>）。
/// </param>
/// <param name="RemediationSql">
/// 修復 SQL（ログイン失敗・データベース不在の場合のみ。環境の実値入り・SQL 認証の
/// パスワードはプレースホルダ——database.md §5.2「提示 SQL は秘密情報を含まない」。
/// <b>表示のみでありサーバは実行しない</b>）。
/// </param>
public sealed record PromotionValidationResult(
    bool Success,
    string Message,
    bool CredentialRequired = false,
    PromotionConnectionFailureKind FailureKind = PromotionConnectionFailureKind.None,
    string? RemediationSql = null);
