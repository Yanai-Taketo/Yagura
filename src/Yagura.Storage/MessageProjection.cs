namespace Yagura.Storage;

/// <summary>
/// 一覧射影用のメッセージ切り詰め（database.md §2.1「一覧は先頭 N 文字の射影」）。
/// SQLite・SQL Server 両 provider で同一のロジックを使う（コードレビューで指摘された重複を解消）。
/// </summary>
internal static class MessageProjection
{
    /// <summary>
    /// メッセージを先頭 <paramref name="projectionLength"/> 文字へ切り詰める。
    /// <paramref name="message"/> が <c>null</c> または既に射影長以下の場合はそのまま返す
    /// （パディングはしない）。
    /// </summary>
    public static string? Truncate(string? message, int projectionLength) =>
        message is null || message.Length <= projectionLength ? message : message[..projectionLength];
}
