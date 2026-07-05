namespace Yagura.Storage.Spool;

/// <summary>
/// <see cref="DiskSpool"/> の構成（configuration.md §8「スプール」区分）。
/// </summary>
public sealed class DiskSpoolOptions
{
    /// <summary>
    /// スプールを有効にするか（既定 <c>true</c>。configuration.md §8「有効/無効（opt-out）」）。
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// スプールのセグメントファイルを置くディレクトリの絶対パス
    /// （既定はデータルート配下。configuration.md §2）。
    /// </summary>
    public required string Directory { get; init; }

    /// <summary>
    /// ディスク使用量の上限（バイト）。architecture.md §3.2.3・M-12 の実測確定待ちの暫定値
    /// （<see cref="SpoolConstants.DefaultQuotaBytes"/> 参照）。
    /// </summary>
    public long QuotaBytes { get; init; } = SpoolConstants.DefaultQuotaBytes;
}
