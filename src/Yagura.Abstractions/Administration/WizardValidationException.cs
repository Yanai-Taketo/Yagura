namespace Yagura.Abstractions.Administration;

/// <summary>
/// 管理 UI の入力値が検証に失敗した場合に送出する例外。ウィザードのステップ入力に限らず、
/// 管理サービスの保存前検証全般で用いる（検証に失敗したステップ/設定は確定されない）。
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
