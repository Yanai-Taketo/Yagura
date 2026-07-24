using Microsoft.Extensions.Logging;

namespace Yagura.Host.Observability.ActiveNotification;

/// <summary>
/// architecture.md §4.6 の能動通知（Windows イベントログの 1000 番台区画。security.md §4.3
/// 「1000 番台 = 運用警告」）に割り当てるイベント ID の初版（Issue #149 実装 PR。SEC-5 の
/// 1000 番台側の初回記録）。
/// </summary>
/// <remarks>
/// <para>
/// <b>1001 は既存実装（M3-2。Program.cs のスプールなし縮退運転起動警告）への遡及割当</b>:
/// 本 PR 以前は <c>EventId</c> を明示指定していなかったため、Windows イベントログ上の
/// イベント ID は既定値 0 のまま出力されていた（<c>Microsoft.Extensions.Logging.EventLog</c>
/// は <c>EventId</c> 未指定時に 0 を使う）。§4.6 の 1000 番台区画を実際に配線する本 PR で
/// まとめて確定させる。
/// </para>
/// <para>
/// <b>1005（スプール書込失敗）は本クラスに含まない</b>: 発火箇所が
/// <c>Yagura.Ingestion.Persistence.PersistenceWriter</c>（Yagura.Host が参照する下流プロジェクト）
/// であり、Yagura.Host → Yagura.Ingestion の参照方向を逆転させられないため、
/// <see cref="Yagura.Ingestion.Persistence.PersistenceEventIds.SpoolWriteFailed"/>
/// （ID 1005）として当該プロジェクト側に定義する。番号は本クラスと同じ 1000 番台区画から
/// 連番で採番し、意味の一覧は security.md §4.3 の表に統合して記録する（コードの定義場所が
/// 2 プロジェクトに分かれても、番号の正本は security.md の表とする）。
/// </para>
/// <para>
/// <b>additive-only</b>（security.md §4.3）: 一度公開した ID の意味とレベルは変えない。
/// 以降の追加は 1009 以降を使う。
/// </para>
/// </remarks>
public static class ActiveNotificationEventIds
{
    /// <summary>スプールなし縮退運転での起動（architecture.md §1.2）。レベル: 警告。</summary>
    public static readonly EventId SpoolDegradedStartup = new(1001, "SpoolDegradedStartup");

    /// <summary>スプール使用量が上限に接近（architecture.md §4.6・§9 M-16）。レベル: 警告。</summary>
    public static readonly EventId SpoolQuotaNearLimit = new(1002, "SpoolQuotaNearLimit");

    /// <summary>スプール使用量が上限に到達（architecture.md §3.2.3・§4.6）。レベル: 警告。</summary>
    public static readonly EventId SpoolQuotaReached = new(1003, "SpoolQuotaReached");

    /// <summary>スプールへの退避が継続している（architecture.md §3.2.2・§4.6）。レベル: 警告。</summary>
    public static readonly EventId SpoolEvacuationContinuing = new(1004, "SpoolEvacuationContinuing");

    // 1005 = SpoolWriteFailed は Yagura.Ingestion.Persistence.PersistenceEventIds 側に定義（上記 remarks 参照）。

    /// <summary>
    /// 監視対象ボリューム（データルート・スプール置き場所）の空き容量が閾値を下回った
    /// （architecture.md §4.6。database.md §3・§5.3。スプール置き場所のボリュームを対象に含めるのは
    /// PR #188 レビュー指摘への対応——`Spool:Directory` が別ドライブに向いた構成でも「夜間にスプールが
    /// 満ちていく」現場のボリュームを見逃さない）。レベル: 警告。
    /// </summary>
    public static readonly EventId MonitoredVolumeFreeSpaceLow = new(1006, "MonitoredVolumeFreeSpaceLow");

    /// <summary>SQL Server Express の DB サイズが上限に接近（database.md §5.3・architecture.md §4.6）。レベル: 警告。</summary>
    public static readonly EventId ExpressCapacityNearLimit = new(1007, "ExpressCapacityNearLimit");

    /// <summary>
    /// 能動通知の周期評価中に未捕捉例外が発生した（監視ループ自体は継続し、次周期で再試行する。
    /// PR #188 レビュー指摘への対応——監視自身が無警告で沈黙・停止する経路を残さない）。レベル: エラー
    /// （その周期の監視が実行できなかった = 部分的な機能停止を伴う事象。security.md §4.3 の割当方針）。
    /// </summary>
    public static readonly EventId EvaluationFailed = new(1008, "ActiveNotificationEvaluationFailed");

