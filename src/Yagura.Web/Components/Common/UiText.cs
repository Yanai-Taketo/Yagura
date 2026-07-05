namespace Yagura.Web.Components.Common;

/// <summary>
/// 共通コンポーネントの画面文言カタログ（ui.md §7 用語対応表・文言原則の実装位置。M8-2）。
/// </summary>
/// <remarks>
/// <para>
/// <b>文言のリソース分離（ui.md §7.1）の v0.1 実装形</b>: 画面文言を部品の実装から
/// 本クラスへ分離する。v0.1 の提供言語は日本語のみのため .resx 化はせず、
/// 将来の英語化時に本クラスを resx 移行の単一起点とする（部品側は本クラス経由でのみ
/// 文言を参照し、文言リテラルを持たない）。
/// </para>
/// <para>
/// 文言は ui.md §7.2 用語対応表に従う（開発用語を画面に出さない。1 対 1 対応）。
/// 新しい開発用語を画面に導入する場合は、対応表への追記を同じ PR に含める（同 §7.1）。
/// </para>
/// </remarks>
public static class UiText
{
    // ---- 状態帯（ui.md §3.1 状態帯・§5.1） ----

    /// <summary>状態帯: 正常（state-ok）の見出し。</summary>
    public const string StatusBandOkTitle = "稼働中";

    /// <summary>状態帯: 警告（state-warning）の見出し。</summary>
    public const string StatusBandWarningTitle = "警告あり";

    /// <summary>状態帯: 異常（state-error）の見出し。</summary>
    public const string StatusBandErrorTitle = "異常あり";

    /// <summary>
    /// 状態帯: 正常時の既定サマリ（ui.md §5.1 の確定文言）。観測できる範囲に限って
    /// 言い切る——対象（サーバに届いたログ）と時間（現在 = 観測窓内）の両方を限定する。
    /// </summary>
    public const string StatusBandOkSummary = "現在、サーバに届いたログの取りこぼしは発生していません";

    /// <summary>
    /// 状態帯: 全送信元合算の最終受信時刻のラベル（ui.md §5.1——「稼働中」はポートの
    /// 待ち受けを意味し、ログが現に届いていることは意味しないため併記する）。
    /// </summary>
    public const string StatusBandLastReceivedLabel = "最終受信";

    /// <summary>状態帯: 送信元別の受信状況への導線の文言（ui.md §5.1）。</summary>
    public const string StatusBandSourcesLinkText = "送信元別の受信状況";

    // ---- 最終更新時刻とステール警告（ui.md §5.2） ----

    /// <summary>最終更新時刻のラベル。</summary>
    public const string LastUpdatedLabel = "最終更新";

    /// <summary>
    /// ステール警告の本文（ui.md §5.2 の確定文言。サーバの状態が確認できない場合）。
    /// {0} に最終更新時刻が入る。クライアント側 JS が自律表示するため、観測できる事実
    /// （表示が古い・サーバの状態を確認できない）だけを言い、受信への影響を必ず含める。
    /// </summary>
    public const string StaleWarningTitleFormat = "表示が古くなっています（最終更新: {0}）";

    /// <summary>ステール警告の補足（ui.md §5.2——受信への影響を観測できる範囲で言う）。</summary>
    public const string StaleWarningBody = "サーバの状態を確認できないため、ログの受信状況も不明です";

    // ---- 欠けているデータの明示（ui.md §5.3） ----

    /// <summary>受信断区間（用語対応表: 受信断 → 受信できなかった時間帯）。</summary>
    public const string MissingDataOutage = "受信できなかった時間帯";

    /// <summary>
    /// クラッシュ由来の近似断点の注記（ui.md §5.3——近似であることを印す）。
    /// </summary>
    public const string MissingDataOutageApproximateNote =
        "サーバが正常に終了しなかったため、この時間帯の境界はおおよその値です";

    /// <summary>保持地平（ui.md §5.3 の確定文言）。</summary>
    public const string MissingDataRetentionHorizon = "この範囲は保持期間外（削除済み）です";

