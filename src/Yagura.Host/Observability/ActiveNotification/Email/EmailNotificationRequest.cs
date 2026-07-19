using Microsoft.Extensions.Logging;

namespace Yagura.Host.Observability.ActiveNotification.Email;

/// <summary>
/// キューに積まれた 1 通のメール通知（ADR-0017 決定 5）。
/// </summary>
/// <param name="EventId">発火元のイベント ID（allowlist の判定・抑制の同一性判定の単位）。</param>
/// <param name="Subject">件名。</param>
/// <param name="Body">本文（委任 2 で上限を固定したフィールド群を整形済み）。</param>
/// <param name="EnqueuedAt">キューへ投入された時刻。</param>
/// <param name="IsFirstReport">
/// この EventId がキューに入るのが史上初か（＝「初報」）。<b>投入時点で確定させて通知自身が
/// 持つ</b>——キューの状態（投入済み EventId の集合）から後で逆算しようとすると、投入した
/// 瞬間に自分自身が「投入済み」になってしまい、全件が「再送」と判定される。
/// キュー溢れ・流量上限超過のときに何を捨てるかの判断材料（決定 6 の選好）。
/// </param>
/// <param name="NotBefore">
/// この時刻までは送信しない（再試行待ち）。<see langword="null"/> は即時送信可。
/// </param>
/// <param name="RetryScheduled">
/// 既に再試行が 1 回スケジュールされたか。<see langword="true"/> の通知が再び失敗した場合は
/// 破棄する（at-most-once。正本はイベントログと監査記録——決定 5）。
/// </param>
internal sealed record EmailNotificationRequest(
    EventId EventId,
    string Subject,
    string Body,
    DateTimeOffset EnqueuedAt,
    bool IsFirstReport,
    DateTimeOffset? NotBefore = null,
    bool RetryScheduled = false);
