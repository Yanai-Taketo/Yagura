namespace Yagura.Host.Observability.ActiveNotification.Email;

/// <summary>
/// メール通知チャネル（ADR-0017）の暫定定数。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ActiveNotificationConstants"/> と同じ運用——すべて「実測で確定するまでの
/// 暫定値」である。ただし本クラスの値は、他の暫定値と違い<b>設定キーとして公開しない</b>
/// （2026-07-18 オーナー決定。ADR-0017 改訂 1-E・configuration.md §8）。理由は 2 つ:
/// ①未実測の仮値に設定面を作ると、後から既定を直したときに明示指定した利用者だけが
/// 古い値に取り残される（キーは追加より削除・既定変更のほうが高くつく）②「簡単に導入できる」
/// という製品の前提は設定キーの総数に直接効く。
/// </para>
/// <para>
/// <b>受け入れる費用</b>: 通知が多すぎる／少なすぎると感じた利用者に、当面それを調整する
/// 手段がない（回避策は機能の無効化のみ）。再評価は ADR-0017 の再評価トリガによる。
/// </para>
/// </remarks>
internal static class EmailNotificationConstants
{
    /// <summary>
    /// 有界メモリキューの上限（暫定値: 64 件。ADR-0017 決定 5）。溢れた場合は古い側を破棄して
    /// 新しい側を保全する——監査の SEC-10（障害起点の保全 = 古い側保全）とは<b>意図的に逆</b>。
    /// 監査は証跡だから発生順が正だが、メールは「今の異常に気づくきっかけ」であり、SMTP の
    /// 長時間停止から復旧したときに届くべきは最新の状態である。
    /// </summary>
    internal const int MaxQueueDepth = 64;

    /// <summary>
    /// 同一 EventId のメール再送を抑制する最小間隔（暫定値: 60 分。ADR-0017 決定 6）。
    /// イベントログ側の抑制窓（<see cref="ActiveNotificationConstants.SuppressionWindow"/> = 15 分）
    /// より<b>長く</b>とる——「イベントログに 1 件ならメールも最大 1 通」の上限関係を保ったまま、
    /// 持続条件（スプール上限接近等）が一晩で数十通を積む事態を防ぐ。
    /// </summary>
    internal static readonly TimeSpan ResendInterval = TimeSpan.FromMinutes(60);

    /// <summary>
    /// 全体流量上限の評価窓（1 時間）と、その窓で送信できる通数（暫定値: 10 通。決定 6）。
    /// <b>エラーレベルの事象は本上限の対象外</b>だが、それは無制限を意味しない——
    /// <see cref="ResendInterval"/> が EventId 粒度で上位に効くため、実効上限は概ね
    /// 「allowlist 中のエラー ID 数 × 60 分あたり 1 通」に収束する。
    /// </summary>
    internal static readonly TimeSpan RateLimitWindow = TimeSpan.FromHours(1);

    /// <inheritdoc cref="RateLimitWindow"/>
    internal const int RateLimitMaxMessages = 10;

    /// <summary>SMTP の接続タイムアウト（暫定値: 10 秒。決定 5）。</summary>
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>SMTP の送信タイムアウト（暫定値: 30 秒。決定 5）。</summary>
    internal static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 送信失敗時の再試行までの待ち時間（暫定値: 5 分。決定 5）。再試行は 1 通あたり 1 回のみ。
    /// 即時再試行にしないのは、落ちているサーバと greylisting（初回配送の意図的な一時拒否）の
    /// いずれに対しても無力なため。<b>再試行待ちの通知はキュー枠を占有したまま後続を塞がない</b>
    /// （待ち時刻付きで保持し、待ちのない通知を先に送る——head-of-line blocking を作らない）。
    /// </summary>
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 送信失敗警告（<see cref="EmailNotificationEventIds.SendFailed"/>）の抑制窓
    /// （暫定値: 15 分）。SMTP が長時間落ちている間、失敗のたびにイベントログを積まない。
    /// <see cref="ActiveNotificationConstants.SuppressionWindow"/> と同じ値を採った。
    /// </summary>
    internal static readonly TimeSpan SendFailureWarningSuppressionWindow = TimeSpan.FromMinutes(15);

    /// <summary>キュー消化ループの周期（送信待ち・再試行待ちの点検間隔）。</summary>
    internal static readonly TimeSpan DispatchPollInterval = TimeSpan.FromSeconds(5);
}
