using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yagura.Host.Observability;
using Yagura.Ingestion.Diagnostics;
using Yagura.Storage.Spool;

namespace Yagura.Host.Observability.ActiveNotification;

/// <summary>
/// architecture.md §4.6 の能動通知のうち、周期監視が必要な 4 トリガ（スプール使用率の
/// 上限接近・到達 / スプール退避の継続 / 監視対象ボリューム（データルート・スプール置き場所）の
/// 空き容量 / SQL Server Express の DB 容量接近）を評価する背景コンポーネント（Issue #149）。
/// </summary>
/// <remarks>
/// <para>
/// <b>スプール書込失敗は本クラスの対象外</b>: 発生箇所（<c>PersistenceWriter</c>）からの
/// 即時通知（抑制窓付き）で配線する——本 Issue の依頼どおり、周期監視ではなく発生時点で
/// 即座に警告する（発生から次の周期評価までの遅延を避ける）。
/// </para>
/// <para>
/// <b>自己検証の失敗（architecture.md §3.2.5）は本クラスに含めない</b>: 自己検証の機構自体が
/// 未実装（後続 Issue #152）であり、実装済みの検知点が無い。本クラスは
/// <see cref="EvaluateOnceAsync"/> に新しい評価メソッドを追加するだけで拡張できる構造にして
/// あり、#152 はその評価メソッドを 1 つ追加する形で乗せられる想定（最終報告に詳細を記す）。
/// <see cref="RunAsync"/> のループが包括的な例外保護を持つため（PR #188 レビュー指摘への対応）、
/// 追加する評価メソッドが自前の例外処理を怠っても、監視ループ自体が無警告で恒久停止することはない。
/// </para>
/// <para>
/// <b>ライフサイクルは <see cref="Yagura.Host.Retention.RetentionScheduler"/> と同じ形</b>: 独立の
/// <c>IHostedService</c> にはせず、<see cref="Yagura.Host.IngestionHostedService"/> が
/// <see cref="Start"/>/<see cref="StopAsync"/> を明示的に呼ぶ（本プロジェクトの既存の合意パターン）。
/// </para>
/// <para>
/// <b>抑制窓・継続判定はすべて <see cref="TimeProvider"/> 経由</b>: テストで
/// <c>Microsoft.Extensions.Time.Testing.FakeTimeProvider</c> により決定的に検証できるようにする
/// （本 Issue の依頼）。
/// </para>
/// <para>
/// <b>シングルスレッド前提</b>: <see cref="EvaluateOnceAsync"/> はテストから直接呼べるよう
/// <c>public</c> だが、内部状態（退避の連続検知・抑制窓の最終発火時刻）はロックなしで保持する。
/// 本番では <see cref="RunAsync"/> の単一ループからしか呼ばれない前提であり、並行呼び出しは
/// 想定しない（テストも 1 インスタンスを直列に操作すること）。
/// </para>
/// </remarks>
public sealed class ActiveNotificationMonitor : IAsyncDisposable
{
    private readonly DiskSpool? _spool;
    private readonly IngestionMetrics _metrics;
    private readonly IMonitoredVolumeInfo _volumeInfo;
    private readonly IExpressCapacityChecker _expressChecker;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ActiveNotificationMonitor> _logger;

    private readonly Dictionary<string, DateTimeOffset> _lastNotifiedAt = new(StringComparer.Ordinal);

    private long? _lastSpoolEvacuatedTotal;
    private DateTimeOffset? _evacuationStreakStartAt;

    private CancellationTokenSource? _stoppingCts;
    private Task? _loopTask;

    public ActiveNotificationMonitor(
        DiskSpool? spool,
        IngestionMetrics metrics,
        IMonitoredVolumeInfo volumeInfo,
        IExpressCapacityChecker expressChecker,
        TimeProvider? timeProvider = null,
        ILogger<ActiveNotificationMonitor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(volumeInfo);
        ArgumentNullException.ThrowIfNull(expressChecker);

        _spool = spool;
        _metrics = metrics;
        _volumeInfo = volumeInfo;
        _expressChecker = expressChecker;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ActiveNotificationMonitor>.Instance;
    }

