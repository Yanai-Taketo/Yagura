namespace Yagura.Host.Observability.ActiveNotification.SourceSilence;

/// <summary>
/// 送信元の途絶検知（ADR-0018）の暫定定数。
/// </summary>
/// <remarks>
/// <see cref="ActiveNotificationConstants"/> と同じ運用——すべて実測で確定するまでの暫定値。
/// 本クラスの値は configuration.md §9 の確定待ち一覧（CF-x）に登録して追跡する
/// （ADR-0018 委任 2）。<b>メール通知の閾値〔<c>EmailNotificationConstants</c>〕とは扱いが違う</b>
/// ——あちらは「設定キーとして公開しない」と決めた値だが、こちらは
/// <see cref="DefaultThresholdMinutes"/> と上限が利用者から見える挙動を直接決めるため、
/// 実運用で確定させる対象として §9 に載せる。
/// </remarks>
internal static class SourceSilenceConstants
{
    /// <summary>
    /// ウォッチリストの登録上限（仮値 1000 件。ADR-0018 決定 1）。
    /// </summary>
    /// <remarks>
    /// 有界化はこの上限が担う。当初案 100 件は「数百台の配布現場・通信事業者規模で初日から
    /// 不足する」というレビュー指摘を受けて引き上げた——エントリ実体は数十バイト・評価は
    /// O(n) の時刻比較のみであり、技術コストは 100 件と変わらない。
    /// </remarks>
    internal const int MaxWatchlistEntries = 1000;

    /// <summary>
    /// 閾値を省略したエントリの補完値（仮値 1440 分 = 24 時間。ADR-0018 決定 1）。
    /// </summary>
    internal const int DefaultThresholdMinutes = 1440;

    /// <summary>
    /// エントリ閾値の下限（仮値 10 分）。評価周期 1 分 + 送信ジッタを考慮した値。
    /// </summary>
    internal const int MinThresholdMinutes = 10;

    /// <summary>エントリ閾値の上限（仮値 43200 分 = 30 日）。</summary>
    internal const int MaxThresholdMinutes = 43200;

    /// <summary>
    /// 同一エントリの再発火を律速する抑制窓（仮値 15 分。ADR-0018 決定 3）。
    /// </summary>
    /// <remarks>
    /// <b>粒度がエントリ別であることが既存の抑制窓との違い</b>
    /// （<see cref="ActiveNotificationConstants.SuppressionWindow"/> はトリガ別）——
    /// 装置 A の発火が装置 B の初報を飲まないようにするため、新設の窓とする。
    /// </remarks>
    internal static readonly TimeSpan EntrySuppressionWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// 起動時 seed（ADR-0018 決定 3。Issue #381）の DB 照会タイムアウト（仮値 5 秒。
    /// 設定画面の候補照会と同じ水準）。超過時は seed を行わず起動時刻仮基準へフォールバックする。
    /// </summary>
    internal static readonly TimeSpan SeedQueryTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 同一評価周期にこの件数以上が途絶へ遷移したら、個別警告ではなく 1 件の集約警告にする
    /// （仮値 5 件。ADR-0018 決定 3）。
    /// </summary>
    /// <remarks>
    /// 集約スイッチ障害等で 50 台が同時に黙ったとき、個別 50 件（メール接続時は 50 通）は
    /// 診断情報としても劣化している。
    /// </remarks>
    internal const int BurstAggregationThreshold = 5;
}
