namespace Yagura.Abstractions.Administration;

/// <summary>
/// 管理系サービスの入力値が検証に失敗した場合に送出する例外（当該操作は確定されない）。
/// 名称はウィザードのステップ入力に由来するが、実際にはウィザード外の管理サービス
/// （<c>AdminRemoteAccessAdminService</c>・<c>AdminAuthenticationAdminService</c> ほか）でも
/// 入力検証例外として常用している。<c>AdminInputValidationException</c> 相当への改名が理想だが、
/// 影響範囲が広いため名称は据え置き、doc で実態を明示するに留める（#359 E）。
/// メッセージは画面にそのまま表示できる利用者向けの日本語とする（ui.md §7.1 の文言原則。
/// 秘密情報・内部例外の詳細を含めない）。
/// </summary>
public sealed class WizardValidationException : Exception
{
    public WizardValidationException(string message)
        : base(message)
    {
    }
}
