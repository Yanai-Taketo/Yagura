using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yagura.Storage;

namespace Yagura.Host.Retention;

/// <summary>
/// 保持期間削除の定期実行スケジューラ（database.md §3）+ 容量枯渇契機の前倒し実行
/// （<see cref="ICapacityExhaustionHandler"/>。§1.2 契約 3・§4・§5.3）+ 起動時キャッチアップ
/// （Issue #150）。
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
/// <b>起動時キャッチアップ（Issue #150）</b>: <see cref="RunAsync"/> は定期実行ループへ入る前に
/// 一度だけ <see cref="TryCatchUpAsync"/> を実行する。日中のみ稼働する設置や、再起動が
/// 実行時刻（既定 03:00）付近に偏る環境では、定期実行が実行時刻に稼働していないホストで
/// 恒常的にスキップされ得る（「保持期間を設定した＝消える」という前提が静かに崩れる。
/// syslog 実務者ペルソナの深掘りレビュー指摘）。<see cref="TryCatchUpAsync"/> は
/// <see cref="ILogStore.QuerySystemEventsAsync"/> で直近の削除実行記録
/// （<c>Kind = RetentionConstants.SystemEventKindRetentionDelete</c>）を検索し、
/// 前回実行（またはその記録の不在）から <see cref="CatchUpThreshold"/> 以上経過していれば
/// 即座に削除を実行する。<b>新規のメタデータ領域は設けない</b>——削除実行の事実は
/// 既に <see cref="ILogStore.WriteSystemEventAsync"/> でシステムイベントとして記録済み
/// （下記「削除実行の記録」）であり、これを読み出すだけでキャッチアップ判定ができるため
/// （Issue 本文の提案「メタデータ領域に最終削除実行時刻を持たせる」との比較: 新規の永続化領域
/// ・書き込み経路を増やさずに済み、既存の記録と情報源が一本化される点を優先した。トレードオフは
/// システムイベントの検索コスト——<c>Kind = retention.delete</c> でサーバ側フィルタした直近
/// <see cref="CatchUpEventQueryLimit"/> 件の読み出し——を起動時に 1 回払う点。種別フィルタは
/// 必須である——種別を問わない直近 N 件の走査だと、削除記録の StartAt が意図的な過去日付
/// = cutoff である一方で受信断イベントの StartAt は実時刻という非対称により、受信断の蓄積が
/// 削除記録をウィンドウから恒常的に押し出す。PR #198 レビュー指摘・
/// <see cref="ILogStore.QuerySystemEventsAsync"/> の <c>kind</c> 引数の doc コメント参照）。
/// </para>
/// <para>
/// <b>二重実行の回避</b>: キャッチアップ実行の直後に、当日の定期実行時刻（
/// <see cref="ComputeDelayUntilNextExecution"/>）がわずかな遅延で到来し得る（例: 実行時刻の
/// 直前に再起動しキャッチアップが発火した場合）。<see cref="TryExecuteRetentionDeleteAsync"/> は
/// 実行のたびに <see cref="_lastExecutionAtUtc"/>（プロセス内メモリ。永続化しない）を更新し、
/// 容量枯渇契機（常に即時実行を優先する）以外の契機（定期実行・キャッチアップ）は、直前の
/// 実行から <see cref="MinimumReexecutionInterval"/> 未満しか経過していなければスキップする。
/// これは <see cref="_executionGate"/>（真の同時実行——同一瞬間の重複呼び出し——を防ぐ排他）とは
/// 別の防御であり、こちらは時間的に近接した「別々の契機による」重複実行を防ぐ。
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
/// <b>書き込みゲート（Issue #151）</b>: <see cref="ILogStore.DeleteOlderThanAsync"/> と
/// <see cref="ILogStore.WriteSystemEventAsync"/> の呼び出しは、コンストラクタで渡された
/// <see cref="LogStoreWriteGate"/>（非 <c>null</c> の場合）で、永続化段（ライブ書き込み）・
/// drain と直列化する——<see cref="ILogStore"/> の「書き込みは単一 writer が呼び出す」契約を
/// 実配線で満たすための共有ゲート。詳細な設計判断は <see cref="LogStoreWriteGate"/> の
/// doc コメントを参照。本スケジューラはライブ書き込みほど緊急性が高くないため、ゲート取得は
/// 固定タイムアウトなし（<see cref="CancellationToken"/> のみで打ち切る
/// <see cref="LogStoreWriteGate.AcquireAsync(CancellationToken)"/>）で待つ。
/// </para>
/// <para>
/// <b>スコープの明示（本 Issue の独自判断）</b>: database.md §3 は定期実行の実行判断側にも
/// 「スプール退避が進行中・Q2 が高水位・drain が進行中の間は削除を開始しない」譲歩条件を
/// 課している。この譲歩条件は <see cref="IngestionPipeline"/> の内部状態（Q2 使用率・
/// drain 進行状況）への新規の公開 API を要し、M5-1（ILogStore の契約完全化）のスコープを
/// 超えるため、本実装では譲歩条件を適用しない（常に実行する）——分割実行
/// （<see cref="ILogStore.DeleteOlderThanAsync"/> 自体の性質）と書き込みゲート（Issue #151）に
/// より書き込みへの影響は抑えられているが、譲歩条件そのものの実装は後続 Issue（M5 の
/// 別チケットまたは M6）でパイプライン側の観測 API を追加してから行う。この限定は
/// 最終報告で明示する。
/// </para>
/// </remarks>
public sealed class RetentionScheduler : ICapacityExhaustionHandler, IAsyncDisposable
{
    /// <summary>
    /// 起動時キャッチアップ（Issue #150）の閾値。前回の削除実行からこの時間以上経過していれば
    /// 即座に実行する。定期実行が「1 日 1 回」の運用である前提（既定 <see cref="RetentionSchedulerOptions.ExecutionTimeOfDay"/>
    /// は日付ではなく時刻のみを持つ）に合わせ、Issue 本文の提案どおり 1 日とする。
    /// </summary>
    internal static readonly TimeSpan CatchUpThreshold = TimeSpan.FromDays(1);

