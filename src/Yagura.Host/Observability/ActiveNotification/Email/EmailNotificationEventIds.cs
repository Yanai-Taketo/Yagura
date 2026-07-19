using Microsoft.Extensions.Logging;

namespace Yagura.Host.Observability.ActiveNotification.Email;

/// <summary>
/// メール通知チャネル自身が発するイベント ID（ADR-0017 決定 5・6。Issue #350）。
/// 番号の正本は security.md §4.3 の表（additive-only）。
/// </summary>
/// <remarks>
/// <b>本クラスの ID は決定 6 の allowlist に含めない</b>——メール送信の失敗をメールで
/// 通知しようとする経路を、実装規律ではなく定義レベルで排除する
/// （<see cref="EmailNotificationAllowlist"/> にこれらの ID が現れないことをテストで固定する）。
/// </remarks>
public static class EmailNotificationEventIds
{
    /// <summary>
    /// メール通知の送信が再試行後も失敗した（ADR-0017 決定 5）。レベル: 警告。
    /// 抑制窓付き（<see cref="EmailNotificationConstants.SendFailureWarningSuppressionWindow"/>）
    /// ——SMTP が長時間落ちている間、毎通ごとに警告を積まない。
    /// </summary>
    public static readonly EventId SendFailed = new(1025, "EmailNotificationSendFailed");

    /// <summary>
    /// 流量制御によりメール通知が抑制された（ADR-0017 決定 6）。レベル: 警告。
    /// 抑制が発生している状態が続く間、イベントログへは 1 回だけ出す（常設カードが継続的な
    /// 可視化を担う——決定 5）。
    /// </summary>
    public static readonly EventId Throttled = new(1026, "EmailNotificationThrottled");
}
