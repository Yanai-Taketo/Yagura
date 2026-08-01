namespace Yagura.Web.Components.Common;

public static partial class UiText
{
    // ---- ログ検索（ui.md §4。M8-3） ----

    /// <summary>検索条件: 期間（開始）。{0} にサーバのタイムゾーンのオフセット表記が入る。</summary>
    public const string SearchFieldFromFormat = "期間の開始（サーバ時刻 {0}）";

    /// <summary>検索条件: 期間（終了）。{0} にサーバのタイムゾーンのオフセット表記が入る。</summary>
    public const string SearchFieldToFormat = "期間の終了（サーバ時刻 {0}）";

    /// <summary>
    /// 検索条件: 送信元アドレス（完全一致。DB-6 確定までの暫定規則）。IP アドレス指定であることを
    /// ラベルで明示する（逆引きホスト名では検索できない——ui.md §4 の誤解手当て）。
    /// </summary>
    public const string SearchFieldSource = "送信元 IP アドレス（完全一致。名前では検索できません）";

    /// <summary>検索条件: 重大度（用語対応表: severity → 重大度）。閾値方式。</summary>
    public const string SearchFieldSeverity = "重大度";

    /// <summary>
    /// 検索条件: 重大度欄の補足（完全一致ではなく閾値方式であることの明示。
    /// syslog は数値が小さいほど深刻なため、選択した重大度「以上」＝選択値と、より深刻な値を含む）。
    /// </summary>
    public const string SearchFieldSeverityHelp =
        "選択した重大度と、それより深刻な重大度をまとめて表示します（例:「3: エラー」を選ぶと、" +
        "0: 緊急・1: 警報・2: 重大・3: エラーを含みます）";

    /// <summary>検索条件: ファシリティ（用語対応表: facility → 分類（ファシリティ））。完全一致。</summary>
    public const string SearchFieldFacility = "分類（ファシリティ）";

    /// <summary>検索条件: 解析状態（「解析失敗だけを見たい」等の絞り込み）。</summary>
    public const string SearchFieldParseStatus = "解析状態";

    /// <summary>解析状態の選択肢: 解析済み。</summary>
    public const string ParseStatusOptionParsed = "解析済み";

    /// <summary>解析状態の選択肢: 解析失敗（<see cref="ParseFailedLabel"/> の短縮形）。</summary>
    public const string ParseStatusOptionParseFailed = "解析失敗（形式を解釈できなかったログ）";

    /// <summary>解析状態の選択肢: 不完全（<see cref="IncompleteLabel"/> の短縮形）。</summary>
    public const string ParseStatusOptionIncomplete = "不完全（切断により途中で途切れたログ）";

    /// <summary>検索条件: 本文の検索語（Message への部分一致）。</summary>
    public const string SearchFieldText = "本文の検索語（部分一致）";

    /// <summary>検索実行ボタン。</summary>
    public const string SearchButton = "検索";

    /// <summary>検索結果の CSV エクスポートボタン。</summary>
    public const string SearchExportCsvButton = "CSV エクスポート";

    /// <summary>
    /// CSV エクスポートの上限件数の注記（<c>{0}</c> に件数を埋め込む書式文字列）。画面表示（一覧）と
    /// 同じ上限を使うことを利用者に明示する（件数上限の明示）。
    /// </summary>
    public const string SearchExportCsvHintFormat = "現在の検索条件のまま、最大 {0:N0} 件まで CSV に出力します（一覧表示と同じ上限）。";

    /// <summary>
    /// 「さらに読み込む」ボタン（database.md DB-11。カーソルページングの追記型 UI）。
    /// 現在の検索条件のまま、表示済みの最後の行より過去の続きを追加で読み込む。
    /// </summary>
    public const string SearchLoadMoreButton = "さらに読み込む";

    /// <summary>
    /// 「さらに読み込む」実行中の表示（連打防止のためボタンを無効化する間のラベル）。
    /// </summary>
    public const string SearchLoadingMoreButton = "読み込み中…";

