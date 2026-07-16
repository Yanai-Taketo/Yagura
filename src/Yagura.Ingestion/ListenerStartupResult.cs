namespace Yagura.Ingestion;

/// <summary>
/// 受信リスナ起動（<see cref="IngestionPipeline.StartListenerAsync"/>）の結果
/// （Issue #291。#141 原子的起動の反転——2026-07-16 オーナー裁定）。
/// 環境要因の bind 失敗はもはや起動全体を失敗させず、開けたリスナで縮小継続する。
/// </summary>
/// <param name="Udp">UDP リスナの帰結。</param>
/// <param name="Tcp">TCP リスナの帰結。</param>
/// <param name="Tls">TLS リスナの帰結（TLS 受信を構成していない場合は <see langword="null"/>）。</param>
public sealed record ListenerStartupResult(
    ListenerStartupOutcome Udp,
    ListenerStartupOutcome Tcp,
    ListenerStartupOutcome? Tls)
{
    /// <summary>1 本以上のリスナが開けず縮小継続中か（ホスト側の警告——EventId 1022——の入力）。</summary>
    public bool IsDegraded =>
        Udp.Status == ListenerStartupStatus.DegradedRetrying
        || Tcp.Status == ListenerStartupStatus.DegradedRetrying
        || Tls?.Status == ListenerStartupStatus.DegradedRetrying;
}

/// <summary>1 リスナぶんの起動の帰結。</summary>
/// <param name="Status">帰結の分類。</param>
/// <param name="Error">bind 失敗の原因（<see cref="ListenerStartupStatus.DegradedRetrying"/> のとき）。</param>
public sealed record ListenerStartupOutcome(
    ListenerStartupStatus Status,
    string? Error = null);

/// <summary>リスナ起動の帰結の分類。</summary>
public enum ListenerStartupStatus
{
    /// <summary>受信を開始した。</summary>
    Started,

    /// <summary>
    /// 環境要因（ポート競合・アドレス未確立等の <see cref="System.Net.Sockets.SocketException"/>）で
    /// bind できず、リスナは開かないまま縮小継続している。CF-6 の定期再試行が受信再開を試み続ける
    /// （configuration.md §4.1。成功時は <see cref="IngestionPipeline.ListenerBindRecovered"/> が発火する）。
    /// </summary>
    DegradedRetrying,
}

/// <summary>
/// bind 再試行（CF-6）による受信再開の通知（Issue #291）。ホスト側が受信断区間
/// （<c>downtime.listener-bind-retry</c>）のシステムイベント記録に使う。
/// </summary>
/// <param name="ProtocolLabel">リスナの別（"UDP" / "TCP" / "TLS"）。</param>
/// <param name="GapStartedAt">受信できなかった区間の開始（最初に bind を試みて失敗した時刻。UTC）。</param>
/// <param name="RecoveredAt">再試行が成功し受信を再開した時刻（UTC）。</param>
public sealed record ListenerBindRecovery(
    string ProtocolLabel,
    DateTimeOffset GapStartedAt,
    DateTimeOffset RecoveredAt);
