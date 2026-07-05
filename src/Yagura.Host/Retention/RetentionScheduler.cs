using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yagura.Storage;

namespace Yagura.Host.Retention;

/// <summary>
/// 保持期間削除の定期実行スケジューラ（database.md §3）+ 容量枯渇契機の前倒し実行
/// （<see cref="ICapacityExhaustionHandler"/>。§1.2 契約 3・§4・§5.3）。
/// </summary>
/// <remarks>
/// <para>
/// <b>定期実行</b>: <see cref="RetentionSchedulerOptions.ExecutionTimeOfDay"/> に達するたびに、
/// <see cref="RetentionSchedulerOptions.RetentionDays"/> が設定されていれば
/// <c>基準時刻 - RetentionDays 日</c> より古いレコードを <see cref="ILogStore.DeleteOlderThanAsync"/>
/// で削除する。<see cref="RetentionSchedulerOptions.RetentionDays"/> が <c>null</c>
/// （「削除しない」。<see cref="RetentionSchedulerOptions"/> のドキュメント参照——設定ファイル
/// 未設定時の既定は 30 日であり、<c>null</c> になるのは不正値時のフォールバックの場合のみ）の
/// 場合、定期実行は何もしない（削除を試みない）。
/// </para>
/// <para>
/// <b>容量枯渇契機の前倒し実行（database.md §3 の譲歩条件の例外・§5.3）</b>:
/// <see cref="ICapacityExhaustionHandler.OnCapacityExhausted"/> の呼び出しは、定期実行の
/// 時間帯を待たず即座に削除を試みる。<b>保持期間が未設定の場合は前倒し実行もできない</b>
/// ——削除の基準となる cutoff（日数）自体が存在しないため、この場合は削除を試みず
/// 警告を出すに留める（容量枯渇の自走復旧は「保持期間が設定されている」ことが前提になる。
/// 本 Issue の設計判断）。
/// </para>
/// <para>
/// <b>削除実行の記録</b>: 削除を実行した（0 件であっても実行した事実そのもの）場合、
/// <see cref="ILogStore.WriteSystemEventAsync"/> で <c>Kind =
/// <see cref="RetentionConstants.SystemEventKindRetentionDelete"/></c> のシステムイベントを
/// 書き込む。<see cref="SystemEvent.Details"/> に削除件数を格納する（database.md §2.3）。
/// </para>
/// <para>
/// <b>スコープの明示（本 Issue の独自判断）</b>: database.md §3 は定期実行の実行判断側にも
/// 「スプール退避が進行中・Q2 が高水位・drain が進行中の間は削除を開始しない」譲歩条件を
/// 課している。この譲歩条件は <see cref="IngestionPipeline"/> の内部状態（Q2 使用率・
/// drain 進行状況）への新規の公開 API を要し、M5-1（ILogStore の契約完全化）のスコープを
/// 超えるため、本実装では譲歩条件を適用しない（常に実行する）——分割実行
/// （<see cref="ILogStore.DeleteOlderThanAsync"/> 自体の性質）により書き込みへの影響は
/// 抑えられているが、譲歩条件そのものの実装は後続 Issue（M5 の別チケットまたは M6）で
/// パイプライン側の観測 API を追加してから行う。この限定は最終報告で明示する。
/// </para>
/// </remarks>
public sealed class RetentionScheduler : ICapacityExhaustionHandler, IAsyncDisposable
{
    private readonly ILogStore _logStore;
    private readonly RetentionSchedulerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RetentionScheduler> _logger;
    private readonly SemaphoreSlim _executionGate = new(1, 1);

    private CancellationTokenSource? _stoppingCts;
    private Task? _schedulerTask;

    public RetentionScheduler(
        ILogStore logStore,
        RetentionSchedulerOptions options,
        TimeProvider? timeProvider = null,
        ILogger<RetentionScheduler>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(logStore);
        ArgumentNullException.ThrowIfNull(options);

        _logStore = logStore;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<RetentionScheduler>.Instance;
    }

    /// <summary>
    /// 定期実行ループを開始する。
    /// </summary>
    public void Start()
    {
        if (_stoppingCts is not null)
        {
            throw new InvalidOperationException("スケジューラは既に開始されている。");
        }

        _stoppingCts = new CancellationTokenSource();
        _schedulerTask = Task.Run(() => RunAsync(_stoppingCts.Token));
    }