    // ---- ダッシュボード → 検索の導線（ui.md §4。無音化検出からの調査動線） ----

    /// <summary>送信元別受信状況テーブルの操作列見出し。</summary>
    public const string SourceColumnActions = "操作";

    /// <summary>送信元を条件にしたログ検索への導線ラベル。</summary>
    public const string SourceSearchLinkLabel = "ログを検索";

    /// <summary>検索結果 0 件（条件あり）の見出し。</summary>
    public const string SearchNoResultsTitle = "条件に一致するログがありません";

    /// <summary>検索結果 0 件（条件あり）の次の行動。</summary>
    public const string SearchNoResultsNextAction = "期間や条件を広げて、もう一度検索してください";

    /// <summary>ログ詳細の見出し。</summary>
    public const string DetailTitle = "ログの詳細";

    /// <summary>ログ詳細を閉じるボタン。</summary>
    public const string DetailClose = "閉じる";

    /// <summary>詳細対象のレコードが取得できなかった（削除済み等）場合の文言。</summary>
    public const string DetailNotFound = "このログは取得できませんでした（保持期間による削除などで既に存在しない可能性があります）";

    /// <summary>
    /// 解析失敗レコードの表示（用語対応表: 解析失敗（raw 保存） → 形式を解釈できなかったログ）。
    /// </summary>
    public const string ParseFailedLabel = "形式を解釈できなかったログ（原文のまま保存しています）";

    /// <summary>不完全レコード（TCP 切断による途中終端。database.md §2.1）の表示。</summary>
    public const string IncompleteLabel = "切断により途中で途切れたログ（原文のまま保存しています）";

    /// <summary>詳細: 受信時刻（基準軸。ui.md §6）。</summary>
    public const string DetailReceivedAt = "受信時刻（サーバ）";

    /// <summary>詳細: 送信元が名乗った時刻（参考情報。ui.md §6）。</summary>
    public const string DetailDeviceTimestamp = "送信元が名乗った時刻（参考）";

    /// <summary>
    /// 装置時計ずれの注記（ui.md §6）。{0} に乖離量（例: "5 時間"）が入る。
    /// 装置時刻がサーバ受信時刻より進んでいる場合。
    /// </summary>
    public const string DetailDeviceTimestampDriftAheadFormat = "装置時刻はサーバ受信時刻より約 {0} 進んでいます";

    /// <summary>
    /// 装置時計ずれの注記（ui.md §6）。{0} に乖離量（例: "5 時間"）が入る。
    /// 装置時刻がサーバ受信時刻より遅れている場合。
    /// </summary>
    public const string DetailDeviceTimestampDriftBehindFormat = "装置時刻はサーバ受信時刻より約 {0} 遅れています";

    /// <summary>
    /// 装置時計ずれ注記の補足。RFC 3164（タイムゾーン情報なし）
    /// 由来の DeviceTimestamp は解析時にタイムゾーンを UTC とみなす近似のため、乖離が時計のずれ
    /// だけでなく送信元のタイムゾーン設定の違いを表すこともあることを明示する。
    /// </summary>
    public const string DetailDeviceTimestampDriftSupplement =
        "タイムゾーン情報を持たない送信形式（RFC 3164 等）では、装置のローカル時刻をそのまま UTC とみなして比較しています。" +
        "この差には時計のずれだけでなく、タイムゾーン設定の違いが含まれる場合があります";

    /// <summary>詳細: 原文（受信したバイト列そのもの）。</summary>
    public const string DetailRaw = "受信した原文";

    /// <summary>詳細: メッセージ本文（オーバーレイの主役。M8-3 再デザイン）。</summary>
    public const string DetailMessage = "メッセージ";

    /// <summary>詳細: 構造化データ（RFC 5424 の STRUCTURED-DATA）。</summary>
    public const string DetailStructuredData = "構造化データ";
}
