namespace Yagura.Storage;

/// <summary>
/// 「1 バッチ削除→削除件数がバッチサイズ未満なら終了」の分割実行を共通化する
/// （<see cref="ILogStore.DeleteOlderThanAsync"/> の実装。database.md §3
/// 「受信の書き込みを長時間妨げない方式（分割実行等）」）。1 回分の削除実行（接続の開閉・
/// DELETE 文の実行）は <paramref name="deleteBatchAsync"/> へ注入する——DELETE 文自体
/// （SQLite の副問い合わせ形 / SQL Server の <c>DELETE TOP</c>）は provider ごとの方言のため
/// ここでは扱わない。
/// </summary>
internal static class BatchDeleteLoop
{
    public static async Task<long> RunAsync(
        int batchSize,
        Func<CancellationToken, Task<int>> deleteBatchAsync,
        CancellationToken cancellationToken)
    {
        long totalDeleted = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var deletedInBatch = await deleteBatchAsync(cancellationToken).ConfigureAwait(false);
            totalDeleted += deletedInBatch;

            if (deletedInBatch < batchSize)
            {
                // 削除対象が尽きた（このバッチで上限未満しか削除できなかった = 残りがない）。
                break;
            }
        }

        return totalDeleted;
    }
}
