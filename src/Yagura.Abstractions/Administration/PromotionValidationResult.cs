namespace Yagura.Abstractions.Administration;

/// <summary>
/// SQL Server 接続検証の結果（database.md §6.1 準備フェーズ）。
/// </summary>
/// <param name="Success">接続検証に成功したか。</param>
/// <param name="Message">
/// 利用者向けの結果メッセージ（失敗時は原因の要約。<b>接続文字列・パスワードを含めない</b>）。
/// </param>
/// <param name="CredentialRequired">
/// 接続文字列が未設定または破棄済みのため検証を実行できなかった場合 <see langword="true"/>
/// （configuration.md §5 の再入力要求）。
/// </param>
public sealed record PromotionValidationResult(
    bool Success,
    string Message,
    bool CredentialRequired = false);
