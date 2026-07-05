namespace Yagura.Storage;

/// <summary>
/// 保持期間削除（database.md §3）の暫定定数。
/// </summary>
/// <remarks>
/// <see cref="Yagura.Storage.Spool.SpoolConstants"/> と同じ運用——実測確定待ちの暫定値。
/// </remarks>
public static class RetentionConstants
{
    /// <summary>
    /// システムイベントの Kind: 保持期間削除の実行記録（database.md §2.3・§3）。
    /// </summary>
    public const string SystemEventKindRetentionDelete = "retention.delete";

    /// <summary>
    /// 保持期間削除 1 回の削除件数上限（分割実行の粒度。database.md §3「受信の書き込みを
    /// 長時間妨げない方式（分割実行等）」）。暫定値: 1,000 件。実測確定待ち。
    /// </summary>
    public const int DeleteBatchMaxSize = 1000;
}
