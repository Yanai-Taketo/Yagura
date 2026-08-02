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
public static partial class UiText
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
    /// 「表示中の結果そのものが消えた」という誤読（試用フィードバックで判明——条件なしの
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
    /// circuit エラーにしないための最後の受け皿。
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

    // ---- 保存先到達不能時の閲覧側の縮退（Issue #500） ----

    /// <summary>
    /// 検索が保存先の到達不能で行えなかったときの表示（Issue #500）。
    /// **「該当なし」と読ませない**——0 件の結果表と同じ見え方になると、
    /// 「そのログは受信できていなかった」という誤った結論を招く。
    /// </summary>
    public const string SearchStorageUnavailableNotice =
        "保存先（データベース）に接続できていないため、ログを読み出せませんでした" +
        "（検索条件に該当するログが無いという意味ではありません）。";

    /// <summary>検索が行えなかったときの補足（受信は続いていること・次に見る場所）。</summary>
    public const string SearchStorageUnavailableSupplement =
        "受信は継続しており、保存できなかった分は一時保管へ退避されます。" +
        "退避の状況は状態画面のカウンタで確認できます。保存先が復旧すると再び検索できます。";


    // ---- 保持期間（ui.md §5.3・database.md §3。M8-3） ----

    /// <summary>
    /// 保持期間が未適用（不正値フォールバック = 削除しない。database.md §3）の場合の常時明示。
    /// </summary>
    public const string RetentionDisabledNotice =
        "古いログの自動削除は現在行われません（ログを保存しておく期間が設定されていないか、設定値が無効です）";


    // ---- 送信元の逆引きホスト名（ui.md §4） ----

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

    // ---- ファシリティ（syslog PRI の facility。ui.md §4） ----

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
}
