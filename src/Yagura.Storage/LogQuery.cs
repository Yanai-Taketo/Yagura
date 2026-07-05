namespace Yagura.Storage;

/// <summary>
/// 対話的検索の条件（database.md §1.2 契約 4「対話的検索」）。
/// 条件はすべて任意——絞り込みの強制はしない（architecture.md §6「絞り込みの強制はしない」）。
/// </summary>
/// <param name="ReceivedAtFrom">受信時刻の下限（UTC。含む）。<c>null</c> は下限なし。</param>
/// <param name="ReceivedAtTo">受信時刻の上限（UTC。含む）。<c>null</c> は上限なし。</param>
/// <param name="SourceAddress">
/// 送信元アドレスの完全一致。DB-6（対話的検索の一致規則）確定までの暫定として、
/// 送信元は完全一致のみを対象列とする（自由文検索の対象は <paramref name="SearchText"/> に限る）。
/// </param>
/// <param name="Severity">重大度の完全一致。<c>null</c> は条件なし。</param>
/// <param name="SearchText">
/// 自由文検索。<see cref="LogRecord.Message"/> に対する部分一致（大文字小文字を区別しない）とする
/// （DB-6 確定までの暫定規則。適合テストスイート整備時に database.md §1.2 へ固定する）。
/// </param>
/// <param name="Limit">結果件数の上限（必須。architecture.md §6・M-10）。</param>
/// <param name="Timeout">クエリの実行時間上限（必須。超過時は例外を送出する）。</param>
/// <param name="MessageProjectionLength">
/// 一覧用の軽量射影として返す <see cref="LogRecordSummary.Message"/> の先頭文字数
/// （database.md §2.1「一覧は先頭 N 文字の射影」。M-10 の仮値 200 を暫定使用）。
/// </param>
public sealed record LogQuery(
    int Limit,
    TimeSpan Timeout,
    DateTimeOffset? ReceivedAtFrom = null,
    DateTimeOffset? ReceivedAtTo = null,
    string? SourceAddress = null,
    int? Severity = null,
    string? SearchText = null,
    // 既定値はここに直接リテラルで書く必要がある（C# の言語仕様上、プライマリコンストラクタの
    // 既定パラメータ値はレコード本体で宣言する定数を前方参照できない）。値そのものの定義は
    // DefaultMessageProjectionLength を正とする——この既定値変更時は両方を同時に更新すること。
    int MessageProjectionLength = 200)
{
    /// <summary>
    /// 一覧射影の先頭文字数の既定値（M-10 の仮値 200。database.md §2.1）。
    /// </summary>
    public const int DefaultMessageProjectionLength = 200;
}
