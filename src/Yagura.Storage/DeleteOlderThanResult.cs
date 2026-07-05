namespace Yagura.Storage;

/// <summary>
/// <see cref="ILogStore.DeleteOlderThanAsync"/> の実行結果（database.md §1.2 契約 5・§3）。
/// </summary>
/// <param name="DeletedCount">削除した件数の合計（分割実行した全バッチの合計）。</param>
/// <param name="Cutoff">実際に適用した基準時刻（呼び出し時に渡した値をそのまま返す）。</param>
public sealed record DeleteOlderThanResult(long DeletedCount, DateTimeOffset Cutoff);
