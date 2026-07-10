namespace Yagura.Storage;

/// <summary>
/// カーソル（キーセット）ページングの位置（database.md §1.2「対話的検索」・DB-11）。
/// 「最後に表示した行」の複合キー <c>(ReceivedAt, Id)</c> を表す——並び順
/// <c>ORDER BY ReceivedAt DESC, Id DESC</c>（Issue #144）と同じキーで、複合索引
/// <c>IX_LogRecords_ReceivedAt_Id</c> に乗るシーク（keyset pagination）を実現する。
/// </summary>
/// <remarks>
/// <para>
/// <b>意味論</b>: <see cref="LogQuery.Cursor"/> に指定すると、<see cref="QueryAsync"/> は
/// このカーソルより<b>後（<c>ORDER BY ReceivedAt DESC, Id DESC</c> の意味で後ろ = より過去）</b>の
/// 行だけを返す——「(<see cref="ReceivedAt"/>, <see cref="Id"/>) より真に古い」行、すなわち
/// <c>ReceivedAt &lt; @ReceivedAt OR (ReceivedAt = @ReceivedAt AND Id &lt; @Id)</c> を満たす行。
/// カーソル自身の行・それより新しい行は含まない（重複を作らない）。
/// </para>
/// <para>
/// <b>OFFSET は使わない</b>: OFFSET はスキップ件数分を毎回スキャンする方式でありページが
/// 深くなるほど劣化する。本方式は複合索引の並びをそのまま辿るシーク法（keyset pagination）
/// のため、ページ番号に依存しない一定の性能特性を持つ。
/// </para>
/// <para>
/// <b>呼び出し側の使い方</b>: 直前の <see cref="ILogStore.QueryAsync"/> 結果の最終行（配列の末尾——
/// <c>ReceivedAt DESC, Id DESC</c> の並びでは「最も過去」の行）の
/// <c>(ReceivedAt, Id)</c> をそのまま次の呼び出しの <see cref="LogQuery.Cursor"/> に渡すことで
/// 「続きを読む」を実現する。<c>null</c>（既定）は先頭ページ（従来どおり最新から）を意味する。
/// </para>
/// </remarks>
/// <param name="ReceivedAt">最後に表示した行の <see cref="LogRecord.ReceivedAt"/>（UTC）。</param>
/// <param name="Id">最後に表示した行の <see cref="LogRecord.Id"/>（同一 ReceivedAt 内のタイブレーク軸）。</param>
public sealed record LogQueryCursor(DateTimeOffset ReceivedAt, long Id);