    /// <summary>
    /// キャッチアップ判定・二重実行防止に用いる「直近の実行とみなす」最小間隔。定期実行が
    /// 1 日 1 回である前提のもとで、キャッチアップ直後にすぐ定刻が到来しても再実行しない
    /// よう、1 日よりは十分短く、通常の実行間隔（1 日）よりは十分長い値を置く必要はない
    /// ——「同じ実行機会」とみなせる近接度であればよいため、実務上余裕を持たせた値とする。
    /// </summary>
    internal static readonly TimeSpan MinimumReexecutionInterval = TimeSpan.FromHours(12);

    /// <summary>
    /// 起動時キャッチアップの判定で読み出すシステムイベントの件数上限。検索は
    /// <c>Kind = retention.delete</c> でサーバ側フィルタ済み（PR #198 レビュー指摘への対応——
    /// 種別を問わない直近 N 件の走査だと、<c>retention.delete</c> の StartAt が意図的な過去日付
    /// = cutoff である一方、受信断イベントの StartAt は実時刻のため常に新しい側へ並び、受信断の
    /// 蓄積だけで削除記録がウィンドウから恒常的に押し出される非対称があった。<see cref="ILogStore.QuerySystemEventsAsync"/>
    /// の <c>kind</c> フィルタ参照）のため、この上限は「削除実行記録そのものが N 件を超えて並ぶ」
    /// 場合にのみ効く。判定に使うのは EndAt（実行時刻）の最大値であり、並び（StartAt 降順）と
    /// EndAt の順序は保持日数の設定変更をまたぐと厳密には一致しないため、1 件ではなく複数件を
    /// 読んで最大値を取る。見つからなければ「記録なし」として安全側（キャッチアップを実行する）に倒す。
    /// </summary>
    internal const int CatchUpEventQueryLimit = 200;

    private static readonly TimeSpan CatchUpQueryTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogStore _logStore;
    private RetentionSchedulerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RetentionScheduler> _logger;
    private readonly LogStoreWriteGate? _writeGate;
    private readonly SemaphoreSlim _executionGate = new(1, 1);

    private CancellationTokenSource? _stoppingCts;
    private Task? _schedulerTask;
    private DateTimeOffset? _lastExecutionAtUtc;

    public RetentionScheduler(
        ILogStore logStore,
        RetentionSchedulerOptions options,
        TimeProvider? timeProvider = null,
        ILogger<RetentionScheduler>? logger = null,
        LogStoreWriteGate? writeGate = null)
    {
        ArgumentNullException.ThrowIfNull(logStore);
        ArgumentNullException.ThrowIfNull(options);

        _logStore = logStore;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<RetentionScheduler>.Instance;
        _writeGate = writeGate;
    }