    /// <summary>周期監視ループを開始する。</summary>
    public void Start()
    {
        if (_stoppingCts is not null)
        {
            throw new InvalidOperationException("監視は既に開始されている。");
        }

        _stoppingCts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunAsync(_stoppingCts.Token));
    }

    /// <summary>
    /// 周期監視ループを停止する。実行中の評価があればその終了（完了・キャンセル・例外の
    /// いずれか）を待ってから戻る——Express 容量チェックは <c>stoppingToken</c> を SqlClient まで
    /// 伝播させているため、通常は速やかにキャンセルされる（PR #188 レビュー指摘によりコメントの
    /// 精度を実装に合わせて修正した）。
    /// </summary>
    public async Task StopAsync()
    {
        if (_stoppingCts is null)
        {
            return;
        }

        _stoppingCts.Cancel();

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 停止要求による正常終了。
            }
        }

        _stoppingCts.Dispose();
        _stoppingCts = null;
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ActiveNotificationConstants.PollInterval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // 評価中の未捕捉例外でループを恒久停止させない（PR #188 レビュー指摘への対応）。
            // 能動通知は「UI を見ていない夜間でも運用者が気づける」ことが存在意義（architecture.md
            // §4.6）であり、監視自身が無警告で沈黙・停止する経路を残さない——例外はエラーとして
            // ログ（EventLog プロバイダ到達）へ記録し、次周期で再試行する。連発は他トリガと同じ
            // 抑制窓で抑える（評価対象の状態が変わらない限り同じ例外が毎周期出続けるため）。
            // この保護により、将来 EvaluateOnceAsync へ追加される評価メソッド（Issue #152 の
            // 自己検証等）が自前の例外処理を怠っても、ループの生存自体は損なわれない。
            try
            {
                await EvaluateOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                NotifyIfDue("evaluation-failed", () =>
                    _logger.LogError(
                        ActiveNotificationEventIds.EvaluationFailed,
                        ex,
                        "[active-notification-evaluation-failed] 能動通知の周期評価中に例外が発生しました。" +
                        "監視ループは継続し、次周期（{PollInterval} 後）に再試行します。" +
                        "同種の警告は {SuppressionWindow} の間は再表示を抑制します。",
                        ActiveNotificationConstants.PollInterval,
                        ActiveNotificationConstants.SuppressionWindow));
            }
        }
    }

    /// <summary>
    /// 1 周期分の評価をまとめて行う。テストから直接呼べるよう公開する
    /// （<see cref="Microsoft.Extensions.Time.Testing.FakeTimeProvider"/> で時刻を進めながら
    /// 繰り返し呼び出すことで、継続判定・抑制窓を決定的に検証できる）。
    /// </summary>
    public async Task EvaluateOnceAsync(CancellationToken cancellationToken = default)
    {
        EvaluateSpoolUsage();
        EvaluateSpoolEvacuationContinuation();
        EvaluateMonitoredVolumesFreeSpace();
        await EvaluateExpressCapacityAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>スプール使用量の上限接近・到達（architecture.md §3.2.3・§4.6）。</summary>
    private void EvaluateSpoolUsage()
    {
        if (_spool is null)
        {
            // スプール無効・縮退運転中は使用量自体が存在しない
            // （縮退はスプールなし起動の通知で別途カバー済み。architecture.md §1.2）。
            return;
        }

        var ratio = _spool.UsageRatio;

        if (ratio >= ActiveNotificationConstants.SpoolReachedRatio)
        {
            NotifyIfDue("spool-quota-reached", () =>
                _logger.LogWarning(
                    ActiveNotificationEventIds.SpoolQuotaReached,
                    "[spool-quota-reached] スプール使用量が上限に到達しました（使用率 {UsageRatio:P0}）。" +
                    "以降の退避対象は破棄されます（architecture.md §3.2.3）。保存先の復旧、または" +
                    "保持期間の見直しを検討してください。同種の警告は {SuppressionWindow} の間は再表示を抑制します。",
                    ratio,
                    ActiveNotificationConstants.SuppressionWindow));
        }
        else if (ratio >= SystemStatusReader.SpoolNearLimitRatio)
        {
            NotifyIfDue("spool-near-limit", () =>
                _logger.LogWarning(
                    ActiveNotificationEventIds.SpoolQuotaNearLimit,
                    "[spool-near-limit] スプール使用量が上限に接近しています（使用率 {UsageRatio:P0}、閾値 {Threshold:P0}）。" +
                    "同種の警告は {SuppressionWindow} の間は再表示を抑制します。",
                    ratio,
                    SystemStatusReader.SpoolNearLimitRatio,
                    ActiveNotificationConstants.SuppressionWindow));
        }
    }

    /// <summary>スプール退避の継続（architecture.md §3.2.2・§4.6・§5.3「持続的な速度不足」）。</summary>
    private void EvaluateSpoolEvacuationContinuation()
    {
        if (_spool is null)
        {
            _lastSpoolEvacuatedTotal = null;
            _evacuationStreakStartAt = null;
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var current = _metrics.SnapshotCumulativeCounters().SpoolEvacuated;

        if (_lastSpoolEvacuatedTotal is { } last && current > last)
        {
            // 前回周期からの間に退避が発生した——継続中のストリークとして扱う。
            _evacuationStreakStartAt ??= now;

            if (now - _evacuationStreakStartAt.Value >= ActiveNotificationConstants.EvacuationContinuationDuration)
            {
                var duration = now - _evacuationStreakStartAt.Value;
                NotifyIfDue("spool-evacuation-continuing", () =>
                    _logger.LogWarning(
                        ActiveNotificationEventIds.SpoolEvacuationContinuing,
                        "[spool-evacuation-continuing] スプールへの退避が {Duration} 以上継続しています。" +
                        "保存先への書き込み速度が受信速度に追いついていない可能性があります" +
                        "（architecture.md §3.2.2・§5.3）。同種の警告は {SuppressionWindow} の間は再表示を抑制します。",
                        duration,
                        ActiveNotificationConstants.SuppressionWindow));
            }
        }
        else
        {
            // 前回周期からの増分が無い——ストリークをリセットする。
            _evacuationStreakStartAt = null;
        }

        _lastSpoolEvacuatedTotal = current;
    }

    /// <summary>
    /// 監視対象ボリューム（データルート・スプール置き場所。同一ボリュームなら読み取り側で
    /// 1 件に重複排除済み——<see cref="MonitoredVolumeInfo"/>）の空き容量（architecture.md §4.6。
    /// database.md §3・§5.3。スプール置き場所のボリュームを含めるのは PR #188 レビュー指摘への対応）。
    /// </summary>
    private void EvaluateMonitoredVolumesFreeSpace()
    {
        foreach (var reading in _volumeInfo.ReadMonitoredVolumes())
        {
            if (reading.AvailableFreeSpaceBytes < ActiveNotificationConstants.MonitoredVolumeFreeSpaceMinBytes)
            {
                // 抑制窓はボリューム単位で独立させる（データルートとスプールが別ドライブの場合、
                // 片方の警告がもう片方を抑制しないように。キーの数は監視対象パス数（最大 2）で
                // 有界であり、辞書の無制限増加は起きない）。
                NotifyIfDue($"volume-free-space-low:{reading.VolumeRoot}", () =>
                    _logger.LogWarning(
                        ActiveNotificationEventIds.MonitoredVolumeFreeSpaceLow,
                        "[volume-free-space-low] 監視対象ボリューム {VolumeRoot} の空き容量が {AvailableFreeSpaceBytes} バイト" +
                        "（閾値 {ThresholdBytes} バイト）まで減少しました。同種の警告は {SuppressionWindow} の間は再表示を抑制します。",
                        reading.VolumeRoot,
                        reading.AvailableFreeSpaceBytes,
                        ActiveNotificationConstants.MonitoredVolumeFreeSpaceMinBytes,
                        ActiveNotificationConstants.SuppressionWindow));
            }
        }
    }

    /// <summary>SQL Server Express の DB 容量接近（database.md §5.3・architecture.md §4.6）。</summary>
    private async Task EvaluateExpressCapacityAsync(CancellationToken cancellationToken)
    {
        ExpressCapacityReading? reading;
        try
        {
            reading = await _expressChecker.CheckAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // IExpressCapacityChecker の契約は取得不能を null で表す想定だが、実装側の保険的な
            // 受け皿として本メソッド自体は落とさない（本監視ループ全体の恒久停止を避ける）。
            _logger.LogDebug(ex, "Express Edition の容量判定に失敗したため、この周期はスキップします。");
            return;
        }

        if (reading is null)
        {
            return;
        }

        if (reading.UsageRatio >= ActiveNotificationConstants.ExpressNearLimitRatio)
        {
            NotifyIfDue("express-capacity-near-limit", () =>
                _logger.LogWarning(
                    ActiveNotificationEventIds.ExpressCapacityNearLimit,
                    "[express-capacity-near-limit] SQL Server Express の DB サイズが上限に接近しています" +
                    "（使用率 {UsageRatio:P0}、現在 {DatabaseSizeBytes} バイト、上限 {MaxDatabaseSizeBytes} バイト）。" +
                    "SQL Server の通常エディションへのアップグレード、または保持期間の短縮を検討してください。" +
                    "同種の警告は {SuppressionWindow} の間は再表示を抑制します。",
                    reading.UsageRatio,
                    reading.DatabaseSizeBytes,
                    reading.MaxDatabaseSizeBytes,
                    ActiveNotificationConstants.SuppressionWindow));
        }
    }

    /// <summary>
    /// <paramref name="triggerKey"/> ごとに、<see cref="ActiveNotificationConstants.SuppressionWindow"/>
    /// 以内に既に発火していれば抑制する（<c>PersistenceWriter.ShouldEmitPermanentFailureWarning</c>
    /// と同じ設計。連発の抑制。architecture.md §4.6）。
    /// </summary>
    private void NotifyIfDue(string triggerKey, Action emit)
    {
        var now = _timeProvider.GetUtcNow();

        if (_lastNotifiedAt.TryGetValue(triggerKey, out var lastAt) &&
            now - lastAt < ActiveNotificationConstants.SuppressionWindow)
        {
            return;
        }

        _lastNotifiedAt[triggerKey] = now;
        emit();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
