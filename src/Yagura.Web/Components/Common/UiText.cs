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

    /// <summary>
    /// 保持地平（ui.md §5.3）。検索範囲の下限が保持期間より古いときに出す。
    /// 「表示中の結果そのものが消えた」という誤読（2026-07-06 試用フィードバック——条件なしの
    /// 初期表示で結果が並んでいる上にこの注記が出ると「消えた?」と読める）を避けるため、
    /// 削除の対象が<b>保持期間より前の古いログ</b>であることと、<b>残っているログは表示されている</b>
    /// ことの両方を言い切る。
    /// </summary>
    public const string MissingDataRetentionHorizon = "保持期間より前のログは自動的に削除済みです（これより古いログは残っていません）";

    /// <summary>
    /// 検索の打ち切り（ui.md §3.1 テーブル規約——件数と共に明示、§5.3——条件を絞る案内と共に）。
    /// {0} に表示済み件数が入る。**database.md DB-11（カーソルページング）の追加後**は
    /// 「さらに読み込む」ボタン（<see cref="SearchLoadMoreButton"/>）で続きを取得できるため、
    /// 文言もその選択肢を案内する（従来の「期間や条件を絞る」も引き続き有効な選択肢として残す）。
    /// <b>{0} は「1 回のクエリの上限」ではなく「現在までに読み込んだ累計件数」</b>
    /// （<c>YaguraTable.Items.Count</c>。「さらに読み込む」を繰り返すと増える）である点に注意——
    /// 文言はこの値を「一度に取得できる上限」と誤読させない表現にする。
    /// </summary>
    public const string MissingDataTruncatedFormat =
        "現在 {0} 件を表示しています。まだ続きがあります——「さらに読み込む」で続きを確認するか、期間や条件を絞ってください";

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

    /// <summary>ブロック版コピー部品（YaguraCopyBlock）のボタン文言。</summary>
    public const string CopyBlockButton = "コピー";

    // ---- ボタン（ui.md §3.1） ----

    /// <summary>
    /// ボタンのクリック処理から漏れた例外の共通表示形式。{0} に例外メッセージが入る。
    /// 第一の受け皿は各画面の catch（画面固有の文言・誘導つき）であり、これは例外を
    /// circuit エラーにしないための最後の受け皿（Issue #372）。
    /// </summary>
    public const string ButtonActionFailedFormat = "操作を完了できませんでした: {0}";

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

    /// <summary>時間軸チャート中央の操作案内（棒ホバーで時間帯 + 件数が出る旨。2026-07-06）。</summary>
    public const string TimelineHoverHint = "棒にカーソルを合わせると、その時間帯と件数が表示されます";

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

    /// <summary>
    /// 一時保管への退避カードの補足（退避が現在も進行中の場合。Issue #132）。累計値は監査上の
    /// 価値があるため残しつつ、「今」の状態が復帰したことを見分けられるようにする
    /// （ui.md §5.4「一時保管中の表示」の裏返し——進行中でなくなったら静かに戻す）。
    /// </summary>
    public const string StatSpoolEvacuatedOngoingSupplement = "現在も一時保管への退避が進行中です";

    /// <summary>
    /// 一時保管への退避カードの補足（過去に退避があったが、現在は消化完了（DB 格納済み）の場合。
    /// Issue #132。「一生画面に表示されっぱなし」という誤解を防ぐ——累計は過去分であり、
    /// 現在は正常に戻っていることを明示する）。
    /// </summary>
    public const string StatSpoolEvacuatedResolvedSupplement = "退避分は格納済み（現在は正常です）";

    /// <summary>ダッシュボードの現在値カード: 取りこぼし（累計。破棄系カウンタの合計）。</summary>
    public const string StatLossTotal = "取りこぼし（累計）";

    /// <summary>
    /// 取りこぼしカードの補足（取りこぼしがある場合のみ表示）。{0} にこの画面を開いてからの増分。
    /// 累計値は保存件数と並ぶと「大半を捨てている」ように見える（2026-07-06 試用フィードバック——
    /// 累計 37,529 対 保存 736）。累計はサーバ起動からの running total であること、および
    /// 「今も増えているか（＝進行中か過去か）」を開いてからの増分で示し、過去の一時的な取りこぼしと
    /// 現在進行中の取りこぼしを見分けられるようにする。
    /// </summary>
    public const string StatLossTotalSupplementFormat = "サーバ起動からの累計。この画面を開いてからは +{0} 件";

    /// <summary>ダッシュボードの現在値カード: 保存済みログ件数。</summary>
    public const string StatStoredRecords = "保存済みログ件数";

    /// <summary>ダッシュボードから状態画面への導線。</summary>
    public const string StatLinkToStatus = "すべてのカウンタ・記録を見る（システム状態）";

    // ---- 重大度分布・Top talkers（ui.md §4。M8-5/Issue #159） ----

    /// <summary>重大度分布カードの見出し。</summary>
    public const string SeverityDistributionTitle = "重大度別の受信件数（直近 1 時間）";

    /// <summary>
    /// 重大度分布で PRI が解析できなかった（severity 不明）バケットのラベル
    /// （解析失敗の事実を隠さない——ui.md §5.3 と同じ向き）。
    /// </summary>
    public const string SeverityDistributionUnparsedLabel = "重大度不明（解析できなかったログ）";

    /// <summary>重大度分布・Top talkers ともこの期間に受信がない場合の注記。</summary>
    public const string SeverityDistributionNoData = "この期間に受信したログはありません";

    /// <summary>
    /// 受信量上位の送信元（Top talkers）カードの見出し。既存の「送信元別の受信状況」
    /// （最終受信が古い順・無音化検出専用。UI-4）とは別の視点であることを見出しで明示する。
    /// </summary>
    public const string TopTalkersTitle = "受信量上位の送信元（直近 1 時間・上位 10）";

    /// <summary>Top talkers: 件数列（重大度分布と同じ列見出しの重複を避けるため独自形式）。</summary>
    public const string TopTalkersColumnCount = "受信件数（直近 1 時間）";

    /// <summary>Top talkers にこの期間の受信がない場合の注記。</summary>
    public const string TopTalkersNoData = "この期間に受信した送信元はありません";

    // ---- 流量制限の発火上位送信元（Issue #288。案 (a) カード型——2026-07-18 オーナー裁定） ----

    /// <summary>流量制限の発火上位送信元カードの見出し。</summary>
    public const string FlowControlRejectionsTitle = "流量制限の発火上位送信元";

    /// <summary>
    /// カードの説明（UI-4「送信元別の受信状況」・Top talkers との住み分けを明示する——
    /// 受信量ではなく「制限に達した」という別の軸であること、および値の生存期間
    /// （ゲートの有界バケットと同寿命——起動からの累計ではない）。
    /// </summary>
    public const string FlowControlRejectionsDescription =
        "流量制御（送信元ごとの受信量の制限）によって破棄が発生した送信元を、破棄の多い順に表示します。" +
        "受信量の一覧（受信量上位の送信元・送信元別の受信状況）とは別の軸——「多く受信した」ではなく「制限に達した」送信元です。" +
        "数値は現在追跡中の値で、制限なく受信できる状態がしばらく続いた送信元は一覧から自然に消えます（破棄の総数は取りこぼしのカウンタが保持します）。";

    /// <summary>流量制限による破棄が発生していない場合の注記。</summary>
    public const string FlowControlRejectionsNoData = "流量制限による破棄は発生していません";

    /// <summary>流量制限の発火上位送信元: 破棄件数列。</summary>
    public const string FlowControlRejectionsColumnCount = "破棄件数";

    /// <summary>スプールが使えない（縮退・無効）場合の現在値カードの値表示。</summary>
    public const string StatSpoolUnavailable = "利用できません";

    // ---- ログ検索（ui.md §4。M8-3） ----

    /// <summary>検索条件: 期間（開始）。{0} にサーバのタイムゾーンのオフセット表記が入る。</summary>
    public const string SearchFieldFromFormat = "期間の開始（サーバ時刻 {0}）";

    /// <summary>検索条件: 期間（終了）。{0} にサーバのタイムゾーンのオフセット表記が入る。</summary>
    public const string SearchFieldToFormat = "期間の終了（サーバ時刻 {0}）";

    /// <summary>
    /// 検索条件: 送信元アドレス（完全一致。DB-6 確定までの暫定規則）。IP アドレス指定であることを
    /// ラベルで明示する（逆引きホスト名では検索できない——ADR-0007 決定 2・ui.md §4 の誤解手当て）。
    /// </summary>
    public const string SearchFieldSource = "送信元 IP アドレス（完全一致。名前では検索できません）";

    /// <summary>検索条件: 重大度（用語対応表: severity → 重大度）。閾値方式（Issue #148）。</summary>
    public const string SearchFieldSeverity = "重大度";

    /// <summary>
    /// 検索条件: 重大度欄の補足（Issue #148——完全一致ではなく閾値方式であることの明示。
    /// syslog は数値が小さいほど深刻なため、選択した重大度「以上」＝選択値と、より深刻な値を含む）。
    /// </summary>
    public const string SearchFieldSeverityHelp =
        "選択した重大度と、それより深刻な重大度をまとめて表示します（例:「3: エラー」を選ぶと、" +
        "0: 緊急・1: 警報・2: 重大・3: エラーを含みます）";

    /// <summary>検索条件: ファシリティ（用語対応表: facility → 分類（ファシリティ））。完全一致（Issue #148）。</summary>
    public const string SearchFieldFacility = "分類（ファシリティ）";

    /// <summary>検索条件: 解析状態（Issue #148——「解析失敗だけを見たい」等の絞り込み）。</summary>
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

    /// <summary>検索結果の CSV エクスポートボタン（Issue #157）。</summary>
    public const string SearchExportCsvButton = "CSV エクスポート";

    /// <summary>
    /// CSV エクスポートの上限件数の注記（<c>{0}</c> に件数を埋め込む書式文字列）。画面表示（一覧）と
    /// 同じ上限を使うことを利用者に明示する（「件数上限の明示」——Issue #157 の受け入れ条件）。
    /// </summary>
    public const string SearchExportCsvHintFormat = "現在の検索条件のまま、最大 {0:N0} 件まで CSV に出力します（一覧表示と同じ上限）。";

    /// <summary>
    /// 「さらに読み込む」ボタン（database.md DB-11。カーソルページングの追記型 UI。Issue #144 残課題）。
    /// 現在の検索条件のまま、表示済みの最後の行より過去の続きを追加で読み込む。
    /// </summary>
    public const string SearchLoadMoreButton = "さらに読み込む";

    /// <summary>
    /// 「さらに読み込む」実行中の表示（連打防止のためボタンを無効化する間のラベル）。
    /// </summary>
    public const string SearchLoadingMoreButton = "読み込み中…";

    // ---- 送信元の逆引きホスト名（ADR-0007。ui.md §4） ----

    /// <summary>
    /// 逆引きホスト名の由来ツールチップ（ui.md §4 の確定文言を正とする。送信元 IP を表示する
    /// 3 箇所すべてに共通で付与する——`YaguraSourceAddress` が内蔵）。
    /// </summary>
    public const string ReverseDnsTooltip =
        "送信元 IP アドレスから DNS の逆引きで取得した名前です。" +
        "機器自身が名乗るホスト名（ログに記載）とは別で、食い違うことがあります";

    /// <summary>
    /// 詳細表示のホスト名 / アプリ名ラベル（ui.md §4——逆引きホスト名との由来の違いを
    /// 「（ログに記載）」で明示する。用語対応表: syslog HOSTNAME → ホスト名（ログに記載））。
    /// </summary>
    public const string DetailHostnameAppLabel = "ホスト名（ログに記載） / アプリ名";

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

    /// <summary>
    /// 重大度の短形ラベル（一覧セル・チップ用。<see cref="SeverityOptionLabels"/> の長形と
    /// 添字で 1 対 1 対応——対応表を二重管理しない）。表示部品は
    /// <c>YaguraSeverityChip</c>（色の対応は ui.md §4 に記録）。
    /// </summary>
    public static readonly IReadOnlyList<string> SeverityShortLabels =
    [
        "0: 緊急",
        "1: 警報",
        "2: 重大",
        "3: エラー",
        "4: 警告",
        "5: 通知",
        "6: 情報",
        "7: デバッグ",
    ];

    /// <summary>
    /// 重大度の長形ラベル整形（詳細表示用）。範囲外の値は解釈を偽装せず生値のまま返す。
    /// </summary>
    public static string FormatSeverityLong(int? severity) => severity switch
    {
        null => "—",
        >= 0 and <= 7 => SeverityOptionLabels[severity.Value],
        _ => severity.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    // ---- ファシリティ（syslog PRI の facility。ui.md §4。2026-07-06 オーナー指示） ----

    /// <summary>
    /// ファシリティ番号 → 標準名の対応（RFC 5424 Table 1）。
    /// <b>16〜23（local0〜local7）は運用側が機器ごとに自由に使う枠</b>であり、標準名は
    /// 「local0」等の枠名しか与えられない——このため表示は常に<b>番号を主とし名前を補助</b>
    /// とする（<see cref="FormatFacility"/>。severity と違い運用依存が強く、番号こそが正）。
    /// 対応表に無い番号（範囲外・未割当）は名前を付けず番号のみ返す（解釈を偽装しない）。
    /// </summary>
    private static readonly IReadOnlyDictionary<int, string> FacilityNames = new Dictionary<int, string>
    {
        [0] = "カーネル",
        [1] = "ユーザー",
        [2] = "メール",
        [3] = "デーモン",
        [4] = "認証",
        [5] = "syslog 内部",
        [6] = "プリンタ",
        [7] = "ニュース",
        [8] = "UUCP",
        [9] = "cron",
        [10] = "認証(private)",
        [11] = "FTP",
        [12] = "NTP",
        [13] = "ログ監査",
        [14] = "ログ警告",
        [15] = "クロック",
        [16] = "local0",
        [17] = "local1",
        [18] = "local2",
        [19] = "local3",
        [20] = "local4",
        [21] = "local5",
        [22] = "local6",
        [23] = "local7",
    };

    /// <summary>
    /// ファシリティを「番号: 名前」で整形する（例: <c>3: デーモン</c>）。名前が無い番号は
    /// 番号のみ（例: <c>99</c>）。<see langword="null"/> は <c>—</c>。番号を主・名前を補助と
    /// する理由はクラス <see cref="FacilityNames"/> の注記参照。
    /// </summary>
    public static string FormatFacility(int? facility)
    {
        if (facility is not { } value)
        {
            return "—";
        }

        return FacilityNames.TryGetValue(value, out var name)
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{value}: {name}")
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

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
    /// 装置時計ずれの注記（ui.md §6・Issue #158）。{0} に乖離量（例: "5 時間"）が入る。
    /// 装置時刻がサーバ受信時刻より進んでいる場合。
    /// </summary>
    public const string DetailDeviceTimestampDriftAheadFormat = "装置時刻はサーバ受信時刻より約 {0} 進んでいます";

    /// <summary>
    /// 装置時計ずれの注記（ui.md §6・Issue #158）。{0} に乖離量（例: "5 時間"）が入る。
    /// 装置時刻がサーバ受信時刻より遅れている場合。
    /// </summary>
    public const string DetailDeviceTimestampDriftBehindFormat = "装置時刻はサーバ受信時刻より約 {0} 遅れています";

    /// <summary>
    /// 装置時計ずれ注記の補足（2026-07-09 Issue #158 の判断）。RFC 3164（タイムゾーン情報なし）
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

    // ---- 蓄積ログの移行（database.md §6.2。DB-5。Issue #266） ----

    /// <summary>システムイベント平易語: 蓄積ログ移行の完了記録。</summary>
    public const string EventKindMigrationImport = "蓄積ログの移行（移行されたログの期間）";

    /// <summary>移行セクションのタイトル。</summary>
    public const string MigrationSectionTitle = "蓄積ログの移行（旧 SQLite → SQL Server）";

    /// <summary>移行の説明。</summary>
    public const string MigrationDescription =
        "本番昇格前に SQLite へ保存されたログを、現在の SQL Server へ移送します。移行中も受信は継続します。" +
        "移行しない場合、旧ログは閲覧できないまま、旧データベースの処分で失われます。";

    /// <summary>移行の実行ボタン。</summary>
    public const string MigrationExecute = "移行を実行";

    /// <summary>移行の進捗書式（{0} = 移行済み件数・{1} = 総件数）。</summary>
    public const string MigrationProgressFormat = "移行中: {0:N0} / {1:N0} 件";

    /// <summary>移行の完了通知。</summary>
    public const string MigrationCompletedNotification = "蓄積ログの移行が完了しました";

    /// <summary>移行の検証不合格通知。</summary>
    public const string MigrationFailedNotification = "移行の完全性検証に不合格です（再実行で追補できます）";

    /// <summary>移行完了済みの表示。</summary>
    public const string MigrationAlreadyCompleted =
        "蓄積ログの移行は完了しています。旧データベースファイルの処分は処分手順に従ってください。";

    // ---- 設定の再読み込み（configuration.md §3。CF-4 層1。Issue #262） ----

    /// <summary>設定再読み込み画面のタイトル。</summary>
    public const string AdminReloadTitle = "設定の再読み込み";

    /// <summary>設定再読み込み画面の説明。</summary>
    public const string AdminReloadDescription =
        "手編集された設定ファイル（yagura.json）を読み直し、変更のうち即時反映できる項目を適用します。" +
        "反映にサービス再起動が必要な項目は、未反映のまま残る項目として下に表示されます。";

    /// <summary>再読み込みの実行ボタン。</summary>
    public const string AdminReloadExecute = "再読み込みを実行";

    /// <summary>再読み込みが検証失敗で拒否されたときの前置き。</summary>
    public const string AdminReloadRejected = "設定に不正があるため再読み込みを中止しました（実行中の構成は変更されていません）: ";

    /// <summary>変更なしの結果表示。</summary>
    public const string AdminReloadNoChanges = "設定ファイルに変更はありませんでした。";

    /// <summary>適用件数の書式（{0} = 適用キー数）。</summary>
    public const string AdminReloadAppliedFormat = "再読み込みを実行しました（即時反映: {0} 件）。";

    /// <summary>適用キー一覧のラベル。</summary>
    public const string AdminReloadAppliedKeys = "反映した項目";

    /// <summary>再起動待ちキーの前置き。</summary>
    public const string AdminReloadPendingRestart =
        "次の項目は反映にサービス再起動が必要なため、未反映のまま残っています:";

    /// <summary>検証警告の前置き。</summary>
    public const string AdminReloadWarnings = "設定値の警告（不正な値は既定値・安全側で継続しています）:";

    /// <summary>未知キーの前置き。</summary>
    public const string AdminReloadUnknownKeys = "認識されない設定キー（誤記の可能性）: ";

    /// <summary>
    /// 型の読み替え一覧の前置き（情報レベル——受理は正常系であり警告にしない。
    /// configuration.md §1。Issue #334）。
    /// </summary>
    public const string AdminReloadTypeCoercions = "文字列以外の JSON 型で書かれ、そのまま受理した設定キー（動作への影響はありません）:";

    /// <summary>
    /// 保存後の自動再読み込み（再起動待ちカードへの計上。Issue #388）が拒否されたときの通知。
    /// {0} に拒否理由が入る。保存自体は成立している。
    /// </summary>
    public const string AdminSaveReloadRejectedFormat =
        "保存は完了しましたが、再起動待ちへの計上（設定の再読み込み）が拒否されました: {0}。設定の再読み込み画面から手動で実行してください";

    /// <summary>再読み込み完了の通知。</summary>
    public const string AdminReloadCompletedNotification = "設定を再読み込みしました";

    /// <summary>再読み込み拒否の通知。</summary>
    public const string AdminReloadRejectedNotification = "設定の再読み込みを中止しました（設定値を確認してください）";

    // ---- ウィザード保存後の自動反映（Issue #287） ----

    /// <summary>保存後すぐ反映するチェックボックスのラベル（既定オン——2026-07-18 オーナー裁定）。</summary>
    public const string WizardAutoApplyLabel = "保存後すぐ反映する";

    /// <summary>保存後すぐ反映するチェックボックスの補足。</summary>
    public const string WizardAutoApplyHelp =
        "オンにすると、保存の直後に即時反映できる項目をライブ反映します（設定の再読み込みと同じ経路）。" +
        "オフにすると保存のみを行い、反映は再読み込み操作または再起動で行います（複数の変更をまとめてから一括反映する運用向け）。";

    /// <summary>自動反映の実行結果（{0} = 即時反映のキー数）。</summary>
    public const string WizardAutoApplyAppliedFormat = "保存後の自動反映を実行しました（即時反映: {0} 件）。";

    /// <summary>自動反映で反映が必要な変更がなかったときの表示。</summary>
    public const string WizardAutoApplyNoChanges = "保存後の自動反映: 反映が必要な変更はありませんでした。";

    /// <summary>自動反映が検証失敗で実行できなかったときの前置き（保存自体は完了している）。</summary>
    public const string WizardAutoApplyRejected =
        "保存は完了しましたが、自動反映は実行できませんでした（設定の検証失敗——実行中の構成は変更されていません）: ";

    // ---- 再起動待ちキーの常設表示（Issue #286。管理面のみ） ----

    /// <summary>再起動待ちカードのタイトル。</summary>
    public const string AdminPendingRestartCardTitle = "再起動待ちの設定変更";

    /// <summary>再起動待ちカードの説明。</summary>
    public const string AdminPendingRestartCardDescription =
        "設定の再読み込みで変更が検出されましたが、反映にサービス再起動が必要なため未反映のまま残っている項目です。" +
        "サービスを再起動すると反映され、この表示は消えます。";

    /// <summary>再起動待ちキーの検出時刻の前置きラベル（後ろに時刻表示が続く）。</summary>
    public const string AdminPendingRestartDetectedAtLabel = "検出した再読み込み: ";

    /// <summary>カウンタ平易語: 流量制御破棄（Issue #260 で判定・破棄が実装され実値を刻む）。</summary>
    public const string CounterFlowControlDropped = "取りこぼし（送信元ごとの受信量の制限）";

    /// <summary>
    /// カウンタ平易語: スプール末尾破損破棄（Issue #201）。他の取りこぼし系カウンタと異なり
    /// 単位がバイト（レコード単位では数えられないため）——値の桁が他行と並ばないことでの
    /// 誤解を避けるため、ラベル自体に単位を明記する。
    /// </summary>
    public const string CounterSpoolCorruptTailDiscarded = "取りこぼし（一時保管ファイルの末尾破損。単位はバイト）";

    /// <summary>カウンタ平易語: 逆引き解決成功（ADR-0007 決定 6。ui.md §7.2）。</summary>
    public const string CounterReverseDnsResolved = "逆引きホスト名の取得（成功）";

    /// <summary>カウンタ平易語: 逆引き PTR 未登録（正常系であることを平易語で明示する）。</summary>
    public const string CounterReverseDnsNotFound = "逆引きホスト名の取得（名前の登録なし——異常ではありません）";

    /// <summary>カウンタ平易語: 逆引き解決失敗（増加が異常のシグナル）。</summary>
    public const string CounterReverseDnsFailed = "逆引きホスト名の取得（失敗——DNS の応答なし・エラー）";

    /// <summary>カウンタ平易語: 逆引き解決の見送り（キャッシュ上限。増加はキャッシュ運用の逼迫のシグナル）。</summary>
    public const string CounterReverseDnsSkipped = "逆引きホスト名の取得（見送り——キャッシュが満杯）";

    /// <summary>未知の計器名のフォールバック表示（新カウンタ追加時の平易語未登録を隠さない）。</summary>
    public const string CounterUnknown = "（対応表未登録の項目）";

    /// <summary>ゲージ一覧カードの見出し。</summary>
    public const string GaugesTitle = "現在の状態";

    /// <summary>ゲージ: 保存先（データベース）の使用量（用語対応表: provider → 保存先（データベース））。</summary>
    public const string GaugeDatabaseSize = "保存先（データベース）の使用量";

    /// <summary>ゲージ: DB サイズ取得不能時の表示。</summary>
    public const string GaugeDatabaseSizeUnavailable = "取得できません";

    /// <summary>
    /// OS 受信破棄（OS 統計）の常時説明（M8-3 の設計判断: 値を表示せず説明のみを常時掲示する。
    /// architecture.md §4.2・D-6——値 0 の表示が「取りこぼしゼロ」の誤解を生むため。
    /// 計器（yagura.os.udp.*）自体も ADR-0016 決定 3 で製品コードから撤去済み。
    /// 判断記録は ui.md §5.5）。
    /// </summary>
    public const string OsUdpDiscardExplanation =
        "OS がこのアプリへ渡す前に破棄した受信データの数（OS の統計値）は、この画面に表示していません";

    /// <summary>OS 受信破棄の常時説明の補足（理由と代替手段）。</summary>
    public const string OsUdpDiscardExplanationSupplement =
        "検証済みの Windows 環境では、この OS 統計は受信・破棄のどちらも計上しないことが実測で確認されています。" +
        "0 という値を表示すると「取りこぼしなし」という誤解を生むため、値の表示自体を行いません。" +
        "取りこぼしの確認には、上記のカウンタと、ダッシュボードの送信元別の受信状況（最終受信時刻）をあわせて確認してください";

    /// <summary>受信断履歴カードの見出し（用語対応表: 受信断 → 受信できなかった時間帯）。</summary>
    public const string OutageHistoryTitle = "受信できなかった時間帯の履歴";

    /// <summary>受信断履歴: 正常停止由来の種別表示。</summary>
    public const string OutageKindNormalStop = "停止・再起動による";

    /// <summary>受信断履歴: クラッシュ近似断点の種別表示（近似である旨を含む。ui.md §5.3）。</summary>
    public const string OutageKindCrashApproximate = "正常に終了しなかったため境界はおおよそ";

    /// <summary>受信断履歴: リスナ再構成（設定の再読み込み）による瞬断の種別表示（Issue #262）。</summary>
    public const string OutageKindListenerReconfigure = "設定反映（リスナ再構成）による";

    /// <summary>受信断履歴: bind 失敗から再試行での受信再開までの種別表示（Issue #291）。</summary>
    public const string OutageKindListenerBindRetry = "ポートを開けなかった間（再試行で復旧）";

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
    // ---- circuit 統治（security.md §2.2。M8-4。用語対応表: circuit → 画面とサーバの接続） ----

    /// <summary>circuit 数上限到達の案内ページの見出し（security.md §2.2 の静的な案内）。</summary>
    public const string CircuitLimitNoticeTitle = "閲覧者数が上限に達しています";

    /// <summary>上限到達の案内本文（現在の閲覧者数・上限値を含める——security.md §2.2 の要件）。</summary>
    public static string FormatCircuitLimitNoticeBody(int current, int limit) =>
        $"現在の閲覧者数が {current} で、上限の {limit} に達しているため、新しく画面を開けません。" +
        "表示中の画面には影響ありません。";

    /// <summary>上限到達時の解放の導線（管理者への連絡——security.md §2.2 の要件）。</summary>
    public const string CircuitLimitNoticeContactHint =
        "しばらくしてから再度お試しください。急ぐ場合は、サーバの管理者に連絡して" +
        "使われていない画面とサーバの接続を切断してもらうと、枠が解放されます。";

    /// <summary>接続終了ページ（circuit を要しない静的な案内）の見出し。</summary>
    public const string CircuitEndedTitle = "画面とサーバの接続を終了しました";

    /// <summary>管理者による個別切断の案内（security.md §2.2）。</summary>
    public const string CircuitEndedByAdministratorBody =
        "サーバの管理者がこの画面とサーバの接続を切断しました。ログの受信は継続しています。";

    /// <summary>無操作回収（SEC-8 仮値）の案内。</summary>
    public const string CircuitEndedByIdleBody =
        "一定時間操作がなかったため、この画面とサーバの接続を終了しました。ログの受信は継続しています。";

    /// <summary>接続終了ページからの復帰導線。</summary>
    public const string CircuitEndedReloadHint = "続きを見るには、ページを再読み込みしてください。";

    // ---- 管理画面共通（ui.md §4「設定（ウィザード群）」。M8-4） ----

    /// <summary>
    /// 管理画面の circuit 層ガードの拒否表示（閲覧側からの到達。管理系パスの存在自体を
    /// 説明しない——ListenerPortGuardMiddleware の 404 と同じ判断）。
    /// </summary>
    public const string AdminScreenNotFound = "ページが見つかりません。";

    /// <summary>管理画面の circuit 層ガードで帰属を確認できない間の表示（fail-closed の中間状態）。</summary>
    public const string AdminScreenAccessChecking = "接続の帰属を確認しています…";

    /// <summary>設定トップ（/admin）の画面見出し（ui.md §4 の画面構成「設定（ウィザード群）」）。</summary>
    public const string AdminHomeTitle = "設定";

    /// <summary>初期セットアップウィザードへの導線・見出し。</summary>
    public const string AdminSetupWizardTitle = "初期セットアップ";

    /// <summary>本番昇格ウィザードへの導線・見出し（用語対応表: 本番昇格 → 保存先を SQL Server に切り替える）。</summary>
    public const string AdminPromotionWizardTitle = "保存先を SQL Server に切り替える";

    /// <summary>circuit 管理画面への導線・見出し。</summary>
    public const string AdminCircuitsTitle = "画面とサーバの接続の管理";

    // ---- 管理 UI 認証（ADR-0010 Phase 1） ----

    /// <summary>認証設定画面への導線・見出し。</summary>
    public const string AdminAuthSetupTitle = "管理 UI の認証";

    /// <summary>ログイン画面の見出し。</summary>
    public const string AdminLoginTitle = "サインイン";

    /// <summary>Windows 統合認証でのサインインボタン文言。</summary>
    public const string AdminLoginWindowsButton = "Windows でサインイン";

    /// <summary>アプリ独自認証のユーザー名入力欄ラベル。</summary>
    public const string AdminLoginUsernameLabel = "ユーザー名";

    /// <summary>アプリ独自認証のパスワード入力欄ラベル。</summary>
    public const string AdminLoginPasswordLabel = "パスワード";

    /// <summary>アプリ独自認証のサインインボタン文言。</summary>
    public const string AdminLoginAppButton = "サインイン";

    /// <summary>
    /// ログイン失敗時の汎用エラー文言（ユーザー列挙耐性のため、資格情報誤り・アカウント不在・
    /// バックオフ待機中のいずれも同一文言とする——ADR-0010 決定 3・ADR-0011 決定 3・
    /// security.md §4.3・§2.4.1）。
    /// </summary>
    public const string AdminLoginError = "ユーザー名またはパスワードが正しくないか、現在サインインできません。";

    /// <summary>
    /// 待機表示の統一文言（ADR-0011 決定 5.1・6）。<c>{0}</c> に秒数を埋め込む。
    /// <b>用途は IP レート制限/グローバルトークンバケット拒否の 429 応答（アクセス集中）に限る</b>——
    /// これらは送信元 IP 単位・プロセス全体の状態で判定し、ユーザー名の実在有無に依存しないため
    /// カウントダウンを出しても列挙シグナルにならない（決定 4）。**アカウント単位バックオフには
    /// 使わない**——バックオフ待機を UI に出すと実在アカウントの存在を暴く（決定 3 の非開示要件。
    /// バックオフの効果はサーバ側の応答遅延としてのみ現れ、応答は誤パスワードと同一に統一する）。
    /// </summary>
    public const string AdminLoginWait = "しばらくお待ちください。あと {0} 秒で再試行できます。";

    /// <summary>管理画面が認証を要求しているが未認証のときの表示（AdminScreenLayout のリダイレクト経路の直前表示）。</summary>
    public const string AdminScreenRequiresAuthentication = "サインインが必要です…";

    // ---- 閲覧 UI 認証（ADR-0010 Phase 4 決定 7） ----

    /// <summary>閲覧ログイン画面の見出し。</summary>
    public const string ViewerLoginTitle = "閲覧 UI へのサインイン";

    /// <summary>
    /// 閲覧ログイン画面の説明（認証 opt-in 有効時のみ到達）。方式に依らず中立の文言にする——アプリ独自認証併用時
    /// （ID/パスワード欄も表示される）に「Windows でのサインインが必要」と読める固定文言だと誤解を招くため
    /// （佐藤のレビュー指摘）。具体的な方式は各サインインボタン/フォームのラベルが示す。
    /// </summary>
    public const string ViewerLoginIntro = "このログを閲覧するにはサインインが必要です。";

    /// <summary>閲覧の Windows 統合認証サインインボタン文言。</summary>
    public const string ViewerLoginWindowsButton = "Windows でサインイン";

    /// <summary>閲覧のアプリ独自認証サインインボタン文言。</summary>
    public const string ViewerLoginAppButton = "サインイン";

    /// <summary>閲覧ログイン失敗時の汎用エラー文言（列挙耐性——管理ログインと同じ非開示方針）。</summary>
    public const string ViewerLoginError = "ユーザー名またはパスワードが正しくないか、閲覧を許可されたグループに所属していません。";

    /// <summary>閲覧画面が認証を要求しているが未認証のときの表示（MainLayout のリダイレクト経路の直前表示）。</summary>
    public const string ViewerScreenRequiresAuthentication = "サインインが必要です…";

    // ---- 管理 UI のリモートアクセス（HTTPS）設定（ADR-0012。/admin/remote-access） ----

    /// <summary>リモートアクセス設定画面への導線・見出し（ADR-0012 決定 1——認証設定とは分離した画面）。</summary>
    public const string AdminRemoteAccessTitle = "管理 UI のリモートアクセス（HTTPS）";

    /// <summary>画面の説明文（何をする画面か。対象キーはすべて反映 = サービス再起動——ADR-0012 決定 6）。</summary>
    public const string AdminRemoteAccessIntro =
        "管理画面をリモートの端末へ HTTPS で公開するための設定です。証明書はこのサーバの証明書ストアから選択でき、" +
        "拇印の手入力は不要です。";

    /// <summary>リモートバインド（Admin:RemoteBinding:Enabled）のスイッチ文言。</summary>
    public const string AdminRemoteAccessRemoteBindingLabel = "リモートの端末から管理画面へのアクセスを許可する";

    /// <summary>
    /// リモートバインドの前提条件の説明（ADR-0010 決定 1 の fail-closed 不変条件を保存前に
    /// 利用者の言葉で示す——ADR-0012 決定 4。認証設定への相互リンク（決定 1）とセットで表示する）。
    /// </summary>
    public const string AdminRemoteAccessRemoteBindingNote =
        "有効化には、管理 UI の認証（Windows 統合認証またはアプリ独自認証）・HTTPS の有効化・証明書の選択が" +
        "すべて必要です。欠けたままでは保存できません（認証や通信保護を欠いたリモート公開を防ぐためです）。";

    /// <summary>認証設定画面への相互リンクの文言（ADR-0012 決定 1）。</summary>
    public const string AdminRemoteAccessAuthLinkText = "認証設定へ";

    /// <summary>認証設定画面側に置く、本画面への相互リンクの文言（ADR-0012 決定 1）。</summary>
    public const string AdminAuthSetupRemoteAccessLinkText = "リモートアクセス（HTTPS）設定へ";

    /// <summary>HTTPS 有効化（Admin:Https:Enabled）のスイッチ文言。</summary>
    public const string AdminRemoteAccessHttpsLabel = "リモート管理の HTTPS 待ち受けを有効にする";

    /// <summary>HTTPS ポート（Admin:Https:Port）の入力ラベル。</summary>
    public const string AdminRemoteAccessHttpsPortLabel = "HTTPS ポート";

    /// <summary>HTTPS ポートの補足（未設定時の既定値を明示する）。</summary>
    public const string AdminRemoteAccessHttpsPortHelp = "未入力の場合は既定の 8516 を使います。";

    /// <summary>現在の設定（保存済みの永続値）カードの見出し。</summary>
    public const string AdminRemoteAccessStatusTitle = "現在の設定（保存済みの値）";

    /// <summary>現在の設定: リモートアクセスの行ラベル。</summary>
    public const string AdminRemoteAccessStatusRemoteBinding = "リモートの端末からの管理アクセス";

    /// <summary>現在の設定: HTTPS の行ラベル。</summary>
    public const string AdminRemoteAccessStatusHttps = "HTTPS 待ち受け";

    /// <summary>現在の設定: 証明書拇印の行ラベル。</summary>
    public const string AdminRemoteAccessStatusThumbprint = "証明書の拇印";

    /// <summary>現在の設定: HTTPS ポートの行ラベル。</summary>
    public const string AdminRemoteAccessStatusPort = "HTTPS ポート";

    /// <summary>現在の設定: 有効。</summary>
    public const string AdminRemoteAccessStatusEnabled = "有効";

    /// <summary>現在の設定: 無効。</summary>
    public const string AdminRemoteAccessStatusDisabled = "無効";

    /// <summary>現在の設定: 未設定の値の表示。</summary>
    public const string AdminRemoteAccessStatusNotSet = "（未設定）";

    /// <summary>現在の設定: ポート未設定（既定値使用）の表示。</summary>
    public const string AdminRemoteAccessStatusDefaultPort = "（未設定——既定の 8516 を使用）";

    /// <summary>証明書選択カードの見出し（ADR-0012 決定 2 の本体）。</summary>
    public const string AdminRemoteAccessCertificatesTitle = "証明書の選択";

    /// <summary>
    /// 証明書一覧の説明（列挙範囲の最小化——serverAuth EKU + 秘密鍵あり——を利用者の言葉で明示する。
    /// ADR-0012 決定 2・受け入れ基準「拇印手貼りの撤廃」）。
    /// </summary>
    public const string AdminRemoteAccessCertificatesIntro =
        @"このサーバの証明書ストア（ローカル コンピューター\個人）のうち、サーバー認証（serverAuth）の用途と" +
        "秘密鍵を備えた証明書のみを表示しています。選択すると拇印が自動で設定されます（手入力は不要です）。";

    /// <summary>証明書一覧の再取得ボタン（付与・取り込みの後に、その場で結果を確認できる導線）。</summary>
    public const string AdminRemoteAccessRefreshButton = "再確認";

    /// <summary>証明書行: 発行者のラベル。</summary>
    public const string AdminRemoteAccessCertIssuerLabel = "発行者";

    /// <summary>証明書行: 有効期間のラベル。</summary>
    public const string AdminRemoteAccessCertValidityLabel = "有効期間";

    /// <summary>証明書行: 拇印のラベル。</summary>
    public const string AdminRemoteAccessCertThumbprintLabel = "拇印";

    /// <summary>証明書の状態バッジ: 使用可能。</summary>
    public const string AdminRemoteAccessCertOkBadge = "使用可能";

    /// <summary>証明書の状態バッジ: 有効期間外（期限切れ／未来証明書）。</summary>
    public const string AdminRemoteAccessCertExpiredBadge = "有効期間外";

    /// <summary>証明書の状態バッジ: サービスアカウントが秘密鍵を読めない。</summary>
    public const string AdminRemoteAccessCertKeyUnreadableBadge = "秘密鍵の読取権限なし";

    /// <summary>
    /// 有効期間外の証明書の警告（選択自体は可能だが保存時に拒否される旨——保存前検証
    /// （<c>AdminRemoteAccessAdminService</c>）と同じ理由で説明する。ADR-0012 決定 4 = D-6）。
    /// </summary>
    public const string AdminRemoteAccessCertExpiredWarning =
        "この証明書は有効期間外のため、保存時に拒否されます。起動時の証明書解決は有効期間外の証明書を" +
        "受け付けず、リモート HTTPS の待ち受けが開かれない縮小継続になるためです。";

    /// <summary>
    /// 秘密鍵をサービスアカウントが読めない証明書の警告（ADR-0012 決定 3 = (b)。保存は可能だが
    /// 付与しないまま再起動すると縮小継続になる旨を明示する）。
    /// </summary>
    public const string AdminRemoteAccessPrivateKeyUnreadableWarning =
        @"サービスアカウント（NT SERVICE\Yagura）がこの証明書の秘密鍵を読み取れません（読取権限の付与が必要です）。" +
        "このまま保存はできますが、権限を付与せずに再起動すると、リモート HTTPS の待ち受けが開かれない縮小継続になります。";

    /// <summary>
    /// 秘密鍵の読取権限の付与手順（certlm.msc の具体手順。configuration.md §6 CF-D2 と同一の手動経路——
    /// ADR-0012 決定 3 = (b)。付与後に「再確認」でその場で結果を確認できる旨を含める）。
    /// </summary>
    public const string AdminRemoteAccessPrivateKeyGrantSteps =
        @"付与手順: certlm.msc → 個人 → 証明書 → 対象の証明書を右クリック → すべてのタスク → 秘密キーの管理 → " +
        @"「NT SERVICE\Yagura」に読み取りを付与（利用者向け設定リファレンス configuration.md §6 CF-D2 と同じ手順）。" +
        "付与後に「再確認」を押すと、その場で結果を確認できます。";

    /// <summary>
    /// 現在設定されている拇印が一覧に見つからない場合の注記。{0} に拇印が入る
    /// （証明書の削除・条件不適合への変化を隠さない——ui.md §5.3 と同じ向き）。
    /// </summary>
    public const string AdminRemoteAccessThumbprintNotInListFormat =
        "現在設定されている拇印 {0} の証明書は一覧にありません（削除されたか、サーバー認証 + 秘密鍵の条件を" +
        "満たさなくなった可能性があります）。";

    /// <summary>候補 0 件の空状態の見出し（ADR-0012 受け入れ基準「空状態の案内」）。</summary>
    public const string AdminRemoteAccessEmptyTitle = "選択できる証明書が見つかりません";

    /// <summary>空状態の補足（どの条件で探したか）。</summary>
    public const string AdminRemoteAccessEmptyDescription =
        @"このサーバの証明書ストア（ローカル コンピューター\個人）に、サーバー認証（serverAuth）の用途と" +
        "秘密鍵を備えた証明書がありません。";

    /// <summary>空状態の次の行動（取り込み先と certlm.msc への言及——ADR-0012 決定 2）。</summary>
    public const string AdminRemoteAccessEmptyNextAction =
        "certlm.msc（ローカル コンピューターの証明書の管理）で「個人」ストアへ秘密鍵付きの証明書を取り込むと、" +
        "この一覧に表示されます。取り込み後に「再確認」を押してください。";

    /// <summary>証明書一覧の取得に失敗した場合の表示。{0} にエラーメッセージが入る（握り潰さない）。</summary>
    public const string AdminRemoteAccessEnumerationFailedFormat = "証明書一覧の取得に失敗しました: {0}";

    /// <summary>
    /// 反映方式の常設注記（ADR-0012 決定 6——何が・どれくらい・どう戻るか。対象 4 キーは
    /// すべて反映 = サービス再起動であり、複数の変更は 1 回の保存 + 1 回の再起動でまとめて反映できる）。
    /// </summary>
    public const string AdminRemoteAccessRestartNote =
        "この画面の設定は、保存だけでは反映されません。反映は次回のサービス再起動時で、再起動中は syslog の受信と" +
        "管理画面が停止します（停止中に送られたログは失われることがあります）。複数の項目を変更する場合は、" +
        "まとめて保存してから 1 回の再起動で反映してください。";

    /// <summary>
    /// 保存成功の通知・要約。{0} に変更されたキーの一覧が入る（ADR-0012 決定 6 の受信断明示を含む）。
    /// </summary>
    public const string AdminRemoteAccessSavedFormat =
        "リモートアクセス設定を保存しました（変更: {0}）。反映には次回サービス再起動が必要です" +
        "（再起動中は syslog の受信と管理画面が停止します）。";

    /// <summary>変更ゼロ（no-op）で保存されなかった場合の通知。</summary>
    public const string AdminRemoteAccessSavedNoChanges = "現在の設定と同じ内容のため、保存は行われませんでした。";

    /// <summary>
    /// 管理 HTTPS 画面から TLS 受信画面への相互リンクの文言（ADR-0019 決定 1——同じ証明書一覧が出る
    /// 2 画面の取り違えを防ぐため、用途を平易に書き分ける）。
    /// </summary>
    public const string AdminRemoteAccessIngestionTlsLinkNote =
        "送信元機器からのログを TLS で受信する設定は別の画面です: ";

    /// <summary>同上のリンク表示名。</summary>
    public const string AdminRemoteAccessIngestionTlsLinkText = "TLS 受信の証明書設定";

    // ---- TLS 受信の証明書設定（ADR-0019。Issue #349） ----

    /// <summary>TLS 受信の証明書設定画面の見出し（ADR-0019 決定 1——管理 HTTPS とは分離した画面）。</summary>
    public const string IngestionTlsTitle = "TLS 受信の証明書設定";

    /// <summary>
    /// 画面の説明文。<b>管理 HTTPS 画面との用途の書き分けを明示する</b>（ADR-0019 決定 1——
    /// 同じ証明書一覧が出る 2 画面の取り違えを防ぐ）。
    /// </summary>
    public const string IngestionTlsIntro =
        "送信元の機器から Yagura へ syslog を TLS で受信するための設定です（RFC 5425）。" +
        "証明書はこのサーバの証明書ストアから選択でき、拇印の手入力は不要です。" +
        "この設定は「機器 → Yagura」の受信用であり、ブラウザから管理画面を見るための HTTPS とは別です。";

    /// <summary>管理 HTTPS 画面への相互リンクの文言（用途の書き分け）。</summary>
    public const string IngestionTlsAdminHttpsLinkNote =
        "ブラウザから管理画面を HTTPS で見るための設定は別の画面です: ";

    /// <summary>同上のリンク表示名。</summary>
    public const string IngestionTlsAdminHttpsLinkText = "管理 UI のリモートアクセス（HTTPS）";

    /// <summary>現在の状態（永続値）カードの見出し。</summary>
    public const string IngestionTlsStatusTitle = "現在の設定（保存済み）";

    /// <summary>状態表: TLS 受信の有効/無効。</summary>
    public const string IngestionTlsStatusEnabled = "TLS 受信";

    /// <summary>状態表: 証明書拇印。</summary>
    public const string IngestionTlsStatusThumbprint = "証明書の拇印";

    /// <summary>状態表: ポート。</summary>
    public const string IngestionTlsStatusPort = "ポート";

    /// <summary>状態表: ポート未設定（既定 6514 = RFC 5425 の標準ポート）。</summary>
    public const string IngestionTlsStatusDefaultPort = "6514（既定・RFC 5425 の標準ポート）";

    /// <summary>TLS 受信の有効化スイッチ。</summary>
    public const string IngestionTlsEnabledLabel = "TLS での syslog 受信を有効にする";

    /// <summary>有効化スイッチの補足（opt-in であり平文受信とは独立であること）。</summary>
    public const string IngestionTlsEnabledNote =
        "既定では無効です。有効にしても平文の UDP / TCP 受信はそのまま継続します（別のポートで待ち受けます）。";

    /// <summary>ポート入力のラベル。</summary>
    public const string IngestionTlsPortLabel = "TLS 受信のポート";

    /// <summary>ポート入力の補助説明。</summary>
    public const string IngestionTlsPortHelp = "未指定の場合は RFC 5425 の標準ポート 6514 が使われます。";

    /// <summary>証明書選択カードの見出し。</summary>
    public const string IngestionTlsCertificatesTitle = "受信に使う証明書";

    /// <summary>証明書一覧の導入文。</summary>
    public const string IngestionTlsCertificatesIntro =
        "このサーバの証明書ストア（LocalMachine\\My）にある、サーバー認証用途で秘密鍵を持つ証明書の一覧です。";

    /// <summary>
    /// 期限切れバッジ。<b>TLS 受信では選択可能</b>（保存は警告付きで通る——ADR-0019 決定 2）。
    /// 管理 HTTPS 側は同じ状態が保存の拒否理由になる。
    /// </summary>
    public const string IngestionTlsCertExpiredBadge = "有効期間外";

    /// <summary>秘密鍵の読取不可バッジ（TLS 受信では保存の拒否理由）。</summary>
    public const string IngestionTlsCertKeyUnreadableBadge = "秘密鍵の読取権限なし";

    /// <summary>問題なしバッジ。</summary>
    public const string IngestionTlsCertOkBadge = "使用可能";

    /// <summary>
    /// 期限切れ証明書を選んだときの警告（ADR-0019 決定 2・委任 4）。<b>管理 HTTPS との挙動差分の
    /// 説明を含める</b>ことが受け入れ基準。あわせて「保存しても能動通知は出続ける」ことも明示する
    /// （出続けるのは不具合ではない、と分かるようにするため）。
    /// </summary>
    public const string IngestionTlsCertExpiredWarning =
        "この証明書は有効期間外です。TLS 受信は有効期間外でも待ち受けを続けるため、この証明書のまま保存できます" +
        "（管理画面の HTTPS 設定では同じ状態は保存できません——あちらは待ち受け自体を開かないためです）。" +
        "ただし送信元の機器が証明書を検証する設定であれば接続を拒否し、そのログは届かないまま気づきにくい形で失われます。" +
        "また期限接近・使用不能の通知は、有効期間内の証明書へ差し替えるまで出続けます。";

    /// <summary>
    /// 秘密鍵が読めない証明書についての説明（TLS 受信では拒否——ADR-0019 決定 2）。
    /// 管理 HTTPS 側（警告に留める）との違いも書く。
    /// </summary>
    public const string IngestionTlsPrivateKeyUnreadableWarning =
        "この証明書の秘密鍵に、Yagura のサービスアカウントがアクセスできません。秘密鍵を読めないと TLS の" +
        "ハンドシェイクが成立せず、TLS 受信はまったく機能しないため、この証明書は選択しても保存できません。";

    /// <summary>秘密鍵の読取権限付与の手順（CF-D2 への誘導。管理 HTTPS 画面と同一手順）。</summary>
    public const string IngestionTlsPrivateKeyGrantSteps =
        "証明書スナップイン（certlm.msc）で対象の証明書を右クリック →「すべてのタスク」→「秘密キーの管理」から、" +
        "サービスアカウントへ読み取り権限を付与し、「再確認」を押してください。";

    /// <summary>一覧が空のときの見出し。</summary>
    public const string IngestionTlsEmptyTitle = "選択できる証明書がありません";

    /// <summary>一覧が空のときの説明。</summary>
    public const string IngestionTlsEmptyDescription =
        "このサーバの証明書ストア（LocalMachine\\My）に、サーバー認証用途（serverAuth）で秘密鍵を持つ証明書が" +
        "見つかりませんでした。";

    /// <summary>一覧が空のときの次の行動。</summary>
    public const string IngestionTlsEmptyNextAction =
        "証明書スナップイン（certlm.msc）から「ローカル コンピューター」の「個人」へ、秘密鍵付きの証明書を" +
        "取り込んでから「再確認」を押してください。";

    /// <summary>一覧の再取得ボタン。</summary>
    public const string IngestionTlsRefreshButton = "再確認";

    /// <summary>列挙に失敗した場合。{0} に例外メッセージが入る。</summary>
    public const string IngestionTlsEnumerationFailedFormat = "証明書の一覧を取得できませんでした: {0}";

    /// <summary>永続値の拇印が一覧に無い場合の警告。{0} に拇印が入る。</summary>
    public const string IngestionTlsThumbprintNotInListFormat =
        "現在設定されている拇印（{0}）が一覧にありません。証明書が削除されたか、サーバー認証用途・秘密鍵ありの" +
        "条件を満たさなくなった可能性があります。";

    /// <summary>
    /// 反映方式の常設注記（ADR-0019 決定 4）。<b>TLS だけでなく全受信が停止する</b>ことを明示し、
    /// 複数画面の変更をまとめて 1 回の再起動で反映するよう促す（管理 HTTPS 画面と本画面の変更は
    /// 累積されるため、2 画面 1 再起動で完結できる）。
    /// </summary>
    public const string IngestionTlsRestartNote =
        "この画面の設定は、保存だけでは反映されません。反映は次回のサービス再起動時で、再起動中は TLS だけでなく" +
        "すべての受信（UDP / TCP を含む）と管理画面が停止します（停止中に送られたログは失われることがあります）。" +
        "管理画面の HTTPS 設定など他の変更もある場合は、まとめて保存してから 1 回の再起動で反映してください。";

    /// <summary>保存成功の要約。{0} に変更されたキーの一覧が入る。</summary>
    public const string IngestionTlsSavedFormat =
        "TLS 受信の設定を保存しました（変更: {0}）。反映するにはサービスの再起動が必要です。";

    /// <summary>変更ゼロ（no-op）の通知。</summary>
    public const string IngestionTlsSavedNoChanges = "現在の設定と同じ内容のため、保存は行われませんでした。";

    /// <summary>
    /// 観測性への導線の見出し（ADR-0019 決定 5b——ADR-0012 の委任を引き取ったもの）。
    /// 証明書差し替え + 再起動の直後に運用者が行う「全送信元が再接続できたかの確認」を、
    /// 設定画面から始められるようにする。
    /// </summary>
    public const string IngestionTlsObservabilityTitle = "受信状況の確認";

    /// <summary>同上の説明文。</summary>
    public const string IngestionTlsObservabilityNote =
        "証明書を差し替えて再起動したあとは、送信元の機器がすべて再接続できたかを確認してください。" +
        "TLS のハンドシェイク失敗は計器に、送信元ごとの最終受信時刻はダッシュボードに出ます。";

    /// <summary>観測性リンク: 送信元別の受信状況。</summary>
    public const string IngestionTlsObservabilitySourcesLinkText = "送信元別の受信状況";

    /// <summary>観測性リンク: 計器（ハンドシェイク失敗を含む）。</summary>
    public const string IngestionTlsObservabilityMetricsLinkText = "計器（ハンドシェイク失敗を含む）";

    // ---- 初期セットアップウィザード（configuration.md §3〜§7。M8-4 骨格） ----

    /// <summary>ステップ: 受信設定。</summary>
    public const string SetupStepReceptionTitle = "受信設定";

    /// <summary>ステップ: 閲覧と管理。</summary>
    public const string SetupStepViewerAccessTitle = "閲覧と管理";

    /// <summary>ステップ: ログを保存しておく期間（用語対応表: 保持期間）。</summary>
    public const string SetupStepRetentionTitle = "ログを保存しておく期間";

    /// <summary>ステップ: 確認。</summary>
    public const string SetupStepReviewTitle = "確認";

    /// <summary>ステップ確定ボタン。</summary>
    public const string WizardConfirmStep = "この内容で次へ";

    /// <summary>設定の適用ボタン。</summary>
    public const string WizardApply = "設定を保存する";

    /// <summary>再開位置の明示（configuration.md §7「どこから再開しているか」。{0} にステップ名）。</summary>
    public const string WizardResumeNoticeFormat = "「{0}」から再開しています。確定済みの内容は保存されています。";

    /// <summary>適用完了。</summary>
    public const string WizardApplied = "設定を保存しました。";

    /// <summary>二重適用の抑止結果（冪等トークンによる再送検出。configuration.md §7）。</summary>
    public const string WizardAlreadyApplied = "この操作は既に適用済みです（二重適用は行われていません）。";

    /// <summary>
    /// 楽観競合の検出結果（configuration.md §3——上書きせずに再読み込みを促す）。
    /// </summary>
    public const string WizardConflict =
        "設定ファイルがほかの手段（手編集など）で変更されていたため、保存を中止しました。" +
        "内容を確認のうえ、確認ステップからやり直してください。";

    /// <summary>冪等トークン不一致（期限切れ・別セッション）。</summary>
    public const string WizardInvalidToken = "操作の有効期限が切れています。確認ステップからやり直してください。";

    /// <summary>
    /// 適用完了画面からの再編集開始ボタン（Issue #248——適用後の画面を行き止まりにしない。
    /// 現在の設定値を種にウィザードを再開する）。
    /// </summary>
    public const string WizardBeginReconfiguration = "設定を変更する";

    /// <summary>ウィザードの前ステップへ戻るボタン（表示の移動——確定の取り消しではない）。</summary>
    public const string WizardBack = "戻る";

    /// <summary>反映方式の表示（configuration.md §3・ui.md §5.4）: 即時反映。</summary>
    public const string ApplyEffectImmediate = "変更はすぐに反映されます";

    /// <summary>反映方式の表示: リスナ再構成（接続の瞬断あり）。</summary>
    public const string ApplyEffectListenerReconfiguration = "反映時に受信の接続が一時的に切れます";

    /// <summary>反映方式の表示: サービス再起動が必要。</summary>
    public const string ApplyEffectRestartRequired = "反映にはサービスの再起動が必要です（再起動中は受信できません）";

    // ---- 本番昇格ウィザード（database.md §6.1。M8-4 骨格 + PR #102 の UX 完成） ----

    /// <summary>接続の入力方式: 項目で入力（既定——database.md §6.1）。</summary>
    public const string PromotionInputModeForm = "項目で入力（推奨）";

    /// <summary>接続の入力方式: 接続文字列の直接入力（上級者向け）。</summary>
    public const string PromotionInputModeRaw = "接続文字列を直接入力（上級者向け）";

    /// <summary>サーバ名の入力ラベル。</summary>
    public const string PromotionServerNameLabel = "サーバ名";

    /// <summary>サーバ名の入力例（名前付きインスタンス・ポート併記の形を示す）。</summary>
    public const string PromotionServerNamePlaceholder = @"例: SV01\SQLEXPRESS または 10.0.0.180,1433";

    /// <summary>データベース名の入力ラベル。</summary>
    public const string PromotionDatabaseNameLabel = "データベース名";

    /// <summary>認証方式の選択ラベル（用語対応表 ui.md §7.2）。</summary>
    public const string PromotionAuthModeLabel = "認証方式";

    /// <summary>認証方式: Windows 統合認証（既定 = database.md §5.1 の第一推奨）。</summary>
    public const string PromotionAuthModeWindows = "Windows 統合認証（サービスのアカウントで接続）";

    /// <summary>認証方式: SQL Server 認証。</summary>
    public const string PromotionAuthModeSql = "SQL Server 認証（ユーザー名とパスワードで接続）";

    /// <summary>
    /// Windows 統合認証の接続アカウントの明示（{0} = サービス実行アカウント名。SQL Server 側に
    /// どの名前で見えるかを推測させない——database.md §6.1）。
    /// </summary>
    public const string PromotionWindowsAccountNoteFormat =
        "接続に使うアカウント: {0}（SQL Server 側にはこの名前のログインが必要です）";

    /// <summary>ユーザー名の入力ラベル（SQL Server 認証）。</summary>
    public const string PromotionUserNameLabel = "ユーザー名";

    /// <summary>パスワードの入力ラベル（SQL Server 認証。マスク表示）。</summary>
    public const string PromotionPasswordLabel = "パスワード";

    /// <summary>パスワードの取り扱いの説明（configuration.md §5・§2 の統治を利用者の言葉で）。</summary>
    public const string PromotionPasswordHelp =
        "パスワードはこの欄でのみ入力します。切り替えの適用時に DPAPI で暗号化して保存され、" +
        "接続文字列の組み立てだけに使われます。画面や記録に平文では残りません。";

    /// <summary>サーバ証明書の信頼（TrustServerCertificate。用語対応表 ui.md §7.2）。</summary>
    public const string PromotionTrustServerCertificateLabel = "サーバ証明書を信頼する";

    /// <summary>
    /// サーバ証明書の信頼の説明（失敗前から読める常設の説明——database.md §6.1。
    /// なりすまし検知が行われない残存リスクを含む）。
    /// </summary>
    public const string PromotionTrustServerCertificateHelp =
        "証明書の検証を省略します（通信の暗号化は維持されます）。なりすまし検知は行われないため、" +
        "自己署名証明書を使う閉域環境向けの選択です。";

    /// <summary>接続文字列の入力ラベル（直接入力方式）。</summary>
    public const string PromotionConnectionStringLabel = "SQL Server への接続文字列";

    /// <summary>直接入力方式の注意（パスワード系キーの拒否——database.md §6.1）。</summary>
    public const string PromotionRawConnectionStringHelp =
        "パスワードは接続文字列に書かず、下のパスワード欄に入力してください（Password / Pwd キーはエラーになります）。";

    /// <summary>パスワードの取り扱いの説明（configuration.md §5 の統治を利用者の言葉で）。</summary>
    public const string PromotionCredentialHandlingNote =
        "パスワードはこのウィザードの実行中だけサーバのメモリ上に保持され、完了または中断で破棄されます。" +
        "15 分間操作がない場合も破棄され、再開時に再入力が必要です（サーバ名などの入力内容は保持されます）。";

    /// <summary>接続検証ボタン（database.md §6.1 準備フェーズ）。</summary>
    public const string PromotionValidateConnection = "接続を検証する";

    /// <summary>接続検証成功。</summary>
    public const string PromotionValidationSucceeded = "SQL Server への接続を確認しました。";

    /// <summary>パスワードの再入力要求（無操作タイムアウト後の再開。configuration.md §5）。</summary>
    public const string PromotionCredentialReentryRequired =
        "操作の間隔が空いたため、パスワードを破棄しました。再入力してください（確定済みの選択は保存されています）。";

    /// <summary>
    /// 検証失敗の案内: サーバ証明書が信頼されない（database.md §6.1 分類①。次の一手 =
    /// 「サーバ証明書を信頼する」の有効化）。
    /// </summary>
    public const string PromotionFailureCertificateGuidance =
        "SQL Server の証明書が信頼されていないため接続できませんでした。自己署名証明書を使っている場合は" +
        "「サーバ証明書を信頼する」を有効にして、もう一度検証してください。";

    /// <summary>検証失敗の案内: サーバへ到達できない（分類②。修復 SQL なし）。</summary>
    public const string PromotionFailureUnreachableGuidance =
        "SQL Server へ到達できませんでした。サーバ名・ポート・ファイアウォールの設定を確認してください。";

    /// <summary>
    /// 検証失敗の案内: ログイン失敗（分類③。18456 は誤パスワードでも DB 不在でも返るため
    /// 条件付きの案内——database.md §6.1）。
    /// </summary>
    public const string PromotionFailureLoginGuidance =
        "SQL Server にログインできませんでした。まずユーザー名とパスワード（Windows 統合認証の場合は" +
        "接続に使うアカウント）を確認してください。ログインが未作成の場合は、下の SQL で作成できます。";

    /// <summary>検証失敗の案内: データベース不在（分類④）。</summary>
    public const string PromotionFailureDatabaseNotFoundGuidance =
        "指定したデータベースを開けませんでした。データベースが未作成の場合は、下の SQL で作成できます。";

    /// <summary>検証失敗の案内: 分類できない失敗（生メッセージ + 汎用案内のみ——database.md §6.1）。</summary>
    public const string PromotionFailureUnclassifiedGuidance =
        "接続できませんでした。次のエラーメッセージを確認してください。";

    /// <summary>
    /// 修復 SQL のブロック見出し（用語対応表 ui.md §7.2——このサーバは実行しない・
    /// SQL Server 側で実行する旨を見出しで伝える）。
    /// </summary>
    public const string PromotionRemediationSqlLabel =
        "解決するための SQL（このサーバは実行しません。SSMS 等で SQL Server 側で実行してください）";

    /// <summary>退避先フォルダの入力ラベル（database.md §6.1——退避の選択時は必須）。</summary>
    public const string PromotionEvacuationDirectoryLabel = "退避先のフォルダ";

    /// <summary>退避先フォルダの入力例。</summary>
    public const string PromotionEvacuationDirectoryPlaceholder = @"例: D:\Backup\Yagura";

    /// <summary>退避の選択の確定ボタン（退避先の入力とセットで確定する）。</summary>
    public const string PromotionConfirmEvacuation = "この退避先で「退避」を選択する";

    /// <summary>
    /// 切替確定前の予告（database.md §6.1 の委任を ui.md §5.4 が確定した文言）。
    /// </summary>
    public const string PromotionSwitchWarning =
        "切り替えると、これまでに保存したログは移行機能の提供まで画面から参照できなくなります。" +
        "あとで参照する可能性がある場合は「退避」を選んでください。";

    /// <summary>旧・組み込み DB ファイルの処分: 退避。</summary>
    public const string PromotionDisposalEvacuate = "退避（指定した場所へ移動して保管する）";

    /// <summary>旧・組み込み DB ファイルの処分: 削除。</summary>
    public const string PromotionDisposalDelete = "削除";

    /// <summary>切替実行ボタン（破壊的操作。確認ダイアログ必須——ui.md §3.1）。</summary>
    public const string PromotionExecute = "切り替えを実行する";

    /// <summary>切替実行の確認ダイアログの見出し。</summary>
    public const string PromotionExecuteConfirmTitle = "保存先を SQL Server に切り替えます";

    /// <summary>切替実行の確認ダイアログの確認ボタン。</summary>
    public const string PromotionExecuteConfirmAction = "切り替える";

    // ---- circuit 管理画面（security.md §2.2。M8-4） ----

    /// <summary>一覧列: 接続元。</summary>
    public const string CircuitColumnRemote = "接続元";

    /// <summary>
    /// 一覧列: 接続の種別（管理 / 閲覧）。開発用語「リスナ」を画面に出さない（ui.md §7.1）。
    /// </summary>
    public const string CircuitColumnListener = "種別";

    /// <summary>一覧列: 確立時刻。</summary>
    public const string CircuitColumnOpenedAt = "接続した時刻";

    /// <summary>一覧列: 最終活動時刻。</summary>
    public const string CircuitColumnLastActivity = "最後に操作した時刻";

    /// <summary>リスナ表示: 管理。</summary>
    public const string CircuitListenerAdmin = "管理";

    /// <summary>リスナ表示: 閲覧（帰属不明も閲覧として表示する——安全側の扱いと揃える）。</summary>
    public const string CircuitListenerViewer = "閲覧";

    /// <summary>切断ボタン。</summary>
    public const string CircuitDisconnect = "切断";

    /// <summary>切断の確認ダイアログの見出し。</summary>
    public const string CircuitDisconnectConfirmTitle = "画面とサーバの接続を切断します";

    /// <summary>切断の確認ダイアログの要約（何が起きるか——ui.md §3.1 確認ダイアログ規約）。</summary>
    public const string CircuitDisconnectConfirmSummary =
        "選択した閲覧者の画面は接続終了の案内に切り替わります。ログの受信には影響しません。";

    /// <summary>切断の確認ダイアログの確認ボタン。</summary>
    public const string CircuitDisconnectConfirmAction = "切断する";

    /// <summary>切断要求の受理。</summary>
    public const string CircuitDisconnectAccepted = "切断しました。";

    /// <summary>切断要求の不成立（対象が既に終了している等）。</summary>
    public const string CircuitDisconnectNotAccepted =
        "切断できませんでした。対象の接続が既に終了しているか、切断を受け付けられない状態です。";

    // ---- フォワーダキット生成画面（ADR-0008。/admin/forwarder-kit） ----

    /// <summary>画面見出し・ナビリンク文言。</summary>
    public const string ForwarderKitTitle = "フォワーダ配布キットの生成";

    /// <summary>画面の説明文（何をする画面か）。</summary>
    public const string ForwarderKitIntro =
        "Windows イベントログを転送する Fluent Bit 配布キットを、このサーバの宛先を設定済みの状態で生成します。" +
        "生成した ZIP は install.ps1 をパラメータなしで実行できます。";

    /// <summary>宛先選択の見出し。</summary>
    public const string ForwarderKitDestinationTitle = "宛先";

    /// <summary>
    /// 候補への既定選択なしの注記（ADR-0008 設計条件 1——到達可能性の判断責任は管理者に残る）。
    /// </summary>
    public const string ForwarderKitDestinationNote =
        "既定の選択はありません。端末から到達できるアドレスかどうかの判断は管理者に委ねられます" +
        "（ループバック・リンクローカル・無効な NIC は候補から除外済み）。";

    /// <summary>候補が 1 件もない場合の案内。</summary>
    public const string ForwarderKitNoCandidates = "候補となるアドレスが見つかりませんでした。手入力で指定してください。";

    /// <summary>手入力の選択肢ラベル。</summary>
    public const string ForwarderKitManualEntryOption = "手入力";

    /// <summary>手入力欄のラベル。</summary>
    public const string ForwarderKitManualEntryLabel = "宛先ホスト（IP アドレスまたはホスト名）";

    /// <summary>ポート入力のラベル。</summary>
    public const string ForwarderKitPortLabel = "ポート";

    // ---- 転送方式（Issue #156: Udp（既定）/ Tcp。TLS 送信はキットから除外——オーナー決定 2026-07-11） ----

    /// <summary>転送方式選択の見出し。</summary>
    public const string ForwarderKitModeTitle = "転送方式";

    /// <summary>転送方式選択の説明。</summary>
    public const string ForwarderKitModeNote =
        "UDP は既定で単純ですが、MTU を超えるイベントは断片化により失われることがあります。" +
        "TCP は断片化損失を避けられますが、注意点があります（選択時に表示）。";

    /// <summary>転送方式: UDP（既定）。</summary>
    public const string ForwarderKitModeUdp = "UDP（既定）";

    /// <summary>転送方式: TCP。</summary>
    public const string ForwarderKitModeTcp = "TCP";

    /// <summary>TCP 選択時の注記（RFC 6587 octet-counting 非対応の制約——Issue #156）。</summary>
    public const string ForwarderKitModeTcpNote =
        "Fluent Bit の out_syslog は TCP で RFC 6587 の octet-counting に対応していません（LF 区切り）。" +
        "複数行を含むイベント本文（Security 監査ログ等）が複数レコードに分かれて届く場合があります。";

    /// <summary>チャネル選択の見出し。</summary>
    public const string ForwarderKitChannelsTitle = "収集チャネル";

    /// <summary>チャネル: System。</summary>
    public const string ForwarderKitChannelSystem = "System";

    /// <summary>チャネル: Application。</summary>
    public const string ForwarderKitChannelApplication = "Application";

    /// <summary>チャネル: Security。</summary>
    public const string ForwarderKitChannelSecurity = "Security";

    /// <summary>
    /// Security チャネル有効化時の警告（ADR-0008 設計条件 2——機微情報・量の注意）。
    /// </summary>
    public const string ForwarderKitSecurityChannelWarning =
        "Security チャネルは機微情報を含み、イベント量も多くなります。組織のポリシーで明示的に判断してから有効化してください。";

    /// <summary>生成される ZIP の内容一覧の見出し。</summary>
    public const string ForwarderKitContentsTitle = "生成される ZIP の内容";

    /// <summary>検証済み Fluent Bit 版の表示（{0} に版番号が入る）。</summary>
    public const string ForwarderKitVerifiedVersionFormat = "検証済み Fluent Bit 版: {0}";

    /// <summary>MSI 非同梱の注記。</summary>
    public const string ForwarderKitMsiNotIncludedNote =
        "MSI は含まれません。README の手順に従って packages.fluentbit.io から取得し、SHA256 で検証してください。";

    /// <summary>生成ボタン。</summary>
    public const string ForwarderKitGenerateButton = "キットを生成してダウンロード";

    /// <summary>宛先未選択時のエラー。</summary>
    public const string ForwarderKitErrorDestinationRequired = "宛先を選択または入力してください。";

    /// <summary>宛先の文字種エラー。</summary>
    public const string ForwarderKitErrorDestinationInvalid = "宛先に使える文字は英数字・ピリオド・ハイフン・コロンのみです。";

    /// <summary>ポート範囲エラー。</summary>
    public const string ForwarderKitErrorPortRange = "ポートは 1〜65535 の範囲で指定してください。";

    /// <summary>チャネル未選択エラー。</summary>
    public const string ForwarderKitErrorChannelsRequired = "収集チャネルを 1 つ以上選択してください。";

    // ---- MSI オプトイン同梱（ADR-0008 設計条件 9） ----

    /// <summary>MSI 同梱セクションの見出し。</summary>
    public const string ForwarderKitMsiSectionTitle = "MSI の同梱（任意）";

    /// <summary>配置フォルダのフルパス表示の形式。{0} にフルパスが入る。</summary>
    public const string ForwarderKitMsiFolderPathFormat = "配置フォルダ: {0}";

    // ---- 収集対象端末のアーキ選択（ADR-0009 決定7・委任 #4） ----

    /// <summary>収集対象端末のアーキ選択の見出し。</summary>
    public const string ForwarderKitMsiArchitectureTitle = "収集対象端末のアーキテクチャ";

    /// <summary>アーキ選択の説明（MSI 同梱時のみ意味を持つ——構成の残り（宛先・チャネル）は共通）。</summary>
    public const string ForwarderKitMsiArchitectureNote =
        "同梱する MSI は、この配布キットの導入先端末のアーキテクチャに合わせて選んでください。迷ったら x64 を選んでください。";

    /// <summary>アーキ選択肢: x64。</summary>
    public const string ForwarderKitMsiArchitectureX64 = "x64（対応・既定）";

    /// <summary>アーキ選択肢: ARM64。</summary>
    public const string ForwarderKitMsiArchitectureArm64 = "ARM64（試験的。Windows 11 on Arm 等）";

    /// <summary>MSI 未検出時の案内の形式。{0} に期待ファイル名パターンが入る。</summary>
    public const string ForwarderKitMsiNotFoundFormat =
        "MSI 未検出。ここに {0} を配置すると、生成する ZIP に MSI を同梱できます（任意）。";

    /// <summary>MSI 同梱チェックボックスのラベル。</summary>
    public const string ForwarderKitMsiIncludeCheckbox = "MSI を同梱する";

    /// <summary>検出 MSI のファイル名表示の形式。{0} にファイル名が入る。</summary>
    public const string ForwarderKitMsiDetectedFileNameFormat = "検出したファイル: {0}";

    /// <summary>検出 MSI の版表示の形式。{0} に版が入る（取得不能時は不明）。</summary>
    public const string ForwarderKitMsiDetectedVersionFormat = "版: {0}";

    /// <summary>版取得不能時の表示。</summary>
    public const string ForwarderKitMsiVersionUnknown = "不明（ファイル名から推定した値を補助的に使用）";

    /// <summary>検出 MSI の SHA256 表示の形式。{0} にハッシュ値が入る。</summary>
    public const string ForwarderKitMsiSha256Format = "SHA256: {0}";

    /// <summary>公式ハッシュとの照合結果: 一致。</summary>
    public const string ForwarderKitMsiOfficialHashMatch = "公式配布 SHA256 と一致しました。";

    /// <summary>公式ハッシュとの照合結果: 不一致。</summary>
    public const string ForwarderKitMsiOfficialHashMismatch =
        "公式配布 SHA256 と一致しませんでした。取得元・改ざんの有無を確認してください。";

    /// <summary>公式ハッシュとの照合結果: 未確認（Yagura に公式ハッシュ未設定）。</summary>
    public const string ForwarderKitMsiOfficialHashUnverified =
        "公式配布 SHA256 との照合は未実施です（Yagura に公式ハッシュが未設定のため）。";

    /// <summary>版不一致の警告の形式。{0} に検出版、{1} に検証済み版が入る。</summary>
    public const string ForwarderKitMsiVersionMismatchWarningFormat =
        "検出した MSI の版（{0}）は検証済み版（{1}）と異なります。動作未検証の組み合わせになる可能性があります。";

    /// <summary>版不一致時の二段階確認チェックボックスのラベル（設計条件 9）。</summary>
    public const string ForwarderKitMsiVersionMismatchAcknowledge =
        "版が異なることを理解した上で、この MSI を同梱します";

    /// <summary>複数 MSI 検出時のエラー見出し。</summary>
    public const string ForwarderKitMsiMultipleErrorTitle = "複数の MSI が見つかりました。1 つだけ残してください。";

    /// <summary>複数 MSI 検出時、検出一覧の見出し。</summary>
    public const string ForwarderKitMsiMultipleListTitle = "検出したファイル:";

    /// <summary>同梱選択時の ZIP サイズ予告。</summary>
    public const string ForwarderKitMsiSizeNotice = "MSI を同梱すると、生成する ZIP のサイズは 20 MB を超える見込みです。";

    /// <summary>版不一致の確認未了エラー（生成ボタン押下時）。</summary>
    public const string ForwarderKitErrorMsiVersionMismatchNotAcknowledged =
        "MSI の版が検証済み版と異なります。同梱するには確認チェックを入れてください。";

    // ---- メール通知（ADR-0017。Issue #350） ----

    public const string EmailNotificationTitle = "メール通知";

    public const string EmailNotificationIntro =
        "スプールの上限接近・証明書の期限接近・認証攻撃の予兆といった運用上の警告を、メールで受け取ります。"
        + "既定では無効です。監視基盤をお持ちの場合は、Windows イベントログ（ソース: Yagura）を"
        + "そのまま監視するほうが確実です。";

    /// <summary>メールが正本ではないことの常設注記（決定 5 の at-most-once を隠さない）。</summary>
    public const string EmailNotificationAtMostOnceNote =
        "通知メールは配送を保証しません（送信できなかった通知は 1 回だけ再試行し、それでも失敗した場合は破棄します）。"
        + "「メールが来ない ＝ 正常」とは限りません。すべての事象は Windows イベントログに記録され、そちらが正本です。"
        + "チャネルが静かに壊れていないかは、下の「送信状況」で確認してください。";

    public const string EmailNotificationEnabledLabel = "メール通知を有効にする";

    public const string EmailNotificationFromLabel = "差出人アドレス";
    public const string EmailNotificationFromHelp =
        "SMTP サーバが送信を許可しているアドレスを指定してください（例: yagura@example.co.jp）。";

    public const string EmailNotificationToLabel = "宛先アドレス";
    public const string EmailNotificationToHelp =
        "1 行に 1 件で入力してください。すべての宛先に同じ内容が送られます（宛先ごとの振り分けはできません）。";

    public const string EmailNotificationSmtpTitle = "SMTP サーバ";
    public const string EmailNotificationSmtpHostLabel = "ホスト名";
    public const string EmailNotificationSmtpPortLabel = "ポート";
    public const string EmailNotificationSmtpPortHelp = "既定は 25 です。STARTTLS を使う場合は 587 が一般的です。";

    public const string EmailNotificationSecurityLabel = "暗号化（STARTTLS）";
    public const string EmailNotificationSecurityNone = "なし（平文で送信する）";
    public const string EmailNotificationSecurityAuto = "自動（対応していれば暗号化する）";
    public const string EmailNotificationSecurityRequired = "必須（暗号化できなければ送信しない）";
    public const string EmailNotificationSecurityHelp =
        "「自動」は、サーバが暗号化に対応していない場合は平文のまま送信します。"
        + "経路に信頼できない区間がある場合は「必須」を選んでください。";

    public const string EmailNotificationAuthTitle = "SMTP 認証（任意）";
    public const string EmailNotificationAuthHelp =
        "ユーザー名とパスワードの両方を入力したときだけ認証します。片方だけでは保存できません"
        + "（認証なしの送信に黙って切り替わることを避けるためです）。";

    public const string EmailNotificationUsernameLabel = "ユーザー名";
    public const string EmailNotificationPasswordLabel = "パスワード";
    public const string EmailNotificationPasswordHelpConfigured =
        "保存済みです。変更する場合のみ入力してください（空欄のままなら現在の値を維持します）。";
    public const string EmailNotificationPasswordHelpUnset =
        "入力した値は暗号化して保存します（このサーバでのみ復号できます）。画面に再表示することはありません。";

    /// <summary>
    /// 保存済みパスワードの明示的な削除（PR #366 レビュー対応）。「空欄 = 変更しない」に固定した
    /// 帰結として、削除には専用の口が要る——これがないと SMTP 認証をやめる操作ができない。
    /// </summary>
    public const string EmailNotificationClearPasswordLabel = "保存済みのパスワードを削除する";
    public const string EmailNotificationClearPasswordHelp =
        "SMTP 認証をやめる場合は、ユーザー名を空にしたうえでこれを有効にして適用してください。";

    /// <summary>決定 3 の能動警告。STARTTLS ストリップで漏れるのが「資格情報」であることを明示する。</summary>
    public const string EmailNotificationPlaintextCredentialWarning =
        "パスワードを設定していますが、暗号化が「必須」になっていません。"
        + "経路上で暗号化が剥がされた場合、漏れるのは通知の内容ではなく SMTP の資格情報です"
        + "（多くの環境では AD のアカウントと同じものです）。暗号化を「必須」にすることを強く推奨します。";

    public const string EmailNotificationTestTitle = "テスト送信";
    public const string EmailNotificationTestIntro =
        "この画面に入力中の値で 1 通だけ送信します（保存前でも試せます）。"
        + "テスト送信は通知の送信数の上限を消費しません。";
    public const string EmailNotificationTestButton = "テスト送信する";
    public const string EmailNotificationTestSending = "送信中…";
    public const string EmailNotificationTestCancelButton = "中止する";

    /// <summary>テスト送信で保存済みパスワードが使われることの明示（決定 8）。</summary>
    public const string EmailNotificationTestUsesStoredPassword =
        "パスワード欄が空欄のため、保存済みのパスワードを使って送信します。";

    public const string EmailNotificationTestRejectedRecipientsFormat =
        "次の宛先はサーバに受理されませんでした: {0}";

    public const string EmailNotificationHealthTitle = "送信状況";
    public const string EmailNotificationHealthLastSuccess = "最終送信成功";
    public const string EmailNotificationHealthLastFailure = "直近の失敗";
    public const string EmailNotificationHealthQueueDepth = "送信待ち";
    /// <summary>
    /// キュー溢れ・流量上限時の押しのけ・再試行の投入不能の 3 経路の合計（Issue #371——
    /// 「キュー溢れ」と限定して表示しない。内訳は EmailNotificationQueue.DroppedCount 参照）。
    /// </summary>
    public const string EmailNotificationHealthDropped = "送信されず破棄した通知";
    public const string EmailNotificationHealthSuppressed = "抑制した通知";
    public const string EmailNotificationHealthNever = "なし";
    public const string EmailNotificationHealthCountFormat = "{0} 件";

    /// <summary>抑制の内訳（回数だけでは「何が届かなかったか」が分からない——決定 5）。</summary>
    public const string EmailNotificationHealthSuppressedBreakdownTitle = "抑制された事象の内訳（イベント ID 別）";
    public const string EmailNotificationHealthSuppressedNote =
        "同じ事象の通知は 1 時間に 1 通までに畳まれます。全体では 1 時間に 10 通までです"
        + "（エラーはこの上限の対象外です）。抑制された事象も Windows イベントログには記録されています。";

    /// <summary>「有効にしたつもりで送られていない」状態——画面上で最も目立たせる（決定 2）。</summary>
    public const string EmailNotificationDisabledByInvalidConfiguration =
        "メール通知は有効になっていますが、設定に不備があるため送信されていません。"
        + "この画面で設定を保存し直すと、不備の内容がその場で表示されます。";

    public const string EmailNotificationSavedNoChanges = "変更はありませんでした（保存していません）。";
    public const string EmailNotificationSavedFormat = "メール通知の設定を保存しました（変更: {0}）。次回の通知から反映されます。";

    /// <summary>無関係キーの不正で設定ファイル全体の検証が失敗している状態の表示（Issue #370）。</summary>
    public const string EmailNotificationConfigurationFileErrorIntro =
        "設定ファイルにメール通知とは別の問題があり、実効状態を判定できません。"
        + "この画面での保存はできますが、送信側への反映は問題の解消後の再読み込みまで見送られます。検証エラー:";

    public const string EmailNotificationSaveFailedFormat = "保存できませんでした: {0}";

    // ------------------------------------------------------------------
    // 送信元の途絶検知（ADR-0018。/admin/source-silence。Issue #351）
    // ------------------------------------------------------------------

    public const string SourceSilenceTitle = "送信元の途絶検知";
    public const string SourceSilenceIntro =
        "ウォッチリストに登録した送信元からの受信が、指定した時間を超えて途絶えると警告します"
        + "（Windows イベントログ。メール通知を有効にしていればメールでも届きます）。"
        + "登録していない送信元は対象になりません。";

    public const string SourceSilenceListTitle = "ウォッチリスト";
    public const string SourceSilenceListEmpty =
        "まだ登録がありません。下の「登録」から、受信実績のある送信元を選んで追加してください。";
    public const string SourceSilenceColumnAddress = "送信元アドレス";
    public const string SourceSilenceColumnLabel = "表示名";
    public const string SourceSilenceColumnThreshold = "閾値";
    public const string SourceSilenceColumnState = "状態";
    public const string SourceSilenceColumnActions = "操作";
    public const string SourceSilenceStateSilent = "途絶中";
    public const string SourceSilenceStateWatching = "監視中";

    /// <summary>保存済みだが稼働状態に現れない（設定不備でエントリ単位無効化された）エントリの表示。</summary>
    public const string SourceSilenceStateInactive = "無効（設定を確認）";

    /// <summary>未保存の追加・変更があるエントリの表示（適用までは監視されない）。</summary>
    public const string SourceSilenceStatePending = "未適用";

    /// <summary>閾値が手編集の省略により既定値で補完されているエントリの表示（決定 1 の識別表示）。</summary>
    public const string SourceSilenceThresholdDefaultedFormat = "{0}（既定値で補完）";

    public const string SourceSilenceEditButton = "編集";
    public const string SourceSilenceDeleteButton = "削除";

    public const string SourceSilenceAddTitle = "登録";
    public const string SourceSilenceAddIntro =
        "受信実績のある送信元から選ぶのが確実です（アドレスの打ち間違いは「登録したのに検知されない」"
        + "という気づきにくい失敗になります）。まだ受信のない送信元を先回りで登録する場合のみ手入力してください。";
    public const string SourceSilenceCandidateLabel = "受信実績のある送信元から選択";
    public const string SourceSilenceCandidateItemFormat = "{0}（最終受信 {1}・累計 {2} 件）";
    public const string SourceSilenceCandidateRegisteredSuffix = "（登録済み）";
    public const string SourceSilenceManualAddressLabel = "送信元アドレス（手入力）";
    public const string SourceSilenceManualAddressHelp =
        "IPv4 / IPv6 アドレスを入力します。先回り登録した送信元は、登録時点から閾値の時間だけ受信がないと警告されます"
        + "——機器側の設定漏れや経路の未開通の検出に使えます。";
    public const string SourceSilenceLabelLabel = "表示名（任意）";
    public const string SourceSilenceLabelHelp = "どの装置かを自分の言葉で残せます（警告と一覧に表示されます）。";
    public const string SourceSilenceThresholdLabel = "閾値";
    public const string SourceSilenceThresholdHelp =
        "この時間を超えて受信がないと警告します。短すぎると装置の送信間隔やジッタで誤検知になります"
        + "（例: 24 時間 = 1 日 1 回でもログを送る装置向け）。"
        + "既存エントリの編集で空にすると、既定値の明示指定に変えず「省略のまま」を保ちます。";
    public const string SourceSilenceUnitMinutes = "分";
    public const string SourceSilenceUnitHours = "時間";
    public const string SourceSilenceUnitDays = "日";
    public const string SourceSilenceAddButton = "一覧に追加";
    public const string SourceSilenceUpdateButton = "一覧を更新";

    public const string SourceSilenceApplyButton = "適用する";
    public const string SourceSilencePendingChangesNote =
        "一覧への追加・編集・削除は「適用する」を押すまで保存されません。";
    public const string SourceSilenceSavedFormat = "ウォッチリストを保存しました（追加 {0} 件・削除 {1} 件・変更 {2} 件）。再起動なしで反映されます。";
    public const string SourceSilenceSavedNoChanges = "変更はありませんでした（保存していません）。";
    public const string SourceSilenceLimitFormat = "登録は {0} 件までです（現在 {1} 件）。";

    /// <summary>ダッシュボード UI-4: ウォッチリスト登録済みの送信元のマーク（ADR-0018 決定 4）。</summary>
    public const string SourceWatchRegisteredChip = "監視中";

    /// <summary>ダッシュボード UI-4: 途絶中と判定されている送信元の強調（ADR-0018 決定 4）。</summary>
    public const string SourceWatchSilentChip = "途絶中";
}
