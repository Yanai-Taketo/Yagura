namespace Yagura.Abstractions.Administration;

/// <summary>
/// ウィザードのステップ入力値が検証に失敗した場合に送出する例外（ステップは確定されない）。
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