    /// <summary>
    /// スプールの定期自己検証（architecture.md §3.2.5。Issue #152）が失敗した——合成レコードの
    /// 投入自体に失敗した、または投入した合成レコードが期待時間内に drain へ合流判定されず、かつ
    /// 同じ期間内に drain の進捗（消化済みセグメント削除の累積カウンタ
    /// <see cref="Yagura.Storage.Spool.DiskSpool.DeletedSegmentsTotal"/> の増分）も観測されなかった
    /// （経路障害が疑われる。バックログ起因（<see cref="SpoolSelfTestTimeoutBacklog"/>）との判別は
    /// Issue #202。<see cref="ActiveNotificationConstants.SelfTestTimeout"/>）。レベル: エラー
    /// （障害時専用経路——スプール退避・drain——が平常時に検証できていない = 実障害時の救済経路が
    /// 機能する保証を失っている状態のため。security.md §4.3 の割当方針「機能停止を伴う事象」に
    /// 相当する）。1009 以降は additive-only（security.md §4.3）で本クラスに定義する最初の ID。
    /// </summary>
    public static readonly EventId SpoolSelfTestFailed = new(1009, "SpoolSelfTestFailed");

    /// <summary>
    /// スプールの定期自己検証（architecture.md §3.2.5。Issue #152）がタイムアウトしたが、同じ
    /// 期待時間内に drain の進捗（消化済みセグメント削除の累積カウンタの増分）を観測しており、
    /// 経路自体は生きていて未消化バックログの滞留（§3.2.2 が「隠れた欠陥ではない」正常な運用状態と
    /// 明記する持続的な速度不足）に起因すると判定できた場合の通知
    /// （PR #200 レビューのフォローアップ。Issue #202）。
    /// レベル: 警告（<see cref="SpoolEvacuationContinuing"/>（1004）と同じ「機能停止を伴わない、
    /// 対応が必要な運用状態の継続」区分——security.md §4.3 の割当方針）。<see cref="SpoolSelfTestFailed"/>
    /// （1009。進捗が観測されない場合）との住み分けと、一度 1009 へエスカレートした後は当該
    /// マーカーの追跡が終わるまで本 ID へ戻さないラッチ（振動防止。PR #211 レビュー対応）は
    /// <see cref="ActiveNotificationMonitor.EvaluateOnceAsync"/> の実装コメントを参照。
    /// additive-only（security.md §4.3）で 1009 の次に採番した ID。
    /// </summary>
    public static readonly EventId SpoolSelfTestTimeoutBacklog = new(1010, "SpoolSelfTestTimeoutBacklog");

    // 1011（管理 UI 認証 fail-closed）・1012（リモートバインド fail-closed）・1013（リモート HTTPS
    // 証明書の起動時解決失敗）は Yagura.Host.Configuration.ConfigurationEventIds 側に定義
    // （設定検証・起動時の事象であり周期監視の管轄ではないため。番号の正本は security.md §4.3 の表）。

    /// <summary>
    /// 管理リスナのリモート HTTPS 証明書の有効期限が接近している（ADR-0010 Phase 2 決定 4
    /// 「期限接近の事前警告は configuration.md §6 既存の能動通知がそのまま管理リスナ用証明書にも
    /// 適用される」の実体。閾値は
    /// <see cref="ActiveNotificationConstants.CertificateExpiryWarningWindow"/>——仮値
    /// 30 日）。レベル: 警告。
    /// </summary>
    public static readonly EventId AdminHttpsCertificateExpiryApproaching =
        new(1014, "AdminHttpsCertificateExpiryApproaching");

    /// <summary>
    /// 管理リスナのリモート HTTPS 証明書が稼働中に使用不能になった——証明書ストアからの削除・
    /// 秘密鍵アクセス不能・有効期限切れへの遷移（期限切れ中は Kestrel の
    /// <c>ServerCertificateSelector</c> が新規 TLS ハンドシェイクを拒否している状態。ADR-0010
    /// Phase 2 決定 4。PR #224 レビュー指摘 #3——起動時警告 1013 だけでは稼働中の異常が無音に
    /// なるギャップへの対応）。レベル: 警告（1013 と同じ判断——リモート HTTPS 面のみの縮退であり、
    /// loopback 経由の管理リスナ・syslog 受信は影響を受けないため、「機能停止を伴う事象 = エラー」
    /// までは引き上げない）。
    /// </summary>
    public static readonly EventId AdminHttpsCertificateUnavailableWhileRunning =
        new(1015, "AdminHttpsCertificateUnavailableWhileRunning");

