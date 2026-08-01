namespace Yagura.Web.Components.Common;

public static partial class UiText
{
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

    /// <summary>時間軸チャート中央の操作案内（棒ホバーで時間帯 + 件数が出る旨）。</summary>
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
    /// 一時保管への退避カードの補足（退避が現在も進行中の場合）。累計値は監査上の
    /// 価値があるため残しつつ、「今」の状態が復帰したことを見分けられるようにする
    /// （ui.md §5.4「一時保管中の表示」の裏返し——進行中でなくなったら静かに戻す）。
    /// </summary>
    public const string StatSpoolEvacuatedOngoingSupplement = "現在も一時保管への退避が進行中です";

    /// <summary>
    /// 一時保管への退避カードの補足（過去に退避があったが、現在は消化完了（DB 格納済み）の場合）。
    /// 「一生画面に表示されっぱなし」という誤解を防ぐ——累計は過去分であり、
    /// 現在は正常に戻っていることを明示する）。
    /// </summary>
    public const string StatSpoolEvacuatedResolvedSupplement = "退避分は格納済み（現在は正常です）";

    /// <summary>ダッシュボードの現在値カード: 取りこぼし（累計。破棄系カウンタの合計）。</summary>
    public const string StatLossTotal = "取りこぼし（累計）";

    /// <summary>
    /// 取りこぼしカードの補足（取りこぼしがある場合のみ表示）。{0} にこの画面を開いてからの増分。
    /// 累計値は保存件数と並ぶと「大半を捨てている」ように見える（試用フィードバックで判明——
    /// 累計 37,529 対 保存 736）。累計はサーバ起動からの running total であること、および
    /// 「今も増えているか（＝進行中か過去か）」を開いてからの増分で示し、過去の一時的な取りこぼしと
    /// 現在進行中の取りこぼしを見分けられるようにする。
    /// </summary>
    public const string StatLossTotalSupplementFormat = "サーバ起動からの累計。この画面を開いてからは +{0} 件";

    /// <summary>ダッシュボードの現在値カード: 保存済みログ件数。</summary>
    public const string StatStoredRecords = "保存済みログ件数";

    /// <summary>ダッシュボードから状態画面への導線。</summary>
    public const string StatLinkToStatus = "すべてのカウンタ・記録を見る（システム状態）";

    // ---- 重大度分布・Top talkers（ui.md §4。M8-5） ----

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

    // ---- 流量制限の発火上位送信元（カード型で表示） ----

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
}