    /// <summary>
    /// 保持期間・実行時刻を実行中に更新する（設定ライブ再読み込み。CF-4 層1。Issue #262）。
    /// 実行ループ・削除試行は毎回 <c>_options</c> を参照するため、参照の原子的交換だけで
    /// 次回の判定から新値が使われる。<b>実行時刻（ExecutionTimeOfDay）の変更は、進行中の
    /// 待機（前回計算した遅延）には割り込まない</b>——次の実行機会の計算から反映される
    /// （日次実行の粒度では十分。即時反映が要る運用は保持日数側の変更が主であるため）。
    /// </summary>
    public void UpdateOptions(RetentionSchedulerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Volatile.Write(ref _options, options);
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
        _ = Task.Run(() => TryExecuteRetentionDeleteAsync(CancellationToken.None, RetentionExecutionTrigger.CapacityExhaustion));
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        // 起動時キャッチアップ（Issue #150）: 定期実行ループへ入る前に一度だけ判定する。
        // RetentionDays 未設定（削除しない既定）ならキャッチアップも対象がないため試みない
        // （TryExecuteRetentionDeleteAsync 自体も同じ条件で何もしないが、判定用の
        // QuerySystemEventsAsync 呼び出し自体を避けるため、ここで先に弾く）。
        if (_options.RetentionDays is not null)
        {
            await TryCatchUpAsync(stoppingToken).ConfigureAwait(false);
        }

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

            await TryExecuteRetentionDeleteAsync(stoppingToken, RetentionExecutionTrigger.Scheduled).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 起動時キャッチアップ（Issue #150）。前回の削除実行記録（システムイベント）を検索し、
    /// <see cref="CatchUpThreshold"/> 以上経過している（または記録が見つからない）場合は
    /// 即座に削除を試みる。判定用のシステムイベント検索自体が失敗した場合は、キャッチアップを
    /// 諦め通常の定期実行の待機に委ねる（DB 未初期化・障害中でも起動そのものは止めない、という
    /// 既存の設計原則——IngestionHostedService の受信断記録と同じ扱い——に合わせる）。
    /// </summary>
    private async Task TryCatchUpAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset? lastDeleteAt;
        try
        {
            using var timeoutCts = new CancellationTokenSource(CatchUpQueryTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);

            // Kind でサーバ側フィルタする（PR #198 レビュー指摘への対応。CatchUpEventQueryLimit の
            // doc コメント参照——種別を問わない走査では受信断イベントの蓄積が削除記録を
            // ウィンドウから押し出す非対称があった）。
            var recentDeleteEvents = await _logStore.QuerySystemEventsAsync(
                from: null,
                to: null,
                limit: CatchUpEventQueryLimit,
                timeout: CatchUpQueryTimeout,
                kind: RetentionConstants.SystemEventKindRetentionDelete,
                cancellationToken: linked.Token).ConfigureAwait(false);

            lastDeleteAt = recentDeleteEvents
                .Select(e => (DateTimeOffset?)e.EndAt)
                .Max();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[retention-catchup-query-failed] 起動時キャッチアップ判定のためのシステムイベント検索に失敗しました。" +
                "今回の起動では定期実行の待機に委ねます。");
            return;
        }

        var now = _timeProvider.GetUtcNow();
        if (lastDeleteAt is { } lastAt && now - lastAt < CatchUpThreshold)
        {
            _logger.LogInformation(
                "[retention-catchup-skip] 前回の保持期間削除（{LastDeleteAt:o}）から {Threshold} 未満のため、" +
                "起動時キャッチアップは実行しません。",
                lastAt,
                CatchUpThreshold);
            return;
        }

        if (lastDeleteAt is null)
        {
            _logger.LogInformation(
                "[retention-catchup] 過去の保持期間削除の実行記録が見つからないため、起動時キャッチアップを実行します。");
        }
        else
        {
            _logger.LogInformation(
                "[retention-catchup] 前回の保持期間削除（{LastDeleteAt:o}）から {Threshold} 以上経過しているため、" +
                "起動時キャッチアップを実行します。",
                lastDeleteAt.Value,
                CatchUpThreshold);
        }