    // 1016（TLS 受信証明書の起動時解決失敗）は Yagura.Host.Configuration.ConfigurationEventIds
    // 側に定義（設定検証・起動時の事象であり周期監視の管轄ではないため。1013 と同じ住み分け）。

    /// <summary>
    /// TLS 受信（<c>Ingestion:Tls:Enabled</c>。RFC 5425。opt-in。security.md §6。Issue #137）の
    /// 証明書の有効期限が接近している。閾値は <see cref="AdminHttpsCertificateExpiryApproaching"/>
    /// と同じ <see cref="ActiveNotificationConstants.CertificateExpiryWarningWindow"/>
    /// （仮値 30 日）を流用する——TLS 受信・管理 UI HTTPS のいずれも「期限接近の事前警告」という
    /// 同じ目的の閾値であり、別の値を採用すべき設計上の根拠が無いため。レベル: 警告。
    /// </summary>
    public static readonly EventId IngestionTlsCertificateExpiryApproaching =
        new(1017, "IngestionTlsCertificateExpiryApproaching");

    /// <summary>
    /// TLS 受信証明書が稼働中に使用不能になった——証明書ストアからの削除・秘密鍵アクセス不能・
    /// 有効期限切れへの遷移（security.md §6）。<b>管理 UI HTTPS（1015）との非対称に注意</b>:
    /// TLS 受信はこの状態でも新規ハンドシェイクを拒否しない——起動時に読み込み済みの証明書を
    /// そのまま提示し続ける（「止めない」判断。TlsSyslogListener の remarks 参照）。本通知は
    /// 状態の可視化のみを目的とし、リスナの挙動を変えない。レベル: 警告（受信自体は継続——
    /// 1015 と異なり「機能停止を伴う事象」ではないため、他の 1000 番台の警告と同じ扱い）。
    /// </summary>
    public static readonly EventId IngestionTlsCertificateUnavailableWhileRunning =
        new(1018, "IngestionTlsCertificateUnavailableWhileRunning");

    /// <summary>
    /// アプリ独自認証の三層防御（バックオフ・IP レート制限・グローバルトークンバケット）のいずれかが
    /// 昇格閾値（仮値 15 分。<see cref="Yagura.Host.Administration.AdminAuthenticationDefaults.EscalationThreshold"/>）
    /// 以上継続して発動している（ADR-0011 決定 6）。レベル: 警告。
    /// </summary>
    public static readonly EventId AdminAuthFailureDefenseEscalated = new(1019, "AdminAuthFailureDefenseEscalated");

    /// <summary>
    /// フォワーダ MSI 配置フォルダの ACL 乖離——アップロード機能（<c>Admin:ForwarderKit:MsiUpload:Enabled</c>）が
    /// 無効なのに、サービス実行アカウントの書き込み ACE が残っている（閉じ忘れ。ADR-0020 決定 2・
    /// 委任 7。Issue #283。#171 の教訓——意図した ACL と実 ACL の乖離は検出されるまで気づかれない）。
    /// 起動時（Program の一回検査）と周期監視（<see cref="ActiveNotificationMonitor"/>）の両方から
    /// 発火する。レベル: 警告。採番: 1000 番台の次の空き 1033（1032 は
    /// <c>ConfigurationEventIds.ForwarderMsiUploadFailClosedStartupRejected</c> が使用）。
    /// </summary>
    public static readonly EventId ForwarderMsiFolderAclDrift = new(1033, "ForwarderMsiFolderAclDrift");

    /// <summary>
    /// フォワーダ MSI 配置フォルダの書き込み経路の開放が継続している——アップロード機能が有効で、
    /// 書き込み ACE の存在が継続判定期間
    /// （<see cref="ActiveNotificationConstants.ForwarderMsiOpenContinuationThreshold"/>。仮値 24 時間）
    /// 以上連続して観測された（ADR-0020 決定 2・委任 7。Issue #283）。設定上は期待どおりの状態で
    /// あり（常置運用は正当な選択）、「開いたままである」ことの定期リマインダとして専用の長い抑制窓
    /// （<see cref="ActiveNotificationConstants.ForwarderMsiOpenContinuationSuppressionWindow"/>。仮値 7 日）で
    /// 再表示する。レベル: <b>情報</b>（1029 に次ぐ 1000 番台の情報レベル——対応を要する異常ではなく
    /// 状態の可視化のため。「使うときだけ開く」運用の閉じ忘れの主たる検出手段——再レビュー クリス指摘 1）。
    /// </summary>
    public static readonly EventId ForwarderMsiWritePathOpenContinuing =
        new(1034, "ForwarderMsiWritePathOpenContinuing");
}
