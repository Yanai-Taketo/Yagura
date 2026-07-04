namespace Yagura.Host.Configuration;

/// <summary>
/// 保存しようとした設定ファイルが、読み込み時点から外部変更されていた場合に送出する例外
/// （configuration.md §3「楽観的な競合検出」）。
/// </summary>
/// <remarks>
/// 「手編集した内容が画面操作で黙って消えた」事故を防ぐため、書き込み側は上書きせず失敗を返す。
/// 呼び出し側（ウィザード等）は最新の内容を再読み込みしたうえで変更をやり直す。
/// </remarks>
public sealed class ConfigurationConflictException : Exception
{
    public ConfigurationConflictException(string message)
        : base(message)
    {
    }

    public ConfigurationConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