    /// <summary>
    /// 検索の打ち切り（ui.md §3.1 テーブル規約——件数と共に明示、§5.3——条件を絞る案内と共に）。
    /// {0} に表示済み件数が入る。
    /// </summary>
    public const string MissingDataTruncatedFormat =
        "結果が上限に達したため、{0} 件で打ち切りました。期間や条件を絞ると続きを確認できます";

    /// <summary>
    /// 保持期間の常時明示（ui.md §5.3 の確定文言）。{0} に保持日数が入る。
    /// 用語対応表: 保持期間 → ログを保存しておく期間（本文言は §5.3 の確定形をそのまま使う）。
    /// </summary>
    public const string RetentionNoticeFormat = "{0} 日より古いログは自動的に削除されます";

    // ---- 設定・システム由来の警告と案内（ui.md §5.4） ----

    /// <summary>
    /// 一時保管（スプール退避）中の補足文言（ui.md §5.4 の確定文言。
    /// 用語対応表: スプール退避 → 一時保管への退避。色 + アイコンとセットで常時表示する）。
    /// </summary>
    public const string SpoolEvacuationNotice = "ログの保存が追いついていません。一時保管領域に退避しています";

    /// <summary>
    /// 昇格案内の閲覧画面向け文言（ui.md §5.4 の確定文言。操作を含めない——閲覧画面の通知には
    /// サーバ状態を変更する操作を置かない。§4 の不変条件）。
    /// </summary>
    public const string PromotionSuggestionViewer =
        "ログの保存が受信に追いついていません。保存先を SQL Server に切り替えることで改善が見込めます" +
        "（切り替えと案内の抑制はサーバ上の設定画面から行えます）";

    // ---- フォーム（ui.md §3.1） ----

    /// <summary>必須項目の表記（記号 * だけにしない。ui.md §3.1 フォーム規約）。</summary>
    public const string FormRequiredMark = "必須";

    // ---- 確認ダイアログ（ui.md §3.1） ----

    /// <summary>確認ダイアログのキャンセルボタン（既定フォーカスは安全側 = こちらに置く）。</summary>
    public const string ConfirmDialogCancel = "キャンセル";

    // ---- テーブル（ui.md §3.1） ----

    /// <summary>ページャの表示件数ラベル。</summary>
    public const string TableRowsPerPage = "1 ページの表示件数:";

    /// <summary>ページャの件数表示形式（MudTablePager の InfoFormat）。</summary>
    public const string TablePagerInfoFormat = "{first_item}-{last_item} 件 / 全 {all_items} 件";

    /// <summary>行の詳細表示ボタンの読み上げラベル（キーボード・支援技術向け。ui.md §8）。</summary>
    public const string TableRowDetailLabel = "詳細を表示";

    // ---- コピー可能フィールド（ui.md §3.1 空状態） ----

    /// <summary>コピーボタンの読み上げラベルの形式。{0} にフィールドのラベルが入る。</summary>
    public const string CopyButtonLabelFormat = "{0}をコピー";

    /// <summary>コピー成功の通知。</summary>
    public const string CopySucceeded = "コピーしました";

    /// <summary>コピー失敗時の案内（クリップボードが使えない環境向け）。</summary>
    public const string CopyFailed = "コピーできませんでした。値を選択して手動でコピーしてください";

    // ---- 画面名・ナビゲーション（ui.md §4。M8-3） ----

    /// <summary>ダッシュボード画面名。</summary>
    public const string NavDashboard = "ダッシュボード";

    /// <summary>ログ検索画面名。</summary>
    public const string NavSearch = "ログ検索";

    /// <summary>システム状態画面名。</summary>
    public const string NavStatus = "システム状態";

    /// <summary>左ナビゲーションの読み上げラベル（ui.md §8）。</summary>
    public const string NavAriaLabel = "画面一覧";

    // ---- 状態帯の判定理由サマリ（ui.md §5.1。M8-3。YaguraHealthReason に 1 対 1 対応） ----

    /// <summary>異常あり: 観測窓内の取りこぼし（用語対応表: ドロップ/破棄 → 取りこぼし）。</summary>
    public const string HealthReasonLoss = "直近でログの取りこぼしが発生しました";

    /// <summary>警告あり: スプール退避の継続（用語対応表: スプール退避 → 一時保管への退避）。</summary>
    public const string HealthReasonSpoolEvacuation = "ログの保存が追いつかず、一時保管へ退避しています";

