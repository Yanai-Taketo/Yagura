using Microsoft.Extensions.Logging;

namespace Yagura.Host.Observability.ActiveNotification.Email;

/// <summary>キューへの投入結果（呼び出し側の計器・テストのための区別）。</summary>
internal enum EmailEnqueueOutcome
{
    /// <summary>キューに入った。</summary>
    Accepted,

    /// <summary>allowlist（<see cref="EmailNotificationAllowlist"/>）に無い ID のため対象外。</summary>
    NotAllowlisted,

    /// <summary>同一 EventId の再送間隔（60 分）内のため抑制した。</summary>
    SuppressedByResendInterval,

    /// <summary>全体流量上限（10 通/時）に達しているため抑制した。</summary>
    SuppressedByRateLimit,
}

/// <summary>
/// メール通知の有界キュー + 流量制御（ADR-0017 決定 5・6。委任 11）。
/// </summary>
/// <remarks>
/// <para>
/// <b>委任 11 の裁定: 抑制・流量制御は「投入時（enqueue）」に評価する。</b>
/// ADR-0017 委任 11 は enqueue / dequeue の選択を実装に委ね、衝突した場合は決定 6 の選好
/// （未送信トリガ種の初報を優先）を上位とすることまでを決めていた。**投入時とすることで
/// 衝突そのものが起きない**——抑制対象がキューに積まれないため、決定 5 の「溢れたら古い側を
/// 破棄」が想定どおり「異なる事象が並ぶキュー」の上で働く。送信時評価にすると、大規模障害時に
/// 抑制対象の重複で 64 件がすぐ埋まり、先に入っていた異種の初報が後から来た同種の重複に
/// 押し出される（決定 5 の理屈は同一事象の連続を前提にしており、異種が並ぶキューでは成立しない）。
/// </para>
/// <para>
/// <b>投入時評価の代償</b>: 枠を消費した通知が、その後の送信で失敗して結局届かないことがある
/// （枠は戻さない）。これは「実際に送れた数」ではなく「送ろうとした数」で上限を数えることを
/// 意味するが、メールは既に at-most-once（決定 5）であり、誤差は常に<b>送りすぎない側</b>へ
/// 倒れるため受容する。
/// </para>
/// <para>
/// <b>キュー溢れ時の破棄も決定 6 の選好に従う</b>: 単純な「最古を破棄」ではなく、
/// <b>まず「過去に送信済みの種の再送」の中で最古のもの</b>を破棄し、それが無い場合にのみ
/// 全体の最古を破棄する。64 件が埋まる場面は多数の異なる事象が同時発火した場面であり、
/// そこで希少な初報を捨てて重複を残すのは優先度逆転そのものになる。
/// </para>
/// <para>
/// 本クラスはロギング呼び出し（受信ホットパス上を含む）から呼ばれるため、
/// <b>ブロックせず・例外を漏らさない</b>。同期の <c>lock</c> のみで完結させ、I/O を行わない。
/// </para>
/// </remarks>
internal sealed class EmailNotificationQueue
{
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();

    /// <summary>送信待ちの通知（先頭が最古）。</summary>
    private readonly LinkedList<EmailNotificationRequest> _pending = new();

    /// <summary>EventId ごとの最終「投入」時刻（再送間隔の判定に使う）。</summary>
    private readonly Dictionary<int, DateTimeOffset> _lastEnqueuedAtByEventId = new();

    /// <summary>過去に一度でも投入された EventId（初報かどうかの判定に使う）。</summary>
    private readonly HashSet<int> _everEnqueuedEventIds = new();

    /// <summary>流量上限の評価窓に入る投入時刻（古い順。窓外は都度切り落とす）。</summary>
    private readonly Queue<DateTimeOffset> _rateLimitWindow = new();

