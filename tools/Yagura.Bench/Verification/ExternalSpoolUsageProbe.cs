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
    /// <remarks>
    /// 列挙（<see cref="Directory.EnumerateFiles(string, string, SearchOption)"/>）とサイズ取得
    /// （<see cref="FileInfo.Length"/>）の間には必ず時間差があり、その間に drain 処理が対象
    /// ファイルを削除し得る（Issue #178）。これは drain が完了に近づいている正常な競合であり
    /// エラーではないため、列挙後に消えたファイルは 0 バイト扱いでスキップする。列挙の開始後に
    /// スプールディレクトリごと削除される競合（<see cref="Directory.Exists"/> の事前確認だけでは
    /// 防げない）も同様に許容し、その時点までに積算済みの合計を返す。
    /// </remarks>
    public static long GetSegmentBytesOnDisk(string spoolDirectory)
    {
        if (!Directory.Exists(spoolDirectory))
        {
            return 0;
        }

        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(spoolDirectory, "*.seg*", SearchOption.TopDirectoryOnly))
            {
                total += TryGetFileLength(file);
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // 列挙中にスプールディレクトリごと削除された（drain 完了直後の掃除など）。
            // これも drain 進行に伴う正常事象であり、ここまでに積算済みの合計を返せばよい。
        }

        return total;
    }

    /// <summary>
    /// 列挙結果を得てから <see cref="FileInfo.Length"/> を読むまでの間に drain が当該ファイルを
    /// 削除した場合、0 として扱う（ScenarioRunner を落とさない）。
    /// </summary>
    private static long TryGetFileLength(string filePath)
    {
        try
        {
            return new FileInfo(filePath).Length;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return 0;
        }
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