    /// <summary>
    /// 定期実行ループを停止する。実行中の削除処理の完了は待たない
    /// （<see cref="ILogStore.DeleteOlderThanAsync"/> 自体の分割実行が長時間化を防ぐ設計のため、
    /// 停止処理側で明示的な打ち切りは行わない——本 Issue の設計判断）。
    /// </summary>
    public async Task StopAsync()
    {
        if (_stoppingCts is null)
        {
            return;
        }

        _stoppingCts.Cancel();

        if (_schedulerTask is not null)
        {
            try
            {
                await _schedulerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 停止要求による正常終了。
            }
        }

        _stoppingCts.Dispose();
        _stoppingCts = null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// database.md §3 の譲歩条件の例外として、時間帯を待たず即座に削除を試みる。
    /// 呼び出し元（永続化段・drain コーディネータ）の書き込みループを阻害しないよう、
    /// 完了を待たない fire-and-forget として実行する。
    /// </remarks>
    public void OnCapacityExhausted()
    {
        _ = Task.Run(() => TryExecuteRetentionDeleteAsync(CancellationToken.None, capacityExhaustionTriggered: true));
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _timeProvider.GetUtcNow();
            var delay = ComputeDelayUntilNextExecution(now, _options.ExecutionTimeOfDay);

            try
            {
                await Task.Delay(delay, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await TryExecuteRetentionDeleteAsync(stoppingToken, capacityExhaustionTriggered: false).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 次回実行までの待機時間を、現在時刻とサーバローカル時刻での実行時刻から計算する。
    /// </summary>
    internal static TimeSpan ComputeDelayUntilNextExecution(DateTimeOffset nowUtc, TimeOnly executionTimeOfDayLocal)
    {
        var nowLocal = nowUtc.ToLocalTime();
        var todayExecution = new DateTimeOffset(
            nowLocal.Year, nowLocal.Month, nowLocal.Day,
            executionTimeOfDayLocal.Hour, executionTimeOfDayLocal.Minute, 0,
            nowLocal.Offset);

        var nextExecution = todayExecution > nowLocal ? todayExecution : todayExecution.AddDays(1);
        return nextExecution - nowLocal;
    }

    /// <summary>
    /// 保持期間削除を 1 回試みる。<see cref="RetentionSchedulerOptions.RetentionDays"/> が
    /// <c>null</c>（削除しない既定）の場合は何もしない——容量枯渇契機であっても、削除対象の
    /// 基準（日数）自体が無いため実行できず、警告に留める（database.md §5.3「前倒し削除でも
    /// 回復しない場合…能動通知で保持期間の短縮…を促す」に近い扱いを、保持期間未設定の場合にも
    /// 準用する）。
    /// </summary>
    private async Task TryExecuteRetentionDeleteAsync(CancellationToken cancellationToken, bool capacityExhaustionTriggered)
    {
        if (_options.RetentionDays is not { } retentionDays)
        {
            if (capacityExhaustionTriggered)
            {
                _logger.LogWarning(
                    "[retention-not-configured] 容量枯渇を検知したが保持期間が未設定のため前倒し削除を実行できません。" +
                    "保持期間（日数）の設定を検討してください。");
            }

            return;
        }

        // 同時実行を避ける（定期実行と容量枯渇契機の前倒し実行が重ならないようにする）。
        if (!await _executionGate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("保持期間削除は既に実行中のため、今回の契機はスキップします。");
            return;
        }

        try
        {
            var cutoff = _timeProvider.GetUtcNow() - TimeSpan.FromDays(retentionDays);

            DeleteOlderThanResult result;
            try
            {
                result = await _logStore.DeleteOlderThanAsync(cutoff, cancellationToken).ConfigureAwait(false);
            }
            catch (LogStoreWriteException ex)
            {
                _logger.LogError(
                    ex,
                    "[retention-delete-failed] 保持期間削除の実行に失敗しました（cutoff={Cutoff:o}, capacityExhaustionTriggered={CapacityExhaustionTriggered}）。",
                    cutoff,
                    capacityExhaustionTriggered);
                return;
            }

            _logger.LogInformation(
                "[retention-delete-executed] 保持期間削除を実行しました: {DeletedCount} 件 (cutoff={Cutoff:o}, capacityExhaustionTriggered={CapacityExhaustionTriggered})。",
                result.DeletedCount,
                cutoff,
                capacityExhaustionTriggered);

            try
            {
                var executedAt = _timeProvider.GetUtcNow();
                await _logStore.WriteSystemEventAsync(
                    new SystemEvent(
                        Kind: RetentionConstants.SystemEventKindRetentionDelete,
                        StartAt: cutoff,
                        EndAt: executedAt,
                        Approximate: false,
                        Details: result.DeletedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (LogStoreWriteException ex)
            {
                // 削除の実行そのものは成功しているため、記録の失敗で削除処理自体を失敗扱いにはしない
                // （database.md §3「実行の失敗は…能動通知の対象とする」——ここでの失敗は「記録」の
                // 失敗であり、警告に留める）。
                _logger.LogWarning(
                    ex,
                    "[retention-delete-event-write-failed] 保持期間削除は成功しましたが、実行記録（システムイベント）の書き込みに失敗しました。");
            }
        }
        finally
        {
            _executionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _executionGate.Dispose();
    }
}
