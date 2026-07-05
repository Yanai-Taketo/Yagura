namespace Yagura.Storage;

/// <summary>
/// provider の統計情報（database.md §1.2 契約 6「統計」）。
/// </summary>
/// <param name="RecordCount">保存件数（<see cref="LogRecord"/> の総件数）。</param>
/// <param name="DatabaseSizeBytes">
/// DB サイズ（バイト）。取得できない場合は <c>null</c>（明示的な取得不能。
/// database.md §1.2「またはサイズ取得不能の明示」）。SQL Server provider には
/// この「取得不能」の逃げ道を適用しない（§5.3。M5-3 で実体化）。
/// </param>
/// <param name="DatabaseSizeUnavailableReason">
/// <paramref name="DatabaseSizeBytes"/> が <c>null</c> の場合の理由（人間可読）。
/// </param>
/// <param name="WalSizeBytes">
/// WAL ファイルサイズ（バイト）。WAL を用いない provider・WAL が存在しない場合は <c>null</c>
/// （database.md §4「WAL 肥大監視の入力」）。
/// </param>
public sealed record LogStoreStatistics(
    long RecordCount,
    long? DatabaseSizeBytes,
    string? DatabaseSizeUnavailableReason = null,
    long? WalSizeBytes = null);
