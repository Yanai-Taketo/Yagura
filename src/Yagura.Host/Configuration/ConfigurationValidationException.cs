namespace Yagura.Host.Configuration;

/// <summary>
/// configuration.md §1「起動失敗」分類の不正値を検出したときに送出する例外。
/// </summary>
/// <remarks>
/// 受信の成立に不可欠なキー（例: 受信 UDP ポートが範囲外）が不正な場合にのみ使う。
/// それ以外（既定値で継続 / 縮小側で継続）は例外にせず <see cref="ConfigurationWarning"/>
/// として収集し、既定値・安全側の値を適用したうえで起動を継続する（§1 参照）。
/// </remarks>
public sealed class ConfigurationValidationException : Exception
{
    public ConfigurationValidationException(string message)
        : base(message)
    {
    }

    public ConfigurationValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