    /// <summary>警告あり: スプール使用量の上限接近。</summary>
    public const string HealthReasonSpoolNearLimit = "一時保管領域の空きが少なくなっています";

    /// <summary>警告あり: スプールなし縮退運転（用語対応表: 縮退運転 → 一部機能を停止して動作中）。</summary>
    public const string HealthReasonSpoolDegraded = "一時保管領域が使えないため、一部機能を停止して動作中です";

    // ---- ステール警告の出し分け（ui.md §5.2。circuit 生存中の文言は M8-3 ダッシュボードが担う） ----

    /// <summary>
    /// circuit 生存中に更新だけが止まった局面の文言（ui.md §5.2 の確定文言）。
    /// 「受信は継続しています」と言い切ってよいのは §7.3 の再接続中に限る——ここでは言わない。
    /// </summary>
    public const string StaleWhileConnectedNotice =
        "画面とサーバの接続は維持されていますが、表示の更新が止まっています。" +
        "ログの受信状況はこの画面から確認できません";

    // ---- 保持期間（ui.md §5.3・database.md §3。M8-3） ----

    /// <summary>
    /// 保持期間が未適用（不正値フォールバック = 削除しない。database.md §3）の場合の常時明示。
    /// </summary>
    public const string RetentionDisabledNotice =
        "古いログの自動削除は現在行われません（ログを保存しておく期間が設定されていないか、設定値が無効です）";

    // ---- ダッシュボード（ui.md §4。M8-3） ----

    /// <summary>受信量推移カードの見出し。</summary>
    public const string VolumeChartTitle = "受信量の推移（直近 1 時間）";

    /// <summary>
    /// 受信量推移の導出が取得上限で打ち切られた場合の注記。{0} に取得件数が入る
    /// （既存の検索 API からの導出範囲——ui.md §4 実装参照の注記）。
    /// </summary>
    public const string VolumeChartTruncatedFormat =
        "受信量が多いため、推移は取得できた範囲（最新 {0} 件）のみ表示しています";

    /// <summary>時間軸チャートで期間内の受信が 0 件の場合の注記。</summary>
    public const string TimelineNoData = "この期間に受信したログはありません";

    /// <summary>送信元別受信状況カードの見出し（ui.md §5.1 の導線の行き先。UI-4）。</summary>
    public const string SourcesTitle = "送信元別の受信状況（最終受信が古い順）";

    /// <summary>送信元別: 送信元列。</summary>
    public const string SourceColumnAddress = "送信元";

    /// <summary>送信元別: 最終受信時刻列。</summary>
    public const string SourceColumnLastReceived = "最終受信";

    /// <summary>送信元別: 無音時間列（開発用語「無音化」を画面に出さない言い換え）。</summary>
    public const string SourceColumnSilence = "最後に受信してからの経過時間";

    /// <summary>送信元別: 件数列。</summary>
    public const string SourceColumnCount = "保存件数";

    /// <summary>
    /// 送信元一覧が上限で打ち切られた場合の注記。{0} に表示件数が入る（切り捨てられるのは
    /// 最近まで受信できている送信元側——ILogStore.QuerySourceActivityAsync の契約）。
    /// </summary>
    public const string SourcesTruncatedFormat =
        "送信元が多いため、最終受信時刻の古い順に {0} 件まで表示しています";

    /// <summary>ログ未着の空状態の見出し（ui.md §3.1 空状態）。</summary>
    public const string NoLogsEmptyTitle = "まだログがありません";

    /// <summary>
    /// ログ未着の空状態の次の行動（ui.md §3.1——機器設定 → 最初の 1 件、の 30 分動線の続き）。
    /// </summary>
    public const string NoLogsEmptyNextAction =
        "送信元機器の syslog 送信先に、このサーバの IP アドレスと下記のポート番号を設定してください";

    /// <summary>受信ポートのコピー可能表示のラベル形式。{0} にプロトコル名が入る。</summary>
    public const string ListenerPortLabelFormat = "{0} 受信ポート";

    /// <summary>ダッシュボードの現在値カード: 一時保管領域の使用量。</summary>
    public const string StatSpoolUsage = "一時保管領域の使用量";

