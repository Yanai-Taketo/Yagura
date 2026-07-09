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
}
