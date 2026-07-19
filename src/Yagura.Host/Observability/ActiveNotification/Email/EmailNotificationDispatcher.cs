using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yagura.Host.Configuration;

namespace Yagura.Host.Observability.ActiveNotification.Email;

/// <summary>
/// メール通知キューの消化（ADR-0017 決定 5）。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ActiveNotificationMonitor"/>・<c>RetentionScheduler</c> と同じ形
/// （<c>IHostedService</c> ではなく <c>Start()</c> / <c>StopAsync()</c> を親が明示的に呼ぶ）に
/// 揃える——Generic Host の逆順停止では本製品が必要とする停止順序を表現できないため
/// （architecture.md §1.3）。
/// </para>
/// <para>
/// <b>受信・保存・UI への影響経路を作らない</b>（決定 5）: SMTP の応答待ちは本ループ内だけで
/// 起き、投入側（<see cref="EmailNotificationLoggerProvider"/>）はキューへ積むだけで戻る。
/// SMTP サーバの停止・遅延・TCP ブラックホールのいずれも、ここで待つのは本ループだけである。
/// </para>
/// </remarks>
/// <remarks>
/// 型のみ <c>public</c>（<see cref="ActiveNotificationMonitor"/> と同じ理由——公開型
/// <c>IngestionHostedService</c> のコンストラクタ引数に現れるため）。構築と操作の口は
/// <c>internal</c> のままとし、外部アセンブリからの利用面は開かない。
/// </remarks>
public sealed class EmailNotificationDispatcher : IAsyncDisposable
{
    private readonly EmailNotificationQueue _queue;
    private readonly IEmailSender _sender;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly object _configurationGate = new();

    private ResolvedEmailNotification? _configuration;
    private CancellationTokenSource? _stoppingCts;
    private Task? _dispatchTask;
    private DateTimeOffset? _lastSendFailureWarningAt;

    internal EmailNotificationDispatcher(
        EmailNotificationQueue queue,
        IEmailSender sender,
        ResolvedEmailNotification? configuration,
        TimeProvider? timeProvider = null,
        ILogger<EmailNotificationDispatcher>? logger = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _configuration = configuration;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<EmailNotificationDispatcher>.Instance;
    }

    /// <summary>最終送信成功時刻（常設カードの表示用。決定 5）。</summary>
    internal DateTimeOffset? LastSuccessAt { get; private set; }

    /// <summary>直近の失敗の分類と説明（常設カードの表示用）。</summary>
    internal EmailSendResult? LastFailure { get; private set; }

    /// <summary>
    /// 設定の即時反映（決定 9）。<see langword="null"/>（機能無効・構成不備）を渡した場合は、
    /// <b>キュー内の未送信通知を破棄する</b>——送り切りを待たず無効化の意図を優先する（決定 5）。
    /// </summary>
    internal void UpdateConfiguration(ResolvedEmailNotification? configuration)
    {
        lock (_configurationGate)
        {
            _configuration = configuration;
        }

        if (configuration is null)
        {
            _queue.Clear();
        }
    }

    internal void Start()
    {
        if (_dispatchTask is not null)
        {
            throw new InvalidOperationException("メール通知の送信ループは既に開始されています。");
        }

        _stoppingCts = new CancellationTokenSource();
        _dispatchTask = Task.Run(() => RunAsync(_stoppingCts.Token), CancellationToken.None);
    }

    internal async Task StopAsync()
    {
        if (_stoppingCts is null || _dispatchTask is null)
        {
            return;
        }

        await _stoppingCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await _dispatchTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停止要求による正常終了。
        }

        _stoppingCts.Dispose();
        _stoppingCts = null;
        _dispatchTask = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // 監視・通知のコンポーネントは黙って死んではならない（他の周期系と同じ規約）。
                _logger.LogWarning(
                    EmailNotificationEventIds.SendFailed,
                    ex,
                    "メール通知の送信ループで予期しない例外が発生しました。次の周期で再開します。");
            }

            try
            {
                await Task.Delay(EmailNotificationConstants.DispatchPollInterval, _timeProvider, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 送信可能な通知を可能な限り送る（テストが決定的に駆動するため internal）。
    /// </summary>
    internal async Task DrainOnceAsync(CancellationToken cancellationToken)
    {
        ResolvedEmailNotification? configuration;
        lock (_configurationGate)
        {
            configuration = _configuration;
        }

        if (configuration is null)
        {
            // 機能無効・構成不備。積まれた通知は UpdateConfiguration(null) が既に破棄している。
            return;
        }

        while (!cancellationToken.IsCancellationRequested && _queue.TryDequeueReady() is { } request)
        {
            var result = await _sender
                .SendAsync(configuration, request.Subject, request.Body, cancellationToken)
                .ConfigureAwait(false);

            if (result.Succeeded)
            {
                LastSuccessAt = _timeProvider.GetUtcNow();
                LastFailure = null;
                WarnAboutRejectedRecipients(request, result);
                continue;
            }

            LastFailure = result;

            // 再試行は 1 通あたり 1 回のみ（決定 5）。2 度目の失敗は破棄する——at-most-once。
            // 正本はイベントログと監査記録であり、届かないことを前提にした設計である。
            var retryScheduled = _queue.TryScheduleRetry(request);
            WarnAboutSendFailure(request, result, retryScheduled);
        }
    }

    private void WarnAboutRejectedRecipients(EmailNotificationRequest request, EmailSendResult result)
    {
        if (result.RejectedRecipients.Count == 0)
        {
            return;
        }

        // 一部拒否は「メッセージとしては送信成功」とし再送しない（委任 7）——
        // 再送すると受理済みの宛先へ二重に届く。
        _logger.LogWarning(
            EmailNotificationEventIds.SendFailed,
            "メール通知（イベント ID {NotifiedEventId}）は送信されましたが、一部の宛先がサーバに受理されませんでした: {RejectedRecipients}。" +
            "受理済みの宛先への二重送信を避けるため再送は行いません。宛先の綴りと SMTP サーバの中継ポリシーを確認してください。",
            request.EventId.Id,
            string.Join(", ", result.RejectedRecipients));
    }

    private void WarnAboutSendFailure(EmailNotificationRequest request, EmailSendResult result, bool retryScheduled)
    {
        // SMTP が長時間落ちている間、失敗のたびに警告を積まない（抑制窓付き。決定 5）。
        var now = _timeProvider.GetUtcNow();
        if (_lastSendFailureWarningAt is { } lastAt
            && now - lastAt < EmailNotificationConstants.SendFailureWarningSuppressionWindow)
        {
            return;
        }

        _lastSendFailureWarningAt = now;

        _logger.LogWarning(
            EmailNotificationEventIds.SendFailed,
            "メール通知（イベント ID {NotifiedEventId}）の送信に失敗しました（{FailureKind}）: {FailureDetail}。{RetryDisposition}" +
            "同種の警告は {SuppressionWindow} の間は再表示を抑制します。",
            request.EventId.Id,
            result.FailureKind,
            result.FailureDetail,
            retryScheduled
                ? $"{EmailNotificationConstants.RetryDelay} 後に 1 回だけ再試行します。"
                : "再試行済みのため、この通知は破棄しました（正本は本イベントログです）。",
            EmailNotificationConstants.SendFailureWarningSuppressionWindow);
    }
}