    /// <summary>ダッシュボードの現在値カード: 一時保管への退避（累計）。</summary>
    public const string StatSpoolEvacuated = "一時保管への退避（累計）";

    /// <summary>ダッシュボードの現在値カード: 取りこぼし（累計。破棄系カウンタの合計）。</summary>
    public const string StatLossTotal = "取りこぼし（累計）";

    /// <summary>ダッシュボードの現在値カード: 保存済みログ件数。</summary>
    public const string StatStoredRecords = "保存済みログ件数";

    /// <summary>ダッシュボードから状態画面への導線。</summary>
    public const string StatLinkToStatus = "すべてのカウンタ・記録を見る（システム状態）";

    /// <summary>スプールが使えない（縮退・無効）場合の現在値カードの値表示。</summary>
    public const string StatSpoolUnavailable = "利用できません";

    // ---- ログ検索（ui.md §4。M8-3） ----

    /// <summary>検索条件: 期間（開始）。{0} にサーバのタイムゾーンのオフセット表記が入る。</summary>
    public const string SearchFieldFromFormat = "期間の開始（サーバ時刻 {0}）";

    /// <summary>検索条件: 期間（終了）。{0} にサーバのタイムゾーンのオフセット表記が入る。</summary>
    public const string SearchFieldToFormat = "期間の終了（サーバ時刻 {0}）";

    /// <summary>検索条件: 送信元アドレス（完全一致。DB-6 確定までの暫定規則）。</summary>
    public const string SearchFieldSource = "送信元アドレス（完全一致）";

    /// <summary>検索条件: 重大度（用語対応表: severity → 重大度）。</summary>
    public const string SearchFieldSeverity = "重大度";

    /// <summary>検索条件: 本文の検索語（Message への部分一致）。</summary>
    public const string SearchFieldText = "本文の検索語（部分一致）";

    /// <summary>検索実行ボタン。</summary>
    public const string SearchButton = "検索";

    /// <summary>重大度の選択肢（RFC 5424 の 0〜7 に 1 対 1 対応。添字 = severity 値）。</summary>
    public static readonly IReadOnlyList<string> SeverityOptionLabels =
    [
        "0: 緊急 (Emergency)",
        "1: 警報 (Alert)",
        "2: 重大 (Critical)",
        "3: エラー (Error)",
        "4: 警告 (Warning)",
        "5: 通知 (Notice)",
        "6: 情報 (Informational)",
        "7: デバッグ (Debug)",
    ];

    /// <summary>選択入力の「指定なし」選択肢（絞り込みの強制はしない——architecture.md §6）。</summary>
    public const string SelectNoneOption = "（指定なし）";

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

    /// <summary>詳細: 原文（受信したバイト列そのもの）。</summary>
    public const string DetailRaw = "受信した原文";

    // ---- システム状態（ui.md §4。M8-3） ----

    /// <summary>カウンタ一覧カードの見出し。</summary>
    public const string CountersTitle = "各種カウンタ（累計）";

    /// <summary>カウンタ一覧: 項目列（平易語）。</summary>
    public const string CounterColumnName = "項目";

    /// <summary>カウンタ一覧: 識別子列（開発用語側のキー。試用報告と設計文書の突合用——ui.md §4）。</summary>
    public const string CounterColumnId = "識別子";

    /// <summary>カウンタ一覧: 値列。</summary>
    public const string CounterColumnValue = "値";

    /// <summary>カウンタ平易語: 内部バッファ破棄（1 対 1 対応。ui.md §7.2）。</summary>
    public const string CounterInternalBufferDropped = "取りこぼし（サーバ内の処理待ちが満杯）";

    /// <summary>カウンタ平易語: TCP 接続拒否。</summary>
    public const string CounterTcpConnectionRejected = "同時接続の上限で受け付けなかった接続";

    /// <summary>カウンタ平易語: スプール退避。</summary>
    public const string CounterSpoolEvacuated = "一時保管への退避（取りこぼしではありません）";

    /// <summary>カウンタ平易語: スプール書込失敗。</summary>
    public const string CounterSpoolWriteFailed = "取りこぼし（一時保管への保存失敗）";