    internal EmailNotificationQueue(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>現在のキュー深度（常設カードの表示用。決定 5）。</summary>
    internal int Depth
    {
        get { lock (_gate) { return _pending.Count; } }
    }

    /// <summary>キュー溢れによる累積破棄数（常設カードの表示用）。</summary>
    internal int DroppedCount { get; private set; }

    /// <summary>流量制御による累積抑制数（常設カードの表示用）。</summary>
    internal int SuppressedCount { get; private set; }

    /// <summary>直近に抑制が発生した時刻（<see langword="null"/> = 一度も発生していない）。</summary>
    internal DateTimeOffset? LastSuppressedAt { get; private set; }

    /// <summary>
    /// 抑制された EventId ごとの回数（常設カードの内訳表示用）。
    /// <b>累積回数だけでは「先週の 1 回の障害でついた数字」と「毎晩じわじわ増えている」が
    /// 区別できず、何が届かなかったかも特定できない</b>（ADR-0017 決定 5。改訂 1 の指摘）。
    /// </summary>
    internal IReadOnlyDictionary<int, int> SuppressedCountByEventId
    {
        get { lock (_gate) { return new Dictionary<int, int>(_suppressedCountByEventId); } }
    }

    private readonly Dictionary<int, int> _suppressedCountByEventId = new();

    /// <summary>
    /// 通知を投入する。allowlist 判定・再送間隔・全体流量上限をここで評価する（委任 11 の裁定）。
    /// </summary>
    internal EmailEnqueueOutcome TryEnqueue(EventId eventId, string subject, string body)
    {
        if (!EmailNotificationAllowlist.TryGetSeverity(eventId, out var severity))
        {
            return EmailEnqueueOutcome.NotAllowlisted;
        }

        var now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            // --- ① EventId 粒度の再送間隔（決定 6。エラーレベルにも等しく適用する） ---
            if (_lastEnqueuedAtByEventId.TryGetValue(eventId.Id, out var lastAt)
                && now - lastAt < EmailNotificationConstants.ResendInterval)
            {
                RecordSuppressionUnderGate(eventId, now);
                return EmailEnqueueOutcome.SuppressedByResendInterval;
            }

            // --- ② 全体流量上限（決定 6。エラーレベルは対象外） ---
            var isFirstReport = !_everEnqueuedEventIds.Contains(eventId.Id);
            if (severity == EmailNotificationAllowlist.Severity.Warning)
            {
                TrimRateLimitWindowUnderGate(now);

                if (_rateLimitWindow.Count >= EmailNotificationConstants.RateLimitMaxMessages)
                {
                    // 未送信トリガ種の初報は、キュー内の「再送」を 1 件押しのけてでも通す
                    // （優先度逆転の防止——決定 6 の選好）。押しのける相手が無ければ抑制する。
                    if (!isFirstReport || !TryEvictOneResendUnderGate())
                    {
                        RecordSuppressionUnderGate(eventId, now);
                        return EmailEnqueueOutcome.SuppressedByRateLimit;
                    }
                }

                _rateLimitWindow.Enqueue(now);
            }

            // --- ③ 有界キューへの投入（溢れたら決定 6 の選好に従って 1 件破棄する） ---
            while (_pending.Count >= EmailNotificationConstants.MaxQueueDepth)
            {
                if (!TryEvictOneResendUnderGate())
                {
                    // 全件が初報。決定 5 の既定どおり最古を破棄して新しい側を保全する。
                    _pending.RemoveFirst();
                    DroppedCount++;
                }
            }

            _pending.AddLast(new EmailNotificationRequest(eventId, subject, body, now, isFirstReport));
            _lastEnqueuedAtByEventId[eventId.Id] = now;
            _everEnqueuedEventIds.Add(eventId.Id);

            return EmailEnqueueOutcome.Accepted;
        }
    }

    /// <summary>
    /// 送信可能な通知を 1 件取り出す（再試行待ちのものは待ち時刻まで飛ばす——
    /// head-of-line blocking を作らない。決定 5）。無ければ <see langword="null"/>。
    /// </summary>
    internal EmailNotificationRequest? TryDequeueReady()
    {
        var now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            for (var node = _pending.First; node is not null; node = node.Next)
            {
                if (node.Value.NotBefore is { } notBefore && now < notBefore)
                {
                    continue;
                }

                _pending.Remove(node);
                return node.Value;
            }

            return null;
        }
    }

    /// <summary>
    /// 送信に失敗した通知を再試行のためキューへ戻す（1 通あたり 1 回だけ。決定 5）。
    /// 既に再試行済みなら戻さず <see langword="false"/> を返す（at-most-once の確定）。
    /// </summary>
    internal bool TryScheduleRetry(EmailNotificationRequest request)
    {
        if (request.RetryScheduled)
        {
            return false;
        }

        var retryAt = _timeProvider.GetUtcNow() + EmailNotificationConstants.RetryDelay;

        lock (_gate)
        {
            // 再試行待ちも枠を占有するが、TryDequeueReady が待ち時刻を飛ばすため後続は塞がらない。
            if (_pending.Count >= EmailNotificationConstants.MaxQueueDepth)
            {
                DroppedCount++;
                return false;
            }

            _pending.AddLast(request with { NotBefore = retryAt, RetryScheduled = true });
            return true;
        }
    }

    /// <summary>
    /// キュー内の未送信通知をすべて破棄する（<c>Enabled=false</c> への即時反映時。決定 5
    /// ——送り切りを待たず無効化の意図を優先する）。
    /// </summary>
    internal void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
        }
    }

    /// <summary>
    /// 「過去に送信済みの種の再送」をキューから 1 件（最古）取り除く。
    /// 見つからない（＝全件が初報）場合は <see langword="false"/>。
    /// </summary>
    private bool TryEvictOneResendUnderGate()
    {
        for (var node = _pending.First; node is not null; node = node.Next)
        {
            if (!node.Value.IsFirstReport)
            {
                _pending.Remove(node);
                DroppedCount++;
                return true;
            }
        }

        return false;
    }

    private void TrimRateLimitWindowUnderGate(DateTimeOffset now)
    {
        while (_rateLimitWindow.Count > 0
            && now - _rateLimitWindow.Peek() >= EmailNotificationConstants.RateLimitWindow)
        {
            _rateLimitWindow.Dequeue();
        }
    }

    private void RecordSuppressionUnderGate(EventId eventId, DateTimeOffset now)
    {
        SuppressedCount++;
        LastSuppressedAt = now;
        _suppressedCountByEventId[eventId.Id] =
            _suppressedCountByEventId.GetValueOrDefault(eventId.Id) + 1;
    }
}
