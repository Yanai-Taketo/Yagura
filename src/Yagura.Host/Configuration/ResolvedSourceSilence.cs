using System.Net;

namespace Yagura.Host.Configuration;

/// <summary>
/// ウォッチリストの 1 エントリ（検証済み）。
/// </summary>
/// <param name="Address">
/// 正規化済みの送信元アドレス。IPv4-mapped IPv6（<c>::ffff:192.0.2.1</c>）は IPv4 へ畳んで
/// 保持する——流量制御・Top talkers と同じ既存規約に合わせ、同一装置が 2 エントリに割れないようにする。
/// </param>
/// <param name="Label">表示名（任意）。通知の Detail と UI に出す。</param>
/// <param name="Threshold">
/// 実効閾値。エントリ個別指定があればそれ、なければ既定値
/// （<c>Notification:SourceSilence:DefaultThresholdMinutes</c>）。
/// </param>
/// <param name="ThresholdIsDefaulted">
/// 閾値が既定値で補完されたか。<b>補完エントリを識別可能にするための情報</b>——
/// 読み込み時に件数と一覧を情報レベルで記録し、設定画面のエントリ一覧では実効閾値を
/// 常時表示する（ADR-0018 決定 1。手編集の大量投入で省略が起きやすい点への補強）。
/// </param>
internal sealed record SourceSilenceWatchEntry(
    IPAddress Address,
    string? Label,
    TimeSpan Threshold,
    bool ThresholdIsDefaulted);

/// <summary>
/// 送信元の途絶検知（ADR-0018。opt-in・既定無効）の検証済み設定。
/// </summary>
/// <remarks>
/// <para>
/// <b>本型が存在する = 監視すべきエントリが 1 件以上ある</b>。ウォッチリスト未設定・空配列・
/// 全エントリが不正のいずれも <see cref="YaguraConfigurationLoader"/> は <see langword="null"/> を
/// 返す（<see cref="ResolvedEmailNotification"/> と同じ設計——監視側で「リストが空なら何もしない」を
/// 書かなくて済むようにする）。
/// </para>
/// <para>
/// <b>不正なエントリはリスト全体を殺さない</b>（ADR-0018 決定 1）。1 エントリのタイポで他の
/// 監視まで止めるのは、検知範囲の定義としては過剰な巻き添えである——当該エントリのみを
/// 落として警告し、残りは有効なまま動かす。configuration.md §1 の 3 分類に対する第 4 の挙動。
/// </para>
/// </remarks>
internal sealed record ResolvedSourceSilence(IReadOnlyList<SourceSilenceWatchEntry> Watchlist)
{
    /// <summary>既定値で閾値を補完したエントリ（読み込み時の情報記録・UI の識別表示用）。</summary>
    internal IReadOnlyList<SourceSilenceWatchEntry> DefaultedEntries =>
        [.. Watchlist.Where(entry => entry.ThresholdIsDefaulted)];
}
