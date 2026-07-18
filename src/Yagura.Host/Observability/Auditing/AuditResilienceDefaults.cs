namespace Yagura.Host.Observability.Auditing;

/// <summary>
/// 監査チャネル障害中の事象保持・書き戻し（SEC-10。security.md §4.2。Issue #269）の仮値。
/// </summary>
/// <remarks>
/// <b>保持上限・スキャン間隔は仮値である</b>（security.md §7 SEC-10）。確定は実装設計で行う
/// （無制限保持は OOM を招くため上限は必須）。確定するまで設定キーは設けない
/// （<see cref="CircuitGovernanceDefaults"/>・<see cref="AuditAggregationDefaults"/> と同じ方針）。
/// </remarks>
internal static class AuditResilienceDefaults
{
    /// <summary>
    /// メモリ内に保持する監査事象の上限（仮値 1000）。1 事象あたり数百バイト程度のため 1000 件でも
    /// 1MB 未満に収まる。超過分は縮退（古い側を残し、到来した新しい事象を破棄——障害の起点と
    /// 「欠落し得た期間」の開始を保全する）し、破棄件数は必ず計上する（復旧サマリ 3013 + ライブ計器
    /// <c>yagura.web.audit.buffer_dropped</c>）。
    /// </summary>
    public const int MaxBufferedEvents = 1000;

    /// <summary>
    /// 障害中に新規事象が来なくてもチャネル復旧を検知するための周期スキャン間隔（仮値 30 秒）。
    /// 復旧サマリ（3013）出力の遅延はこの間隔に依存する。
    /// </summary>
    public static readonly TimeSpan RecoveryScanInterval = TimeSpan.FromSeconds(30);
}
