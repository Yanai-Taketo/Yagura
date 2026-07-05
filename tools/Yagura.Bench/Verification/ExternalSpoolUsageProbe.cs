namespace Yagura.Bench.Verification;

/// <summary>
/// スプールディレクトリの使用量を外部プロセスから観測するプローブ（Issue #60。スプール発動 →
/// 追いつきシナリオが「drain が完了し使用量が 0 に戻った」ことを子プロセス外から確認するために使う）。
/// </summary>
/// <remarks>
/// <see cref="Yagura.Storage.Spool.DiskSpool.CurrentUsageBytes"/> はホスト子プロセス内の
/// インメモリカウンタであり、外部からは読めない。ディスク上のセグメントファイル
/// （<c>*.seg</c>。<see cref="Yagura.Storage.Spool.SpoolSegmentFileNames"/> 参照）の合計サイズは
/// プロセス外からも観測できる代替指標として使う——「.seg ファイルの合計バイト数」は
/// <c>DiskSpool</c> 内部の <c>_currentUsageBytes</c>（セグメント追記のたびに増分・削除のたびに
/// 減算する値）と、削除がファイルシステム上の実削除と同期している設計（§3.2.4「通常のファイル
/// 削除で速やかに削除する」）により一致する。
/// </remarks>
public static class ExternalSpoolUsageProbe
{
    /// <summary>
    /// スプールディレクトリ配下の <c>*.seg</c>（drain 中の <c>*.seg.draining</c> を含む）ファイルの
    /// 合計バイト数を返す。ディレクトリが存在しない場合は 0。
    /// </summary>
    public static long GetSegmentBytesOnDisk(string spoolDirectory)
    {
        if (!Directory.Exists(spoolDirectory))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(spoolDirectory, "*.seg*", SearchOption.TopDirectoryOnly))
        {
            total += new FileInfo(file).Length;
        }

        return total;
    }

    /// <summary>
    /// drain が完了する（スプール使用量が 0 に戻る）まで条件ポーリングで待つ。
    /// </summary>
    public static async Task<bool> WaitForDrainCompletionAsync(string spoolDirectory, TimeSpan timeout, TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(200);
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (GetSegmentBytesOnDisk(spoolDirectory) == 0)
            {
                return true;
            }

            await Task.Delay(interval).ConfigureAwait(false);
        }

        return GetSegmentBytesOnDisk(spoolDirectory) == 0;
    }
}
