namespace Yagura.Storage.Administration;

/// <summary>
/// <see cref="IAdminAccountStore"/> 実装が大小文字を区別しないユーザー名照合に使う正規化
/// （前後空白の除去 + 不変カルチャでの小文字化）。DB 側の照合順序設定に依存せず、
/// provider 間で同一の一致判定にする。
/// </summary>
internal static class AdminUsernameNormalization
{
    public static string Normalize(string username) => username.Trim().ToLowerInvariant();
}
