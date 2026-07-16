namespace Yagura.Storage;

/// <summary>
/// 一括読み出し操作（database.md §1.2「契約拡張の予約 (a)」の実体化。§6.2 蓄積ログ移行・
/// Issue #266）。<see cref="ILogStore"/> 本体には載せない optional 契約——対話検索の防御
/// （上限・タイムアウトの必須化）を**適用しない**代わりに、呼び出し経路を管理操作
/// （移行・エクスポート）に限定する（予約 (a) の条件どおり。閲覧経路から到達させない）。
/// provider が対応するかは <c>store is IBulkLogReader</c> の capability 検出で判定する。
/// </summary>
public interface IBulkLogReader
{
    /// <summary>
    /// 全ログレコードを古い順（<c>ReceivedAt ASC, Id ASC</c>）にストリーミング読み出しする。
    /// </summary>
    /// <param name="resumeAfter">
    /// 再開点（このカーソル**より後**から読む。<see langword="null"/> は先頭から）。
    /// 中断・再開の安全性（database.md §6.2 要件③）は「カーソル以前は読み出し済み」という
    /// 単調性で担保する——再開時に直前バッチを重複して読む可能性は許容する（at-least-once。
    /// 検証（要件②）が差分を重複として説明する前提）。
    /// </param>
    IAsyncEnumerable<LogRecord> ReadAllAscendingAsync(
        BulkReadCursor? resumeAfter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定範囲（<paramref name="toInclusive"/> 以前）のレコード件数を数える
    /// （移行の完全性検証——database.md §6.2 要件②「ReceivedAt 範囲での件数突合」——用）。
    /// <see langword="null"/> は上限なし（全件）。
    /// </summary>
    Task<long> CountAsync(DateTimeOffset? toInclusive, CancellationToken cancellationToken = default);
}

/// <summary>一括読み出しの再開カーソル（<c>ReceivedAt ASC, Id ASC</c> の複合キー位置）。</summary>
/// <param name="ReceivedAt">最後に読み出したレコードの受信時刻。</param>
/// <param name="Id">最後に読み出したレコードの Id（同時刻内のタイブレーク）。</param>
public sealed record BulkReadCursor(DateTimeOffset ReceivedAt, long Id);
