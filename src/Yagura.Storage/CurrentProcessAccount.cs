namespace Yagura.Storage;

/// <summary>
/// 実行アカウント名の解決（SQL Server ログイン名の表示・修復 SQL の生成に使う。database.md §6.1）。
/// 本製品は Windows 専用（ADR-0001）——非 Windows 分岐は CA1416（プラットフォーム互換性解析）
/// ガードであり、テスト実行環境向け。
/// </summary>
public static class CurrentProcessAccount
{
    public static string ResolveName() =>
        OperatingSystem.IsWindows()
            ? System.Security.Principal.WindowsIdentity.GetCurrent().Name
            : Environment.UserName;
}
