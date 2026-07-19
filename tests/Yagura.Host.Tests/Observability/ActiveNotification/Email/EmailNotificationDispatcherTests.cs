using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Yagura.Host.Configuration;
using Yagura.Host.Observability.ActiveNotification;
using Yagura.Host.Observability.ActiveNotification.Email;

namespace Yagura.Host.Tests.Observability.ActiveNotification.Email;

/// <summary>
/// <see cref="EmailNotificationLoggerProvider"/> と <see cref="EmailNotificationDispatcher"/> の
/// 単体テスト（ADR-0017 決定 5・7、委任 6・7）。
/// </summary>
public sealed class EmailNotificationDispatcherTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);

    private static readonly ResolvedEmailNotification Configuration = new(
        From: "yagura@example.com",
        To: ["ops@example.com"],
        SmtpHost: "smtp.example.com",
        SmtpPort: 25,
        Security: EmailTransportSecurity.Auto,
        Username: null,
        Password: null);

    /// <summary>送信結果を台本どおりに返す差し替え送信器。</summary>
    private sealed class ScriptedSender : IEmailSender
    {
        private readonly Queue<EmailSendResult> _script;

        internal ScriptedSender(params EmailSendResult[] results) => _script = new Queue<EmailSendResult>(results);

        internal List<string> SentSubjects { get; } = [];

        public Task<EmailSendResult> SendAsync(
            ResolvedEmailNotification configuration, string subject, string body, CancellationToken cancellationToken = default)
        {
            SentSubjects.Add(subject);
            return Task.FromResult(_script.Count > 0 ? _script.Dequeue() : EmailSendResult.Success());
        }
    }

    // ------------------------------------------------------------------
    // ILoggerProvider（決定 7）
    // ------------------------------------------------------------------

    [Fact]
    public void Provider_EnqueuesOnlyAllowlistedEventIds()
    {
        var queue = new EmailNotificationQueue(new FakeTimeProvider(Origin));
        using var provider = new EmailNotificationLoggerProvider(queue, new FakeTimeProvider(Origin));
        var logger = provider.CreateLogger("Test");

        // allowlist 内。
        logger.Log(LogLevel.Warning, ActiveNotificationEventIds.SpoolQuotaNearLimit, "状態", null, (s, _) => s);
        Assert.Equal(1, queue.Depth);

        // allowlist 外。
        logger.Log(LogLevel.Warning, ConfigurationEventIds.ConfigurationReloadRejected, "状態", null, (s, _) => s);
        Assert.Equal(1, queue.Depth);

        // EventId 未指定（= 0）は対象になり得ない（委任 6 が検出対象とする欠陥クラス）。
        logger.Log(LogLevel.Error, default, "状態", null, (s, _) => s);
        Assert.Equal(1, queue.Depth);
    }

    [Fact]
    public void Provider_AcceptsEveryLogLevel_IndependentOfUserLoggingFilters()
    {
        // 利用者の Logging:LogLevel 設定が黙ってメールを止める経路を作らない（決定 7）。
        using var provider = new EmailNotificationLoggerProvider(
            new EmailNotificationQueue(new FakeTimeProvider(Origin)), new FakeTimeProvider(Origin));
        var logger = provider.CreateLogger("Test");

        foreach (var level in Enum.GetValues<LogLevel>().Where(l => l != LogLevel.None))
        {
            Assert.True(logger.IsEnabled(level), $"{level} が無効になっている。");
        }

        Assert.False(logger.IsEnabled(LogLevel.None));
    }

    [Fact]
    public void Provider_DoesNotThrowWhenFormatterFails()
    {
        // ロギング呼び出しは受信ホットパス上にあり得る。通知側の失敗で呼び出し元を壊さない。
        using var provider = new EmailNotificationLoggerProvider(
            new EmailNotificationQueue(new FakeTimeProvider(Origin)), new FakeTimeProvider(Origin));
        var logger = provider.CreateLogger("Test");

        var exception = Record.Exception(() => logger.Log<string>(
            LogLevel.Warning,
            ActiveNotificationEventIds.SpoolQuotaNearLimit,
            "状態",
            null,
            (_, _) => throw new InvalidOperationException("整形に失敗")));

        Assert.Null(exception);
    }

    [Fact]
    public void Provider_BodyCarriesTheEventIdAndPointsAtTheEventLogAsSourceOfTruth()
    {
        var queue = new EmailNotificationQueue(new FakeTimeProvider(Origin));
        using var provider = new EmailNotificationLoggerProvider(queue, new FakeTimeProvider(Origin));

        provider.CreateLogger("Test").Log(
            LogLevel.Warning, ActiveNotificationEventIds.SpoolQuotaNearLimit, "スプールが上限に接近", null, (s, _) => s);

        var request = queue.TryDequeueReady();
        Assert.NotNull(request);
        Assert.Contains("1002", request!.Subject);
        Assert.Contains("スプールが上限に接近", request.Body);
        // メールを一次資料として扱わせない（at-most-once であり届かないことがある——決定 5）。
        Assert.Contains("正本", request.Body);
    }

    // ------------------------------------------------------------------
    // 送信・再試行・破棄（決定 5）
    // ------------------------------------------------------------------

    /// <remarks>
    /// <paramref name="configuration"/> に既定値を持たせない——<c>null</c>（機能無効）は
    /// このテスト群が検証したい状態そのものであり、「未指定」との区別を潰すと
    /// 無効時の検証が黙って有効時の検証にすり替わる。
    /// </remarks>
    private static (EmailNotificationDispatcher Dispatcher, EmailNotificationQueue Queue, FakeTimeProvider Time)
        CreateDispatcher(IEmailSender sender, ResolvedEmailNotification? configuration)
    {
        var time = new FakeTimeProvider(Origin);
        var queue = new EmailNotificationQueue(time);
        return (new EmailNotificationDispatcher(queue, sender, configuration, time), queue, time);
    }

    [Fact]
    public async Task DrainOnceAsync_SuccessfulSend_RecordsLastSuccess()
    {
        var sender = new ScriptedSender(EmailSendResult.Success());
        var (dispatcher, queue, _) = CreateDispatcher(sender, Configuration);

        queue.TryEnqueue(ActiveNotificationEventIds.SpoolQuotaNearLimit, "件名", "本文");
        await dispatcher.DrainOnceAsync(CancellationToken.None);

        Assert.Single(sender.SentSubjects);
        Assert.Equal(Origin, dispatcher.LastSuccessAt);
        Assert.Null(dispatcher.LastFailure);
        Assert.Equal(0, queue.Depth);
    }

    [Fact]
    public async Task DrainOnceAsync_FailureIsRetriedExactlyOnceThenDropped()
    {
        // at-most-once（有界再試行 + 破棄。決定 5）。
        var sender = new ScriptedSender(
            EmailSendResult.Failure(EmailSendFailureKind.ConnectionFailed, "接続不能"),
            EmailSendResult.Failure(EmailSendFailureKind.ConnectionFailed, "接続不能"));
        var (dispatcher, queue, time) = CreateDispatcher(sender, Configuration);

        queue.TryEnqueue(ActiveNotificationEventIds.SpoolQuotaNearLimit, "件名", "本文");

        await dispatcher.DrainOnceAsync(CancellationToken.None);
        Assert.Single(sender.SentSubjects);
        Assert.Equal(1, queue.Depth); // 再試行待ちとして保持されている

        // 再試行の時刻が来るまで送らない。
        await dispatcher.DrainOnceAsync(CancellationToken.None);
        Assert.Single(sender.SentSubjects);

        time.Advance(EmailNotificationConstants.RetryDelay + TimeSpan.FromSeconds(1));
        await dispatcher.DrainOnceAsync(CancellationToken.None);

        Assert.Equal(2, sender.SentSubjects.Count);
        Assert.Equal(0, queue.Depth); // 2 度目の失敗で破棄された
        Assert.NotNull(dispatcher.LastFailure);
    }

    [Fact]
    public async Task DrainOnceAsync_PartialRecipientRejection_CountsAsSuccessAndIsNotResent()
    {
        // 委任 7: 一部拒否は「メッセージとしては送信成功・拒否宛先を警告ログ・再送しない」
        // ——再送すると受理済みの宛先へ二重に届く。
        var sender = new ScriptedSender(EmailSendResult.Success(["rejected@example.com"]));
        var (dispatcher, queue, _) = CreateDispatcher(sender, Configuration);

        queue.TryEnqueue(ActiveNotificationEventIds.SpoolQuotaNearLimit, "件名", "本文");
        await dispatcher.DrainOnceAsync(CancellationToken.None);

        Assert.Single(sender.SentSubjects);
        Assert.Equal(0, queue.Depth);
        Assert.NotNull(dispatcher.LastSuccessAt);
        Assert.Null(dispatcher.LastFailure);
    }

    [Fact]
    public async Task DrainOnceAsync_WithoutConfiguration_SendsNothing()
    {
        var sender = new ScriptedSender();
        var (dispatcher, queue, _) = CreateDispatcher(sender, configuration: null);

        queue.TryEnqueue(ActiveNotificationEventIds.SpoolQuotaNearLimit, "件名", "本文");
        await dispatcher.DrainOnceAsync(CancellationToken.None);

        Assert.Empty(sender.SentSubjects);
    }

    [Fact]
    public async Task UpdateConfiguration_ToDisabled_DropsPendingNotifications()
    {
        // Enabled=false への即時反映では送り切りを待たない（決定 5・9）。
        var sender = new ScriptedSender();
        var (dispatcher, queue, _) = CreateDispatcher(sender, Configuration);

        queue.TryEnqueue(ActiveNotificationEventIds.SpoolQuotaNearLimit, "件名", "本文");
        Assert.Equal(1, queue.Depth);

        dispatcher.UpdateConfiguration(null);

        Assert.Equal(0, queue.Depth);
        await dispatcher.DrainOnceAsync(CancellationToken.None);
        Assert.Empty(sender.SentSubjects);
    }

    [Fact]
    public async Task UpdateConfiguration_ToEnabled_ResumesSendingWithoutRestart()
    {
        var sender = new ScriptedSender(EmailSendResult.Success());
        var (dispatcher, queue, _) = CreateDispatcher(sender, configuration: null);

        queue.TryEnqueue(ActiveNotificationEventIds.SpoolQuotaNearLimit, "件名", "本文");
        await dispatcher.DrainOnceAsync(CancellationToken.None);
        Assert.Empty(sender.SentSubjects);

        dispatcher.UpdateConfiguration(Configuration);
        await dispatcher.DrainOnceAsync(CancellationToken.None);

        Assert.Single(sender.SentSubjects);
    }

    [Fact]
    public async Task StartThenStop_IsIdempotentAndDoesNotThrow()
    {
        var (dispatcher, _, _) = CreateDispatcher(new ScriptedSender(), Configuration);

        dispatcher.Start();
        Assert.Throws<InvalidOperationException>(dispatcher.Start);

        await dispatcher.StopAsync();
        await dispatcher.StopAsync();
        await dispatcher.DisposeAsync();
    }
}
