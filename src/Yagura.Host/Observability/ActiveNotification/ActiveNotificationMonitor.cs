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
/// 無警告で恒久停止することはない。**タイムアウト時、drain の進捗（消化済みセグメントの削除の
/// 累積カウンタ <see cref="DiskSpool.DeletedSegmentsTotal"/> の増分）が直近に観測されているかで
/// バックログ起因（EventId 1010。警告）と経路障害の疑い（EventId 1009。エラー）を判別する
/// （Issue #202。PR #200 レビューのフォローアップ）**——詳細は
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
    private readonly IAdminHttpsCertificateStatusProbe? _adminHttpsCertificateProbe;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ActiveNotificationMonitor> _logger;

    private readonly Dictionary<string, DateTimeOffset> _lastNotifiedAt = new(StringComparer.Ordinal);

    private long? _lastSpoolEvacuatedTotal;
    private DateTimeOffset? _evacuationStreakStartAt;
    private DateTimeOffset? _lastSelfTestInjectedAt;
    private long? _lastSelfTestObservedDeletedSegments;
    private DateTimeOffset? _lastSelfTestDrainProgressObservedAt;
    private bool _selfTestFailureLatched;

    private CancellationTokenSource? _stoppingCts;
    private Task? _loopTask;

    public ActiveNotificationMonitor(
        DiskSpool? spool,
        IngestionMetrics metrics,
        IMonitoredVolumeInfo volumeInfo,
        IExpressCapacityChecker expressChecker,
        TimeProvider? timeProvider = null,
        ILogger<ActiveNotificationMonitor>? logger = null,
        SpoolSelfTestTracker? selfTestTracker = null,
        IAdminHttpsCertificateStatusProbe? adminHttpsCertificateProbe = null)
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
        _adminHttpsCertificateProbe = adminHttpsCertificateProbe;
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
        EvaluateAdminHttpsCertificate();
    }

    /// <summary>
    /// 管理リスナのリモート HTTPS 証明書の期限接近・稼働中の使用不能を検知する
    /// （ADR-0010 Phase 2 決定 4。PR #224 レビュー指摘 #2・#3 への対応。
    /// <see cref="IAdminHttpsCertificateStatusProbe"/> の remarks 参照）。
    /// リモートバインド opt-in が無効・起動時に証明書を解決できず縮小継続した構成
    /// （プローブ未注入 = <see langword="null"/>）では何もしない——後者は起動時警告
    /// （EventId 1013）が既に一度報告しており、再起動なしに bind が有効化されることもないため、
    /// 周期監視の対象にしない（重複警告の抑制。<c>Program</c> の結線コメント参照）。
    /// </summary>
    private void EvaluateAdminHttpsCertificate()
    {
        if (_adminHttpsCertificateProbe is null)
        {
            return;
        }

        var status = _adminHttpsCertificateProbe.Check();
        var now = _timeProvider.GetUtcNow();

        if (!status.IsAvailable)
        {
            NotifyIfDue("admin-https-certificate-unavailable", () =>
                _logger.LogWarning(
                    ActiveNotificationEventIds.AdminHttpsCertificateUnavailableWhileRunning,
                    "[admin-https-certificate-unavailable-while-running] 管理リスナのリモート HTTPS 証明書が" +
                    "使用できなくなりました（理由: {Reason}）。リモート HTTPS の新規接続は受け付けられません。" +
                    "loopback 経由の管理リスナ・syslog 受信は影響を受けません（ADR-0010 Phase 2 決定 4）。" +
                    "証明書の再取り込み・設定の見直し後、反映にはサービス再起動が必要です。" +
                    "同種の警告は {SuppressionWindow} の間は再表示を抑制します。",
                    status.FailureReason,
                    ActiveNotificationConstants.SuppressionWindow));
            return;
        }

        if (now > status.NotAfter)
        {
            // 期限切れへの遷移（稼働中）。Kestrel の ServerCertificateSelector が新規 TLS
            // ハンドシェイクを拒否している状態を、ハンドシェイク単位ではなく状態として周期通知する
            // （個々のハンドシェイク失敗イベントを Kestrel から拾う配線は持たない——
            // security.md §2.5 の限界明示のとおり）。
            NotifyIfDue("admin-https-certificate-unavailable", () =>
                _logger.LogWarning(
                    ActiveNotificationEventIds.AdminHttpsCertificateUnavailableWhileRunning,
                    "[admin-https-certificate-unavailable-while-running] 管理リスナのリモート HTTPS 証明書の" +
                    "有効期限（{NotAfter}）が切れました。リモート HTTPS の新規 TLS ハンドシェイクは拒否されています。" +
                    "loopback 経由の管理リスナ・syslog 受信は影響を受けません（ADR-0010 Phase 2 決定 4）。" +
                    "証明書を更新し、サービスを再起動してください。" +
                    "同種の警告は {SuppressionWindow} の間は再表示を抑制します。",
                    status.NotAfter,
                    ActiveNotificationConstants.SuppressionWindow));
            return;
        }

        var remaining = status.NotAfter - now;
        if (remaining <= ActiveNotificationConstants.AdminHttpsCertificateExpiryWarningWindow)
        {
            NotifyIfDue("admin-https-certificate-expiry-approaching", () =>
                _logger.LogWarning(
                    ActiveNotificationEventIds.AdminHttpsCertificateExpiryApproaching,
                    "[admin-https-certificate-expiry-approaching] 管理リスナのリモート HTTPS 証明書の" +
                    "有効期限が接近しています（期限: {NotAfter}、残り {RemainingDays:F1} 日、警告閾値: {WarningWindow}）。" +
                    "期限切れになるとリモート HTTPS の新規接続は拒否されます（loopback 経由の管理リスナは" +
                    "影響を受けません）。証明書の更新を計画してください。" +
                    "同種の警告は {SuppressionWindow} の間は再表示を抑制します。",
                    status.NotAfter,
                    remaining.TotalDays,
                    ActiveNotificationConstants.AdminHttpsCertificateExpiryWarningWindow,
                    ActiveNotificationConstants.SuppressionWindow));
        }
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
    /// 検知が遅れる」方向に倒れるリスクが①以上に高い。採用したのは②——drain がセグメントを消化・
    /// 削除するたびに単調増加する累積カウンタ（<see cref="DiskSpool.DeletedSegmentsTotal"/>）を
    /// 周期ごとに観測し、前回周期から増えていれば drain の進捗の直接証拠として扱う
    /// （<see cref="EvaluateSpoolEvacuationContinuation"/> が <c>SpoolEvacuated</c> 累積カウンタで
    /// 行う継続判定と同じパターン）。<see cref="DiskSpool.CurrentUsageBytes"/> の周期サンプリング
    /// 差分（純増減）を使わないのは PR #211 レビュー指摘への対応——持続的な速度不足（追記速度が
    /// 消化速度を恒常的に上回る状態）では、drain が実際にセグメントを消化していても任意の 1 分
    /// サンプルで純減少が一度も観測されず「進捗なし = 1009」に誤分類される。まさに本判別が対象と
    /// する高負荷滞留の場面で機能しないため、追記と混ざらない削除専用の累積カウンタへ分離した。
    /// 判定は「直近 <see cref="ActiveNotificationConstants.SelfTestTimeout"/> 以内に進捗を観測
    /// したか」で行う——観測していれば未消化バックログの滞留（EventId 1010。警告）、していなければ
    /// 経路障害の疑い（EventId 1009。エラー。従来どおり）とする。drain は 1 セグメントの書込失敗で
    /// 以降のセグメントの消化を止める設計（<see cref="Yagura.Ingestion.Persistence.SpoolDrainCoordinator"/>
    /// の FIFO 順次処理）のため、この判定は「進捗が観測される限り経路は生きている」という近似では
    /// なく、「特定セグメントで恒久的に詰まれば以降の進捗も止まる」という構造上の性質に支えられて
    /// いる——判定を先送りし続けて実障害の検知が沈黙する事態（本節の留意事項）は、進捗が実際に
    /// 途絶えた時点で 1009 へ切り替わることで避ける（進捗のたびに再発火する 1010 と異なり、1010 は
    /// 1009 の発火を止めない——両者は独立したトリガキーで抑制窓を持つ）。
    /// </para>
    /// <para>
    /// <b>1009 へのエスカレーションはマーカー単位でラッチする（PR #211 レビュー指摘への対応）</b>:
    /// 未照合マーカーは次回投入（最大 1 日後）まで残り、タイムアウト判定は毎周期評価され続ける
    /// ため、ラッチが無いと「進捗途絶で 1009 → 単発の進捗で 1010 へ回帰 → 再途絶で 1009 → …」の
    /// 振動（flapping）が起こり得る。同一の根本原因に対して独立した抑制窓を持つ 2 つの ID が交互に
    /// 再発火するノイズを避けるため、<b>一度 1009 と判定した後は、当該マーカーの追跡が終わる
    /// （照合される・次のマーカーで上書きされる）まで 1010 へ戻さない</b>——進捗が丸ごと
    /// 期待時間分途絶えた事実は「経路のどこかが詰まった疑い」の観測であり、その後の単発進捗は
    /// 疑いを晴らす証拠として弱い（マーカー自身は依然未照合のまま）。マーカーが実際に照合されれば
    /// 経路の健全性はそこで実証され、次のマーカーの投入時にラッチは解除される（新しい検証は白紙から
    /// 判定する）。
    /// </para>
    /// </remarks>
    private async Task EvaluateSpoolSelfTestAsync(CancellationToken cancellationToken)
    {
        if (_spool is null || _selfTestTracker is null)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();

        // drain 進捗の観測（上記 remarks 参照）: drain がセグメントを消化・削除するたびに単調増加
        // する累積カウンタを前回周期と比較する。追記（実トラフィックの退避・本メソッド自身の投入）
        // はこのカウンタに影響しないため、追記と削除が同一周期内に混在していても取りこぼさない
        // （PR #211 レビュー指摘への対応——使用量の純増減サンプリングは持続的な速度不足下で
        // 進捗を見落とす）。
        var deletedSegmentsTotal = _spool.DeletedSegmentsTotal;
        if (_lastSelfTestObservedDeletedSegments is { } lastDeletedSegments &&
            deletedSegmentsTotal > lastDeletedSegments)
        {
            _lastSelfTestDrainProgressObservedAt = now;
        }

        _lastSelfTestObservedDeletedSegments = deletedSegmentsTotal;

        if (_selfTestTracker.IsPendingTimedOut(now, ActiveNotificationConstants.SelfTestTimeout))
        {
            // 一度 1009（経路障害の疑い）と判定したら、当該マーカーの追跡が終わるまで 1010 へ
            // 戻さない（ラッチ。remarks 参照——単発の進捗回復で 1009/1010 が交互に再発火する
            // 振動を避ける）。ラッチは次のマーカー投入時に解除される。
            var recentDrainProgress = !_selfTestFailureLatched &&
                _lastSelfTestDrainProgressObservedAt is { } progressAt &&
                now - progressAt < ActiveNotificationConstants.SelfTestTimeout;
            var usageRatio = _spool.UsageRatio;

            if (recentDrainProgress)
            {
                NotifyIfDue("spool-self-test-timeout-backlog", () =>
                    _logger.LogWarning(
                        ActiveNotificationEventIds.SpoolSelfTestTimeoutBacklog,
                        "[spool-self-test-timeout-backlog] スプールの定期自己検証（合成レコードの投入 → drain 合流" +
                        "判定）が期待時間 {Timeout} 以内に完了しませんでしたが、同じ期待時間内に drain の進捗" +
                        "（消化済みセグメントの削除）を観測しているため、経路は生きており未消化バックログの滞留" +
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
                _selfTestFailureLatched = true;
                NotifyIfDue("spool-self-test-timeout", () =>
                    _logger.LogError(
                        ActiveNotificationEventIds.SpoolSelfTestFailed,
                        "[spool-self-test-timeout] スプールの定期自己検証（合成レコードの投入 → drain 合流判定）が" +
                        "期待時間 {Timeout} 以内に完了せず、同じ期間内に drain の進捗（消化済みセグメントの削除）も" +
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
            // 前回投入からまだ周期（仮値 1 日）に達していない。
            return;
        }

        _lastSelfTestInjectedAt = now;

        // 新しいマーカーの検証は白紙から判定する——前マーカーで 1009 へエスカレートしていた
        // ラッチはここで解除する（remarks 参照）。
        _selfTestFailureLatched = false;

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
