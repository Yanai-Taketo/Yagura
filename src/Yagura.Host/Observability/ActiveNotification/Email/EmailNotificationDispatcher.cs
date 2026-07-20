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

    private readonly EmailNotificationMetrics? _metrics;

    internal EmailNotificationDispatcher(
        EmailNotificationQueue queue,
        IEmailSender sender,
        ResolvedEmailNotification? configuration,
        TimeProvider? timeProvider = null,
        ILogger<EmailNotificationDispatcher>? logger = null,
        EmailNotificationMetrics? metrics = null)
    {
        _metrics = metrics;
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _configuration = configuration;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<EmailNotificationDispatcher>.Instance;

        // 無効構成の間は投入自体を受け付けない（Issue #384。合成ルートの初期設定と同じ向き）。
        _queue.SetEnabled(configuration is not null);
    }

    /// <summary>
    /// 常設カード表示用の対（最終送信成功時刻・直近の失敗）。書き手は送信ループ、読み手は
    /// Blazor スレッド——<b>不変オブジェクトの参照 1 回の差し替え</b>で更新し、読み手が
    /// 「成功時刻と失敗が別々の時点の値」という不整合な対や torn read を見ない形にする
    /// （Issue #371——プロパティ 2 本の個別更新は 2 回読みの間に送信ループが割り込み得た）。
    /// </summary>
    private sealed record DeliveryHealth(DateTimeOffset? LastSuccessAt, EmailSendResult? LastFailure);

    private volatile DeliveryHealth _deliveryHealth = new(null, null);

    /// <summary>最終送信成功時刻（常設カードの表示用。決定 5）。</summary>
    internal DateTimeOffset? LastSuccessAt => _deliveryHealth.LastSuccessAt;

    /// <summary>
    /// 直近の失敗の分類と説明（常設カードの表示用）。読み手は本プロパティを<b>ローカルへ 1 回
    /// 読んでから</b>分類と説明を取り出すこと（2 回読むと間の送信成功で null 化され得る）。
    /// </summary>
    internal EmailSendResult? LastFailure => _deliveryHealth.LastFailure;

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

        // 無効の間は投入も受け付けない（Issue #384——有効化した瞬間に無効期間中の滞留分が
        // 流量制御を経ずに一斉送信されるのを防ぐ。有効化後は以後の発生分から送信が始まる）。
        _queue.SetEnabled(configuration is not null);

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
                // 1025 の抑制窓（15 分。security.md §4.3）はこの経路にも等しく適用する
                // （Issue #384——恒常的な例外で警告が毎周期〔5 秒間隔〕積み上がるのを防ぐ）。
                if (TryBeginSendFailureWarning())
                {
                    _logger.LogWarning(
                        EmailNotificationEventIds.SendFailed,
                        ex,
                        "メール通知の送信ループで予期しない例外が発生しました。次の周期で再開します。" +
                        "同種の警告は {SuppressionWindow} の間は再表示を抑制します。",
                        EmailNotificationConstants.SendFailureWarningSuppressionWindow);
                }
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

        // 抑制状態の開始警告（1026。security.md §4.3——状態が続く間 1 回だけ）。発火点は
        // 受信ホットパス上のロギング呼び出しのため、キューは状態の予約のみを行い、
        // ログはこの送信ループ側で書く（Issue #384）。
        AnnounceSuppressionOnsetIfPending();

        while (!cancellationToken.IsCancellationRequested && _queue.TryDequeueReady() is { } request)
        {
            EmailSendResult result;
            try
            {
                result = await _sender
                    .SendAsync(configuration, request.Subject, request.Body, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // IEmailSender は「例外を投げない」契約だが、契約違反でも dequeue 済みの通知を
                // 再試行なしに失わない——通常の失敗と同じ経路（LastFailure・再試行 1 回・
                // 抑制窓付き警告）へ倒す（PR #366 レビュー対応）。
                result = EmailSendResult.Failure(EmailSendFailureKind.Other, ex.Message);
            }

            if (result.Succeeded)
            {
                _deliveryHealth = new DeliveryHealth(_timeProvider.GetUtcNow(), null);
                WarnAboutRejectedRecipients(request, result);
                continue;
            }

            _deliveryHealth = _deliveryHealth with { LastFailure = result };
            _metrics?.RecordSendFailure(); // 決定 5 のライブ計器（Issue #386。再試行を含む試行ごと）

            // 再試行は 1 通あたり 1 回のみ（決定 5）。2 度目の失敗は破棄する——at-most-once。
            // 正本はイベントログと監査記録であり、届かないことを前提にした設計である。
            var retryScheduled = _queue.TryScheduleRetry(request);
            WarnAboutSendFailure(request, result, retryScheduled);

            if (result.FailureKind is EmailSendFailureKind.ConnectionFailed or EmailSendFailureKind.Timeout)
            {
                // サーバへ到達できない間は当該 drain を打ち切る（Issue #371）——接続不能は
                // 1 通あたり最大で接続タイムアウト（10 秒）を直列に消費するため、キューが深いと
                // 1 回の drain の滞在時間が RetryDelay（5 分）を超え、序盤に失敗した通知の再試行が
                // 同じ停止期間中に消費されて全滅する。打ち切れば再試行は次回以降の drain
                // （ポーリング間隔 5 秒）へ持ち越され、SMTP の復旧後まで生き残る余地が大きく増える。
                // 打ち切りは本ループだけの話であり、受信・保存・UI への影響経路は従来どおり無い。
                return;
            }
        }
    }

    /// <summary>
    /// 抑制状態の開始警告（1026）。キューが予約した「状態の開始」を 1 回だけ書き出す
    /// （security.md §4.3——抑制が発生している状態が続く間、イベントログへは 1 回だけ。
    /// 状態解消〔抑制なしで再送間隔が経過〕で自然に再武装される。Issue #384）。
    /// </summary>
    private void AnnounceSuppressionOnsetIfPending()
    {
        if (_queue.TryTakeSuppressionAnnouncement() is not { } onset)
        {
            return;
        }

        _logger.LogWarning(
            EmailNotificationEventIds.Throttled,
            "メール通知が流量制御により抑制されています（直近の契機: イベント ID {SuppressedEventId}・累積 {TotalSuppressedCount} 件）。" +
            "同一イベント ID の再送間隔（{ResendInterval}）または全体流量上限（{RateLimitMaxMessages} 通/{RateLimitWindow}）によるものです。" +
            "抑制が発生している状態が続く間、本警告は 1 回だけ記録します（内訳は管理画面のメール通知設定の常設カードを参照してください）。",
            onset.LastSuppressedEventId,
            onset.TotalSuppressedCount,
            EmailNotificationConstants.ResendInterval,
            EmailNotificationConstants.RateLimitMaxMessages,
            EmailNotificationConstants.RateLimitWindow);
    }

    /// <summary>
    /// 1025 の抑制窓（15 分。security.md §4.3）の共通判定。窓内なら <see langword="false"/>
    /// （警告を出さない）。送信失敗・部分拒否・送信ループの予期しない例外のすべての 1025 経路が
    /// 同じ窓を通る（Issue #384——一部経路だけ毎回出る状態を作らない）。
    /// </summary>
    private bool TryBeginSendFailureWarning()
    {
        var now = _timeProvider.GetUtcNow();
        if (_lastSendFailureWarningAt is { } lastAt
            && now - lastAt < EmailNotificationConstants.SendFailureWarningSuppressionWindow)
        {
            return false;
        }

        _lastSendFailureWarningAt = now;
        return true;
    }

    private void WarnAboutRejectedRecipients(EmailNotificationRequest request, EmailSendResult result)
    {
        if (result.RejectedRecipients.Count == 0 || !TryBeginSendFailureWarning())
        {
            return;
        }

        // 一部拒否は「メッセージとしては送信成功」とし再送しない（委任 7）——
        // 再送すると受理済みの宛先へ二重に届く。
        _logger.LogWarning(
            EmailNotificationEventIds.SendFailed,
            "メール通知（イベント ID {NotifiedEventId}）は送信されましたが、一部の宛先がサーバに受理されませんでした: {RejectedRecipients}。" +
            "受理済みの宛先への二重送信を避けるため再送は行いません。宛先の綴りと SMTP サーバの中継ポリシーを確認してください。" +
            "同種の警告は {SuppressionWindow} の間は再表示を抑制します。",
            request.EventId.Id,
            string.Join(", ", result.RejectedRecipients),
            EmailNotificationConstants.SendFailureWarningSuppressionWindow);
    }

    private void WarnAboutSendFailure(EmailNotificationRequest request, EmailSendResult result, bool retryScheduled)
    {
        // SMTP が長時間落ちている間、失敗のたびに警告を積まない（抑制窓付き。決定 5）。
        if (!TryBeginSendFailureWarning())
        {
            return;
        }

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
