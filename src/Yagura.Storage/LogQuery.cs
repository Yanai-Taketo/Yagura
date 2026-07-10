namespace Yagura.Storage;

/// <summary>
/// 対話的検索の条件（database.md §1.2 契約 4「対話的検索」）。
/// 条件はすべて任意——絞り込みの強制はしない（architecture.md §6「絞り込みの強制はしない」）。
/// </summary>
/// <param name="ReceivedAtFrom">受信時刻の下限（UTC。含む）。<c>null</c> は下限なし。</param>
/// <param name="ReceivedAtTo">受信時刻の上限（UTC。含む）。<c>null</c> は上限なし。</param>
/// <param name="SourceAddress">
/// 送信元アドレスの完全一致。database.md §1.2「自由文検索の一致規則（DB-6）」の対象列は
/// <see cref="LogRecord.Message"/> のみと確定しており、送信元等の他条件は常に完全一致とする
/// （自由文検索の対象は <paramref name="SearchText"/> に限る）。
/// </param>
/// <param name="SeverityAtMost">
/// 重大度の閾値（Issue #148）。syslog の重大度は<b>数値が小さいほど深刻</b>（0 = 緊急〜7 = デバッグ）
/// であるため、「N 以上の重大度」という運用語彙は数値としては<b>「Severity が N 以下」</b>に
/// 対応する——<paramref name="SeverityAtMost"/> を満たすレコードは <c>Severity &lt;= SeverityAtMost</c>
/// のもの（指定値そのものより深刻、または同じ深刻度をすべて含む）。<c>null</c> は条件なし。
/// Severity が未設定（PRI 自体を解析できなかった行）は対象外——それらを明示的に拾うには
/// <paramref name="ParseStatus"/> を使う。
/// <b>完全一致ではなく閾値方式を採る理由</b>: 完全一致では「エラー以上」を意図して
/// 「3: エラー」を選んでも Severity = 3 の行しか返らず、より深刻な緊急・警報・重大（0〜2）が
/// 結果から消える実害があった（Issue #148 の症状）。
/// </param>
/// <param name="Facility">
/// syslog PRI の facility の完全一致（Issue #148）。<c>null</c> は条件なし。
/// PRI 自体を解析できなかった行（Facility が未設定）は対象外。
/// </param>
/// <param name="ParseStatus">
/// <see cref="Yagura.Storage.ParseStatus"/> の完全一致（Issue #148）。<c>null</c> は条件なし。
/// 「解析失敗だけを見る」等の絞り込みに使う——Severity 系の条件は Severity が
/// <see langword="null"/> の行（解析失敗の典型）を常に除外するため、本条件がその唯一の手段となる。
/// </param>
/// <param name="SearchText">
/// 自由文検索。<see cref="LogRecord.Message"/> に対する部分一致（大文字小文字を区別しない）とする
/// （database.md §1.2「自由文検索の一致規則（DB-6）」。2026-07-09 オーナー決定で規則は確定済み。
/// ASCII・非 ASCII とも両 provider で blocking として満たす——SQL Server は database.md §5.4 の
/// 列 COLLATE、SQLite は DB-9 の性能実測（2026-07-10）を経たアプリ定義比較関数
/// （<see cref="Yagura.Storage.Sqlite.SqliteLogStore"/>）で実装済み）。
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
    int? SeverityAtMost = null,
    int? Facility = null,
    ParseStatus? ParseStatus = null,
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