    /// <summary>カウンタ平易語: スプール破棄。</summary>
    public const string CounterSpoolDiscarded = "取りこぼし（一時保管が満杯）";

    /// <summary>カウンタ平易語: 永続化失敗。</summary>
    public const string CounterPersistenceFailed = "取りこぼし（保存の失敗）";

    /// <summary>カウンタ平易語: 流量制御破棄。</summary>
    public const string CounterFlowControlDropped = "取りこぼし（受信量の制限。現在この機能は無効です）";

    /// <summary>未知の計器名のフォールバック表示（新カウンタ追加時の平易語未登録を隠さない）。</summary>
    public const string CounterUnknown = "（対応表未登録の項目）";

    /// <summary>ゲージ一覧カードの見出し。</summary>
    public const string GaugesTitle = "現在の状態";

    /// <summary>ゲージ: 保存先（データベース）の使用量（用語対応表: provider → 保存先（データベース））。</summary>
    public const string GaugeDatabaseSize = "保存先（データベース）の使用量";

    /// <summary>ゲージ: DB サイズ取得不能時の表示。</summary>
    public const string GaugeDatabaseSizeUnavailable = "取得できません";

    /// <summary>
    /// OS 受信破棄ゲージの常時説明（M8-3 の設計判断: 値を表示せず説明のみを常時掲示する。
    /// architecture.md §4.2・D-6——値 0 の表示が「取りこぼしゼロ」の誤解を生むため。
    /// 判断記録は ui.md §5.5）。
    /// </summary>
    public const string OsUdpGaugeExplanation =
        "OS がこのアプリへ渡す前に破棄した受信データの数（OS の統計値）は、この画面に表示していません";

    /// <summary>OS 受信破棄ゲージの常時説明の補足（理由と代替手段）。</summary>
    public const string OsUdpGaugeExplanationSupplement =
        "検証済みの Windows 環境では、この OS 統計は受信・破棄のどちらも計上しないことが実測で確認されています。" +
        "0 という値を表示すると「取りこぼしなし」という誤解を生むため、値の表示自体を行いません。" +
        "取りこぼしの確認には、上記のカウンタと、ダッシュボードの送信元別の受信状況（最終受信時刻）をあわせて確認してください";

    /// <summary>受信断履歴カードの見出し（用語対応表: 受信断 → 受信できなかった時間帯）。</summary>
    public const string OutageHistoryTitle = "受信できなかった時間帯の履歴";

    /// <summary>受信断履歴: 正常停止由来の種別表示。</summary>
    public const string OutageKindNormalStop = "停止・再起動による";

    /// <summary>受信断履歴: クラッシュ近似断点の種別表示（近似である旨を含む。ui.md §5.3）。</summary>
    public const string OutageKindCrashApproximate = "正常に終了しなかったため境界はおおよそ";

    /// <summary>履歴テーブル: 種別列。</summary>
    public const string HistoryColumnKind = "種別";

    /// <summary>履歴テーブル: 開始列。</summary>
    public const string HistoryColumnStart = "開始";

    /// <summary>履歴テーブル: 終了列。</summary>
    public const string HistoryColumnEnd = "終了";

    /// <summary>履歴テーブル: 付帯情報列。</summary>
    public const string HistoryColumnDetails = "付帯情報";

    /// <summary>通知履歴・動作記録カードの見出し。</summary>
    public const string EventHistoryTitle = "通知・動作の記録";

    /// <summary>動作記録: 保持期間削除の実行記録の種別表示。</summary>
    public const string EventKindRetentionDelete = "古いログの自動削除を実行";

    /// <summary>動作記録: 未知の種別のフォールバック表示。{0} に Kind の生値が入る。</summary>
    public const string EventKindUnknownFormat = "その他の記録（{0}）";

    /// <summary>
    /// 通知の記録先の案内（architecture.md §4.6——能動通知は Windows イベントログが既定の書き出し先）。
    /// </summary>
    public const string EventLogNote =
        "サーバからの能動的な通知（警告）は Windows イベントログにも記録されます。" +
        "この画面の記録は保存先（データベース）に残された動作の記録です";

    /// <summary>履歴が 1 件もない場合の表示。</summary>
    public const string HistoryEmpty = "まだ記録がありません";
}
