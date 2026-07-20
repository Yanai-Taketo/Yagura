using System.Diagnostics.Metrics;

namespace Yagura.Host.Observability.ActiveNotification.Email;

/// <summary>
/// メール通知チャネルのライブ計器（ADR-0017 決定 5。Issue #386）。
/// </summary>
/// <remarks>
/// <para>
/// <b>単一 Meter「Yagura」への統合</b>（architecture.md §4.1.1）——<c>WebGuardMetrics</c> と
/// 同じく、同名 Meter の別インスタンスを構築する（購読側は Meter 名で購読するため、複数の
/// Meter インスタンスが同名でも集約先は 1 つになる）。
/// </para>
/// <para>
/// 常設カード表示用のプロセス内カウンタ（<see cref="EmailNotificationQueue.DroppedCount"/> 等）
/// とは役割が異なる——こちらは外部監視（<c>MeterListener</c>・OpenTelemetry エクスポータ）から
/// 観測するための計器で、決定 5 の「破棄数はライブ計器に計上」「送信失敗は…+ ライブ計器で
/// 観測可能にする」に対応する。
/// </para>
/// </remarks>
internal sealed class EmailNotificationMetrics : IDisposable
{
    private readonly Meter _meter;
    private readonly Counter<long> _dropped;
    private readonly Counter<long> _sendFailures;

    internal EmailNotificationMetrics()
    {
        _meter = new Meter(Yagura.Ingestion.Diagnostics.IngestionMetrics.MeterName);

        _dropped = _meter.CreateCounter<long>(
            "yagura.notification.email.dropped",
            unit: "{message}",
            description: "送信されずに破棄されたメール通知の件数" +
                "（キュー溢れの最古破棄・初報による再送の押しのけ・再試行枠なしの 3 経路の合計）");

        _sendFailures = _meter.CreateCounter<long>(
            "yagura.notification.email.send_failures",
            unit: "{message}",
            description: "メール通知の送信失敗の回数（再試行を含む送信試行ごとに計上）");
    }

    internal void RecordDropped() => _dropped.Add(1);

    internal void RecordSendFailure() => _sendFailures.Add(1);

    public void Dispose() => _meter.Dispose();
}
