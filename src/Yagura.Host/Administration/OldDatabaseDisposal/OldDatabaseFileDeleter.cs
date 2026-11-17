namespace Yagura.Host.Administration.OldDatabaseDisposal;

/// <summary>
/// 旧ファイルの削除（差し替え可能な口——共有違反・部分失敗の経路を Windows 以外でもテストできるようにする）。
/// </summary>
public interface IOldDatabaseFileDeleter
{
    /// <summary>ファイルを削除する。失敗は例外で知らせる。</summary>
    void Delete(string path);
}

/// <summary>通常のファイル削除（上書き消去はしない——database.md §6.3「削除の手順」）。</summary>
public sealed class SystemOldDatabaseFileDeleter : IOldDatabaseFileDeleter
{
    /// <inheritdoc />
    public void Delete(string path) => File.Delete(path);
}

/// <summary>削除の失敗の分類。</summary>
public static class OldDatabaseFileErrors
{
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);

    /// <summary>共有違反（別のプログラムが一瞬開いている等。短く再試行する対象）か。</summary>
    public static bool IsSharingViolation(Exception exception) =>
        exception is IOException { HResult: ErrorSharingViolation or ErrorLockViolation };
}