        await TryExecuteRetentionDeleteAsync(stoppingToken, RetentionExecutionTrigger.CatchUp).ConfigureAwait(false);
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
    private async Task TryExecuteRetentionDeleteAsync(CancellationToken cancellationToken, RetentionExecutionTrigger trigger)
    {
        if (_options.RetentionDays is not { } retentionDays)
        {
            if (trigger == RetentionExecutionTrigger.CapacityExhaustion)
            {
                _logger.LogWarning(
                    "[retention-not-configured] 容量枯渇を検知したが保持期間が未設定のため前倒し削除を実行できません。" +
                    "保持期間（日数）の設定を検討してください。");
            }

            return;
        }

        // 二重実行の回避（Issue #150）: 容量枯渇契機は常に即時実行を優先するため対象外とする。
        // 定期実行・キャッチアップは、直前の実行から間もない場合はスキップする
        // （キャッチアップ直後に当日の定刻がわずかな遅延で到来するケースが主眼。本クラスの
        // remarks「二重実行の回避」参照）。
        if (trigger != RetentionExecutionTrigger.CapacityExhaustion &&
            _lastExecutionAtUtc is { } lastExecutionAt &&
            _timeProvider.GetUtcNow() - lastExecutionAt < MinimumReexecutionInterval)
        {
            _logger.LogInformation(
                "[retention-delete-recent-skip] 直前（{LastExecutionAt:o}）に保持期間削除を実行済みのため、" +
                "今回の契機（{Trigger}）はスキップします。",
                lastExecutionAt,
                trigger);
            return;
        }

        // 同時実行を避ける（定期実行・キャッチアップ・容量枯渇契機の前倒し実行が重ならないようにする）。
        if (!await _executionGate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("保持期間削除は既に実行中のため、今回の契機（{Trigger}）はスキップします。", trigger);
            return;
        }

        try
        {
            var cutoff = _timeProvider.GetUtcNow() - TimeSpan.FromDays(retentionDays);

            DeleteOlderThanResult result;
            try
            {
                // 書き込みゲート（Issue #151）: ライブ・drain と直列化する。緊急性より完遂を
                // 優先するため、固定タイムアウトなし（cancellationToken のみで打ち切る）で待つ
                // （LogStoreWriteGate の doc コメント参照）。
                //
                // 影響範囲の認識（PR #198 レビュー指摘）: 容量枯渇契機（OnCapacityExhausted）は
                // CancellationToken.None の fire-and-forget であるため、この待ちはその経路では
                // 実質無期限であり StopAsync でも中断できない。ゲートは正しく実装されていれば
                // 必ず解放されるため通常は問題にならないが、万一ゲート解放のリークバグが将来
                // 混入した場合、この経路は _executionGate を保持したまま永久待ちとなり、以後の
                // あらゆる契機（定期実行・キャッチアップ）の削除実行を巻き添えにする——
                // タイムアウトなしを選ぶ対価として明記しておく（現時点で対策——契機別トークンや
                // ゲート待ちの上限——を足さないのは、リーク前提の防御が過剰であり、_executionGate
                // の WaitAsync(TimeSpan.Zero) スキップにより「詰まる」事象自体はログ
                // （retention-delete の実行ログ途絶）から診断可能なため）。
                IDisposable? gateLease = _writeGate is null
                    ? null
                    : await _writeGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    result = await _logStore.DeleteOlderThanAsync(cutoff, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    gateLease?.Dispose();
                }
            }
            catch (LogStoreWriteException ex)
            {
                _logger.LogError(
                    ex,
                    "[retention-delete-failed] 保持期間削除の実行に失敗しました（cutoff={Cutoff:o}, trigger={Trigger}）。",
                    cutoff,
                    trigger);
                return;
            }

            _lastExecutionAtUtc = _timeProvider.GetUtcNow();

            _logger.LogInformation(
                "[retention-delete-executed] 保持期間削除を実行しました: {DeletedCount} 件 (cutoff={Cutoff:o}, trigger={Trigger})。",
                result.DeletedCount,
                cutoff,
                trigger);

            try
            {
                var executedAt = _timeProvider.GetUtcNow();

                IDisposable? gateLease = _writeGate is null
                    ? null
                    : await _writeGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await _logStore.WriteSystemEventAsync(
                        new SystemEvent(
                            Kind: RetentionConstants.SystemEventKindRetentionDelete,
                            StartAt: cutoff,
                            EndAt: executedAt,
                            Approximate: false,
                            Details: result.DeletedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    gateLease?.Dispose();
                }
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

/// <summary>
/// 保持期間削除の実行契機（Issue #150 でキャッチアップを追加し、ログ・二重実行防止の分岐が
/// 3 値になったため、従来の <c>bool capacityExhaustionTriggered</c> から列挙型へ整理した）。
/// </summary>
internal enum RetentionExecutionTrigger
{
    /// <summary>定期実行（<see cref="RetentionSchedulerOptions.ExecutionTimeOfDay"/> の定刻）。</summary>
    Scheduled,

    /// <summary>容量枯渇契機の前倒し実行（<see cref="ICapacityExhaustionHandler"/>）。</summary>
    CapacityExhaustion,

    /// <summary>起動時キャッチアップ（Issue #150）。</summary>
    CatchUp,
}
