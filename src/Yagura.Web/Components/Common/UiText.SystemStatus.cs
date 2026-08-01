namespace Yagura.Web.Components.Common;

public static partial class UiText
{
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

    // ---- 蓄積ログの移行（database.md §6.2。DB-5）のシステムイベント平易語 ----

    /// <summary>システムイベント平易語: 蓄積ログ移行の完了記録。</summary>
    public const string EventKindMigrationImport = "蓄積ログの移行（移行されたログの期間）";

    // ---- カウンタ平易語・ゲージ・履歴（続き） ----

    /// <summary>カウンタ平易語: 流量制御破棄（判定・破棄の実装により実値を刻む）。</summary>
    public const string CounterFlowControlDropped = "取りこぼし（送信元ごとの受信量の制限）";

    /// <summary>
    /// カウンタ平易語: スプール末尾破損破棄。他の取りこぼし系カウンタと異なり
    /// 単位がバイト（レコード単位では数えられないため）——値の桁が他行と並ばないことでの
    /// 誤解を避けるため、ラベル自体に単位を明記する。
    /// </summary>
    public const string CounterSpoolCorruptTailDiscarded = "取りこぼし（一時保管ファイルの末尾破損。単位はバイト）";

    /// <summary>カウンタ平易語: 逆引き解決成功（ui.md §7.2）。</summary>
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

    /// <summary>受信断履歴: リスナ再構成（設定の再読み込み）による瞬断の種別表示。</summary>
    public const string OutageKindListenerReconfigure = "設定反映（リスナ再構成）による";

    /// <summary>受信断履歴: bind 失敗から再試行での受信再開までの種別表示。</summary>
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
}
