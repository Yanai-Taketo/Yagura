using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Yagura.Host.Configuration;
using Yagura.Host.Observability.ActiveNotification;
using Yagura.Host.Observability.ActiveNotification.Email;

namespace Yagura.Host.Tests.Observability.ActiveNotification.Email;

/// <summary>
/// <see cref="EmailNotificationQueue"/>（ADR-0017 決定 5・6。委任 11）の単体テスト。
/// </summary>
/// <remarks>
/// ADR-0017 の受け入れ基準「流量上限とエラー優先の単体テストが存在する」に対応する。
/// 本クラスが固定する性質は 4 つ:
/// (1) allowlist 外は投入されない、(2) 同一 EventId は再送間隔内なら 1 通に畳まれる、
/// (3) 全体流量上限は警告にのみ効きエラーには効かない、
/// (4) <b>枠・キューが埋まったとき、初報が重複に押し出されない</b>（優先度逆転の防止）。
/// </remarks>
public sealed class EmailNotificationQueueTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);

    // allowlist 済みの ID を代表として使う（重大度はテストの意図に合わせて選ぶ）。
    private static EventId Warning1 => ActiveNotificationEventIds.SpoolQuotaNearLimit;      // 1002 警告
    private static EventId Warning2 => ActiveNotificationEventIds.MonitoredVolumeFreeSpaceLow; // 1006 警告
    private static EventId ErrorEvent => ActiveNotificationEventIds.EvaluationFailed;       // 1008 エラー

    private static (EmailNotificationQueue Queue, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider(Origin);
        return (new EmailNotificationQueue(time), time);
    }

    private static EmailEnqueueOutcome Enqueue(EmailNotificationQueue queue, EventId eventId) =>
        queue.TryEnqueue(eventId, $"件名 {eventId.Id}", $"本文 {eventId.Id}");

    // ------------------------------------------------------------------
    // (1) allowlist
    // ------------------------------------------------------------------

    [Fact]
    public void TryEnqueue_EventIdOutsideAllowlist_IsRejected()
    {
        var (queue, _) = Create();

        // 1021（設定再読み込みの拒否）は allowlist に無い。
        var outcome = queue.TryEnqueue(ConfigurationEventIds.ConfigurationReloadRejected, "件名", "本文");

        Assert.Equal(EmailEnqueueOutcome.NotAllowlisted, outcome);
        Assert.Equal(0, queue.Depth);
    }

    [Fact]
    public void Allowlist_DoesNotContainEmailChannelsOwnEventIds()
    {
        // メール送信の失敗をメールで通知するループを、実装規律ではなく定義レベルで排除する
        // （ADR-0017 決定 5）。allowlist に載せた瞬間にこのテストが落ちる。
        Assert.DoesNotContain(EmailNotificationEventIds.SendFailed.Id, EmailNotificationAllowlist.RegisteredEventIds);
        Assert.DoesNotContain(EmailNotificationEventIds.Throttled.Id, EmailNotificationAllowlist.RegisteredEventIds);
    }

    // ------------------------------------------------------------------
    // (2) EventId 粒度の再送間隔（60 分）
    // ------------------------------------------------------------------

    [Fact]
    public void TryEnqueue_SameEventIdWithinResendInterval_IsFoldedIntoOne()
    {
        var (queue, time) = Create();

        Assert.Equal(EmailEnqueueOutcome.Accepted, Enqueue(queue, Warning1));

        time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(EmailEnqueueOutcome.SuppressedByResendInterval, Enqueue(queue, Warning1));

        // 60 分を越えれば再び通る。
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(EmailEnqueueOutcome.Accepted, Enqueue(queue, Warning1));

        Assert.Equal(1, queue.SuppressedCount);
        Assert.Equal(Origin.AddMinutes(59), queue.LastSuppressedAt);
        Assert.Equal(1, queue.SuppressedCountByEventId[Warning1.Id]);
    }

    [Fact]
    public void TryEnqueue_ResendIntervalAppliesToErrorsToo()
    {
        // エラーは「全体流量上限の対象外」だが「無制限」ではない——EventId 粒度の再送間隔は
        // 等しく効く（ADR-0017 決定 6 の導出。深夜の大量送信経路にしないための上位の効き目）。
        var (queue, time) = Create();

        Assert.Equal(EmailEnqueueOutcome.Accepted, Enqueue(queue, ErrorEvent));
        time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(EmailEnqueueOutcome.SuppressedByResendInterval, Enqueue(queue, ErrorEvent));
    }

    // ------------------------------------------------------------------
    // (3) 全体流量上限（10 通/時。エラーは対象外）
    // ------------------------------------------------------------------

    [Fact]
    public void TryEnqueue_WarningsBeyondRateLimit_AreSuppressed()
    {
        var (queue, time) = Create();

        // 異なる警告 ID を 10 件通して枠を使い切る（再送間隔に掛からないよう ID を変える）。
        var allowlistedWarnings = EmailNotificationAllowlist.RegisteredEventIds
            .Where(id => EmailNotificationAllowlist.TryGetSeverity(new EventId(id), out var s)
                && s == EmailNotificationAllowlist.Severity.Warning)
            .Take(EmailNotificationConstants.RateLimitMaxMessages + 1)
            .ToList();

        Assert.True(allowlistedWarnings.Count > EmailNotificationConstants.RateLimitMaxMessages,
            "allowlist の警告 ID が流量上限より少ないと本テストは意味を持たない。");

        for (var i = 0; i < EmailNotificationConstants.RateLimitMaxMessages; i++)
        {
            Assert.Equal(EmailEnqueueOutcome.Accepted, Enqueue(queue, new EventId(allowlistedWarnings[i])));
        }

        // 11 件目は初報だが、押しのけられる「再送」がキューに無いため抑制される。
        var overflow = new EventId(allowlistedWarnings[EmailNotificationConstants.RateLimitMaxMessages]);
        Assert.Equal(EmailEnqueueOutcome.SuppressedByRateLimit, Enqueue(queue, overflow));

        // 窓（1 時間）を越えれば枠は回復する。
        time.Advance(EmailNotificationConstants.RateLimitWindow + TimeSpan.FromMinutes(1));
        Assert.Equal(EmailEnqueueOutcome.Accepted, Enqueue(queue, overflow));
    }

    [Fact]
    public void TryEnqueue_ErrorsAreExemptFromTheRateLimit()
    {
        var (queue, time) = Create();

        // 警告で枠を使い切る。
        var warnings = EmailNotificationAllowlist.RegisteredEventIds
            .Where(id => EmailNotificationAllowlist.TryGetSeverity(new EventId(id), out var s)
                && s == EmailNotificationAllowlist.Severity.Warning)
            .Take(EmailNotificationConstants.RateLimitMaxMessages);

        foreach (var id in warnings)
        {
            Assert.Equal(EmailEnqueueOutcome.Accepted, Enqueue(queue, new EventId(id)));
        }

        // エラーは枠を待たずに通る（通知が最も必要な場面で止めない）。
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(EmailEnqueueOutcome.Accepted, Enqueue(queue, ErrorEvent));
    }

    // ------------------------------------------------------------------
    // (4) 優先度逆転の防止（委任 11 の核心）
    // ------------------------------------------------------------------

    [Fact]
    public void TryEnqueue_QueueOverflow_DiscardsAResendBeforeAFirstReport()
    {
        var (queue, time) = Create();

        // Warning1 を 2 回通す（2 回目は「再送」= IsFirstReport=false でキューに 2 件並ぶ）。
        Assert.Equal(EmailEnqueueOutcome.Accepted, Enqueue(queue, Warning1));
        time.Advance(EmailNotificationConstants.ResendInterval + TimeSpan.FromMinutes(1));
        Assert.Equal(EmailEnqueueOutcome.Accepted, Enqueue(queue, Warning1));
        Assert.Equal(2, queue.Depth);

        // キューを上限まで埋める（初報で埋める）。
        var filler = EmailNotificationAllowlist.RegisteredEventIds
            .Where(id => id != Warning1.Id)
            .ToList();

        // 上限 64 に対し allowlist は 64 件も無いため、上限到達は「再送」で作る——
        // ここでは溢れの瞬間に何が捨てられるかだけを見るため、Warning1 の再送を積み増す。
        while (queue.Depth < EmailNotificationConstants.MaxQueueDepth)
        {
            time.Advance(EmailNotificationConstants.ResendInterval + TimeSpan.FromMinutes(1));
            Assert.Equal(EmailEnqueueOutcome.Accepted, Enqueue(queue, Warning1));
        }

        var droppedBefore = queue.DroppedCount;

        // 溢れた状態でさらに 1 件入れると、先頭（最古 = Warning1 の初報）ではなく
        // 「再送」が捨てられる——初報は残る。
        time.Advance(EmailNotificationConstants.RateLimitWindow + TimeSpan.FromMinutes(1));
        Assert.Equal(EmailEnqueueOutcome.Accepted, Enqueue(queue, Warning2));

        Assert.Equal(droppedBefore + 1, queue.DroppedCount);

        // 残っているものを全部取り出し、初報が生き残っていることを確認する。
        var drained = new List<EmailNotificationRequest>();
        while (queue.TryDequeueReady() is { } request)
        {
            drained.Add(request);
        }

        Assert.Contains(drained, r => r.EventId.Id == Warning1.Id && r.IsFirstReport);
        Assert.Contains(drained, r => r.EventId.Id == Warning2.Id && r.IsFirstReport);
        _ = filler;
    }

    // ------------------------------------------------------------------
    // 取り出し・再試行・破棄
    // ------------------------------------------------------------------

    [Fact]
    public void TryDequeueReady_SkipsItemsWaitingForRetry_WithoutBlockingTheRest()
    {
        // head-of-line blocking を作らない（決定 5）。
        var (queue, time) = Create();

        Assert.Equal(EmailEnqueueOutcome.Accepted, Enqueue(queue, Warning1));
        var first = queue.TryDequeueReady();
        Assert.NotNull(first);

        // 失敗させて再試行待ち（5 分後）へ戻す。
        Assert.True(queue.TryScheduleRetry(first!));

        // 待ちのない通知を後から積むと、そちらが先に出る。
        Assert.Equal(EmailEnqueueOutcome.Accepted, Enqueue(queue, Warning2));
        var next = queue.TryDequeueReady();
        Assert.Equal(Warning2.Id, next!.EventId.Id);

        // 再試行待ちは時刻が来るまで出てこない。
        Assert.Null(queue.TryDequeueReady());
        time.Advance(EmailNotificationConstants.RetryDelay + TimeSpan.FromSeconds(1));
        Assert.Equal(Warning1.Id, queue.TryDequeueReady()!.EventId.Id);
    }

    [Fact]
    public void TryScheduleRetry_OnlyOncePerMessage()
    {
        // at-most-once（有界再試行 + 破棄。決定 5）。
        var (queue, _) = Create();

        Enqueue(queue, Warning1);
        var request = queue.TryDequeueReady()!;

        Assert.True(queue.TryScheduleRetry(request));

        var retried = queue.TryDequeueReady() ?? request with { RetryScheduled = true };
        Assert.False(queue.TryScheduleRetry(retried));
    }

    [Fact]
    public void Clear_DropsPendingNotifications()
    {
        // Enabled=false への即時反映時は送り切りを待たない（決定 5）。
        var (queue, _) = Create();

        Enqueue(queue, Warning1);
        Enqueue(queue, Warning2);
        Assert.Equal(2, queue.Depth);

        queue.Clear();

        Assert.Equal(0, queue.Depth);
        Assert.Null(queue.TryDequeueReady());
    }
}
