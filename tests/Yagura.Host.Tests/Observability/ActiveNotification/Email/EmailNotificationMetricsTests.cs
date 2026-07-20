using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Time.Testing;
using Yagura.Host.Observability.ActiveNotification;
using Yagura.Host.Observability.ActiveNotification.Email;

namespace Yagura.Host.Tests.Observability.ActiveNotification.Email;

/// <summary>
/// メール通知チャネルのライブ計器（ADR-0017 決定 5。Issue #386）のテスト。
/// </summary>
/// <remarks>
/// 計器は単一 Meter「Yagura」へ統合される（architecture.md §4.1.1）。ここでは Meter 名 +
/// instrument 名で購読し、破棄・送信失敗の各経路が計上されることを固定する。
/// </remarks>
public sealed class EmailNotificationMetricsTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void QueueOverflow_IncrementsTheDroppedCounter()
    {
        using var metrics = new EmailNotificationMetrics();

        using var collector = new MetricCollector<long>(
            null, "Yagura", "yagura.notification.email.dropped");

        // 上限（MaxQueueDepth）を超えて投入し、キュー溢れによる最古破棄を起こす。同一 EventId が
        // 再送間隔（60 分）で抑制されないよう、投入のたびに時刻を進める（抑制されると enqueue
        // 自体が起きず溢れない）。
        var time = new FakeTimeProvider(Origin);
        var queue = new EmailNotificationQueue(time, metrics);
        for (var i = 0; i < EmailNotificationConstants.MaxQueueDepth + 5; i++)
        {
            queue.TryEnqueue(ActiveNotificationEventIds.SpoolQuotaNearLimit, $"件名{i}", $"本文{i}");
            time.Advance(TimeSpan.FromMinutes(61));
        }

        // 溢れが 1 回以上起きていれば計器が動いている。
        Assert.True(collector.GetMeasurementSnapshot().Count > 0, "破棄カウンタが計上されていない。");
    }

    [Fact]
    public async Task SendFailure_IncrementsTheSendFailureCounter()
    {
        using var metrics = new EmailNotificationMetrics();
        var time = new FakeTimeProvider(Origin);
        var queue = new EmailNotificationQueue(time, metrics);
        var sender = new AlwaysFailingSender();
        var dispatcher = new EmailNotificationDispatcher(
            queue, sender, TestConfiguration, time, logger: null, metrics);

        using var collector = new MetricCollector<long>(
            null, "Yagura", "yagura.notification.email.send_failures");

        queue.TryEnqueue(ActiveNotificationEventIds.SpoolQuotaNearLimit, "件名", "本文");
        await dispatcher.DrainOnceAsync(CancellationToken.None);

        Assert.True(collector.GetMeasurementSnapshot().Count > 0, "送信失敗カウンタが計上されていない。");
    }

    private static readonly Yagura.Host.Configuration.ResolvedEmailNotification TestConfiguration = new(
        From: "yagura@example.com",
        To: ["ops@example.com"],
        SmtpHost: "smtp.example.com",
        SmtpPort: 25,
        Security: Yagura.Host.Configuration.EmailTransportSecurity.Auto,
        Username: null,
        Password: null);

    private sealed class AlwaysFailingSender : IEmailSender
    {
        public Task<EmailSendResult> SendAsync(
            Yagura.Host.Configuration.ResolvedEmailNotification configuration,
            string subject, string body, CancellationToken cancellationToken = default) =>
            Task.FromResult(EmailSendResult.Failure(EmailSendFailureKind.RelayRejected, "拒否"));
    }
}
