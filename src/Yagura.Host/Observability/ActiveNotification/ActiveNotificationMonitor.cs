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
/// <b>自己検証の投入・タイムアウト判定（architecture.md §3.2.5。Issue #152）</b>:
/// 想定どおり <see cref="EvaluateOnceAsync"/> に評価メソッド（<c>EvaluateSpoolSelfTestAsync</c>）
/// を 1 つ追加する形で実装した。本メソッドは 2 つの役割を持つ——①
/// <see cref="ActiveNotificationConstants.SelfTestInterval"/>（仮値 1 日）ごとに合成レコード
/// （<see cref="SpoolRecord.ForSelfTest"/>）を <see cref="_spool"/> へ投入し、②直前に投入した
/// マーカーが <see cref="ActiveNotificationConstants.SelfTestTimeout"/>（仮値 10 分）以内に
/// drain へ合流判定されたか（<see cref="Yagura.Storage.Spool.SpoolSelfTestTracker"/> 経由）を
/// 判定する。合流判定の実体は drain 側（<c>Yagura.Ingestion.Persistence.SpoolDrainCoordinator</c>）
/// が同一の <see cref="_selfTestTracker"/> インスタンスへ通知する（<c>Yagura.Host.Program</c> が
/// 両者へ同一インスタンスを渡す構成）。<see cref="RunAsync"/> のループが包括的な例外保護を持つため
/// （PR #188 レビュー指摘への対応）、本メソッドが自前の例外処理を怠っても監視ループ自体が
/// 無警告で恒久停止することはない。**タイムアウト時、drain の進捗（スプール使用量の減少）が
/// 直近に観測されているかでバックログ起因（EventId 1010。警告）と経路障害の疑い（EventId 1009。
/// エラー）を判別する（Issue #202。PR #200 レビューのフォローアップ）**——詳細は
/// <c>EvaluateSpoolSelfTestAsync</c> の remarks 参照。
/// </para>
/// <para>
/// <b>スプール無効・縮退運転中の扱い</b>: <see cref="_spool"/> または <see cref="_selfTestTracker"/>
/// が <c>null</c>（スプール opt-out、またはスプール領域を開けなかった縮退運転。§1.2）の間は
/// 自己検証そのものを行わない——投入対象（スプール）が存在しないため投入せず、かつ「投入できて
/// いない」ことを重ねて警告もしない（縮退運転自体は別の通知——EventId 1001——で既にカバー済みで
/// あり、本メソッドが黙って何もしないことは「警告の二重化を避ける」意図的な設計である）。
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
    private readonly SpoolSelfTestTracker? _selfTestTracker;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ActiveNotificationMonitor> _logger;

    private readonly Dictionary<string, DateTimeOffset> _lastNotifiedAt = new(StringComparer.Ordinal);

    private long? _lastSpoolEvacuatedTotal;
    private DateTimeOffset? _evacuationStreakStartAt;
    private DateTimeOffset? _lastSelfTestInjectedAt;
    private long? _lastSelfTestObservedSpoolUsageBytes;
    private DateTimeOffset? _lastSelfTestDrainProgressObservedAt;

    private CancellationTokenSource? _stoppingCts;
    private Task? _loopTask;

    public ActiveNotificationMonitor(
        DiskSpool? spool,
        IngestionMetrics metrics,
        IMonitoredVolumeInfo volumeInfo,
        IExpressCapacityChecker expressChecker,
        TimeProvider? timeProvider = null,
        ILogger<ActiveNotificationMonitor>? logger = null,
        SpoolSelfTestTracker? selfTestTracker = null)
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
        _selfTestTracker = selfTestTracker;
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
        await EvaluateSpoolSelfTestAsync(cancellationToken).ConfigureAwait(false);
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
    /// スプールの定期自己検証（architecture.md §3.2.5。Issue #152）。
    /// <see cref="_spool"/> または <see cref="_selfTestTracker"/> が <c>null</c>（スプール
    /// opt-out・縮退運転）の間は投入・判定のいずれも行わない（クラス remarks 参照）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>実行順序</b>: タイムアウト判定を新規投入より先に行う——「直前に投入したマーカーが
    /// 期待時間内に drain へ合流したか」を、次のマーカーで上書きする前に確認する必要がある
    /// （<see cref="SpoolSelfTestTracker.BeginNewMarker"/> は未照合のまま残っていても上書きする
    /// 設計のため、判定の機会は投入直前の一度しかない）。
    /// </para>
    /// <para>
    /// <b>バックログ起因の判別（Issue #202。PR #200 レビューのフォローアップ）</b>: drain は FIFO
    /// のため、投入時点で未消化バックログが深い（§3.2.2 が「隠れた欠陥ではない」と明記する持続的な
    /// 速度不足の正常状態）と、経路自体は健全でもマーカーが期待時間内に合流しないことがある。
    /// 検討した 3 案（Issue #202）: ①投入時点のバックログ量を記録して判定に使う、②drain の進行
    /// （セグメント消化）を観測する、③タイムアウト値をバックログ量に比例させて再設計する。
    /// ①は「バックログが深い」ことと「経路が壊れている」ことを判別できない（両方とも投入時点の
    /// バックログは深いまま観測される）ため単独では使えない。③はバックログ量と所要時間の比例関係が
    /// 実測（M-16 も含め）で未検証であり、誤った比例式は「タイムアウトを長く取りすぎて実障害の
    /// 検知が遅れる」方向に倒れるリスクが①以上に高い。採用したのは②——<see cref="_spool"/> の
    /// 使用量（<see cref="DiskSpool.CurrentUsageBytes"/>）を周期ごとに観測し、前回周期から
    /// 減少していれば drain がセグメントを消化・削除できている直接証拠として扱う（新規追記だけでは
    /// 使用量は増えることはあっても減ることはなく、削除はセグメント単位で離散的に起こるため、
    /// 持続的な速度不足下でも「使用量の減少」は drain 成功の信頼できるシグナルになる）。判定は
    /// 「直近 <see cref="ActiveNotificationConstants.SelfTestTimeout"/> 以内に進捗を観測したか」
    /// で行う——観測していれば未消化バックログの滞留（EventId 1010。警告）、していなければ経路障害の
    /// 疑い（EventId 1009。エラー。従来どおり）とする。drain は 1 セグメントの書込失敗で以降の
    /// セグメントの消化を止める設計（<see cref="Yagura.Ingestion.Persistence.SpoolDrainCoordinator"/>
    /// の FIFO 順次処理）のため、この判定は「進捗が観測される限り経路は生きている」という近似では
    /// なく、「特定セグメントで恒久的に詰まれば以降の進捗も止まる」という構造上の性質に支えられて
    /// いる——判定を先送りし続けて実障害の検知が沈黙する事態（本節の留意事項）は、進捗が実際に
    /// 途絶えた時点で 1009 へ切り替わることで避ける（進捗のたびに再発火する 1010 と異なり、1010 は
    /// 1009 の発火を止めない——両者は独立したトリガキーで抑制窓を持つ）。
    /// </para>
    /// </remarks>
    private async Task EvaluateSpoolSelfTestAsync(CancellationToken cancellationToken)
    {
        if (_spool is null || _selfTestTracker is null)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();

        // drain 進捗の観測（上記 remarks 参照）: このメソッド自身の投入（後段）より前の使用量を
        // 前回周期末の使用量と比較する——前回周期の投入分は前回周期末のスナップショットに含まれる
        // ため、比較対象は「周期間に外部（drain・実トラフィックの退避）で起きた変化」に絞られる。
        var usageBytesAtCycleStart = _spool.CurrentUsageBytes;
        if (_lastSelfTestObservedSpoolUsageBytes is { } lastObservedUsageBytes &&
            usageBytesAtCycleStart < lastObservedUsageBytes)
        {
            _lastSelfTestDrainProgressObservedAt = now;
        }

        if (_selfTestTracker.IsPendingTimedOut(now, ActiveNotificationConstants.SelfTestTimeout))
        {
            var recentDrainProgress = _lastSelfTestDrainProgressObservedAt is { } progressAt &&
                now - progressAt < ActiveNotificationConstants.SelfTestTimeout;
            var usageRatio = _spool.UsageRatio;

            if (recentDrainProgress)
            {
                NotifyIfDue("spool-self-test-timeout-backlog", () =>
                    _logger.LogWarning(
                        ActiveNotificationEventIds.SpoolSelfTestTimeoutBacklog,
                        "[spool-self-test-timeout-backlog] スプールの定期自己検証（合成レコードの投入 → drain 合流" +
                        "判定）が期待時間 {Timeout} 以内に完了しませんでしたが、同じ期待時間内に drain の進捗" +
                        "（スプール使用量の減少）を観測しているため、経路は生きており未消化バックログの滞留" +
                        "（保存先の持続的な速度不足。architecture.md §3.2.2 が正常な運用状態と明記——スプール" +
                        "退避継続の警告 イベント ID 1004 が並行して出ている可能性が高い）と判定しました" +
                        "（architecture.md §3.2.5。Issue #202）。現在のスプール使用率 {UsageRatio:P0}。" +
                        "進捗が途絶えた場合は経路障害の疑いとしてイベント ID 1009 で改めて警告します。" +
                        "同種の警告は {SuppressionWindow} の間は再表示を抑制します。",
                        ActiveNotificationConstants.SelfTestTimeout,
                        usageRatio,
                        ActiveNotificationConstants.SuppressionWindow));
            }
            else
            {
                NotifyIfDue("spool-self-test-timeout", () =>
                    _logger.LogError(
                        ActiveNotificationEventIds.SpoolSelfTestFailed,
                        "[spool-self-test-timeout] スプールの定期自己検証（合成レコードの投入 → drain 合流判定）が" +
                        "期待時間 {Timeout} 以内に完了せず、同じ期間内に drain の進捗（スプール使用量の減少）も" +
                        "観測されませんでした（architecture.md §3.2.5。現在のスプール使用率 {UsageRatio:P0}）。" +
                        "未消化バックログの滞留であれば drain の進捗が観測されるはずのため、本通知はスプール経路" +
                        "（書込 → セグメント読出 → 逆直列化 → drain 合流判定）の障害が疑われる場合に絞って発火" +
                        "します（バックログ起因との判別は Issue #202）。同種の警告は {SuppressionWindow} の間は" +
                        "再表示を抑制します。",
                        ActiveNotificationConstants.SelfTestTimeout,
                        usageRatio,
                        ActiveNotificationConstants.SuppressionWindow));
            }
        }

        if (_lastSelfTestInjectedAt is { } lastInjectedAt &&
            now - lastInjectedAt < ActiveNotificationConstants.SelfTestInterval)
        {
            // 前回投入からまだ周期（仮値 1 日）に達していない。次周期の比較基準として、
            // このメソッド内で投入が起きなかった今回の使用量をそのまま記録する。
            _lastSelfTestObservedSpoolUsageBytes = usageBytesAtCycleStart;
            return;
        }

        _lastSelfTestInjectedAt = now;

        // 登録 → 書込 → 失敗時は登録取消、の順序（PR #200 レビュー指摘への対応）。
        // 書込に失敗したマーカーを未照合のまま残すと、drain に照合される見込みが無いまま
        // タイムアウト通知（別トリガキー）が次回投入（最大 1 日後）まで抑制窓ごとに反復発火する。
        // 登録自体は書込より先に行う——書込成功の確認より先に drain がレコードを読んで照合を
        // 通知し得るため、成功後に登録する方式は偽タイムアウトの競合を持つ
        // （SpoolSelfTestTracker.CancelPending の remarks 参照）。
        var marker = _selfTestTracker.BeginNewMarker(now);

        SpoolAppendResult result;
        try
        {
            result = await _spool.TryAppendAsync(SpoolRecord.ForSelfTest(marker), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // キャンセル・想定外例外のいずれでも、書き込まれた確証の無いマーカーを未照合のまま
            // 残さない（例外はループ側の包括保護（EventId 1008）またはキャンセル処理に委ねる）。
            _selfTestTracker.CancelPending(marker);
            throw;
        }

        if (result != SpoolAppendResult.Appended)
        {
            // 投入自体が失敗した——登録を取り消し、タイムアウトを待たず即座に警告する
            // （書込失敗はそれ自体が経路の破損を示す一次シグナルであり、次の判定機会まで
            // 待つ理由がない。通知は本 1 系統に一本化され、タイムアウト通知は発生しない）。
            _selfTestTracker.CancelPending(marker);
            NotifyIfDue("spool-self-test-write-failed", () =>
                _logger.LogError(
                    ActiveNotificationEventIds.SpoolSelfTestFailed,
                    "[spool-self-test-write-failed] スプールの定期自己検証用レコードの書き込みに失敗しました" +
                    "（結果: {Result}。architecture.md §3.2.5）。" +
                    "同種の警告は {SuppressionWindow} の間は再表示を抑制します。",
                    result,
                    ActiveNotificationConstants.SuppressionWindow));
        }

        // 次周期の比較基準として、今回の投入（成功・失敗いずれも）を反映した最新の使用量を記録する
        // （このメソッドの先頭で読む「周期開始時点の使用量」から投入分を除外するため——remarks 参照）。
        _lastSelfTestObservedSpoolUsageBytes = _spool.CurrentUsageBytes;
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
