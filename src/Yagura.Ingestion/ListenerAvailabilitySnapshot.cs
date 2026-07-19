namespace Yagura.Ingestion;

/// <summary>
/// 受信リスナの「今この瞬間の受信可否」のスナップショット（ADR-0018 委任 6。Issue #351）。
/// <see cref="IngestionPipeline.ListenerAvailability"/> が返す。
/// </summary>
/// <remarks>
/// 起動 Outcome（<see cref="ListenerStartupResult"/>）・再構成 Outcome
/// （<see cref="ListenerReconfigurationResult"/>）・CF-6 復旧イベント
/// （<see cref="IngestionPipeline.ListenerBindRecovered"/>）はいずれも「その時点の帰結」を 1 回
/// 報告する形であり、現在状態を問い合わせる口がなかった。本型はこの 3 系統を畳んだ現在状態を表す
/// ——新しい観測機構ではなく、既存の帰結の集約である。
/// </remarks>
/// <param name="Udp">UDP リスナが受信可能か。</param>
/// <param name="Tcp">TCP リスナが受信可能か。</param>
/// <param name="Tls">
/// TLS リスナが受信可能か。TLS 受信を構成していない場合は <see langword="null"/>
/// （未構成のリスナは <see cref="AllListenersDown"/> の判定に数えない）。
/// </param>
public sealed record ListenerAvailabilitySnapshot(bool Udp, bool Tcp, bool? Tls)
{
    /// <summary>
    /// 構成済みの全リスナが受信不能か。送信元の途絶検知（ADR-0018 決定 3）が「サーバ都合の
    /// 受信断」として途絶判定を保留する条件——部分受信断（UDP のみ down 等）は保留対象にしない
    /// （途絶警告の Detail への受信経路の状態の併記で対応する）。
    /// </summary>
    public bool AllListenersDown => !Udp && !Tcp && Tls is not true;
}
