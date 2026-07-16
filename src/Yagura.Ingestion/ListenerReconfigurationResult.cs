namespace Yagura.Ingestion;

/// <summary>
/// 無瞬断リスナ再構成（CF-4 層2。Issue #262）1 回の結果。UDP・TCP それぞれの帰結を持つ。
/// </summary>
/// <param name="Udp">UDP リスナの帰結。</param>
/// <param name="Tcp">TCP リスナの帰結。</param>
public sealed record ListenerReconfigurationResult(
    ListenerReconfigurationOutcome Udp,
    ListenerReconfigurationOutcome Tcp);

/// <summary>1 リスナぶんの再構成の帰結。</summary>
/// <param name="Status">帰結の分類。</param>
/// <param name="GapStartedAt">受信断（瞬断）の開始時刻（UTC。旧リスナの停止開始）。変更なしの場合は <see langword="null"/>。</param>
/// <param name="GapEndedAt">受信断の終了時刻（UTC。新リスナ（または復旧した旧構成リスナ）の受信開始）。リスナ停止中は <see langword="null"/>。</param>
/// <param name="Error">新構成での bind 失敗の原因（<see cref="ListenerReconfigurationStatus.RolledBack"/> / <see cref="ListenerReconfigurationStatus.DownRetrying"/> のとき）。</param>
public sealed record ListenerReconfigurationOutcome(
    ListenerReconfigurationStatus Status,
    DateTimeOffset? GapStartedAt = null,
    DateTimeOffset? GapEndedAt = null,
    string? Error = null);

/// <summary>再構成の帰結の分類。</summary>
public enum ListenerReconfigurationStatus
{
    /// <summary>options に変更がなくリスナに触れていない（瞬断なし）。</summary>
    NotChanged,

    /// <summary>新構成で受信を再開した。</summary>
    Reconfigured,

    /// <summary>新構成の bind に失敗し、旧構成で受信を復旧した（configuration.md §3——失敗時は旧構成の維持）。</summary>
    RolledBack,

    /// <summary>新構成・旧構成とも bind に失敗し、リスナは停止中。CF-6 の定期再試行が継続している。</summary>
    DownRetrying,
}
