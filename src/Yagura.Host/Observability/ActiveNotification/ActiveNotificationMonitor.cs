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
    private readonly IAdminHttpsCertificateStatusProbe? _ingestionTlsCertificateProbe;
    private readonly Yagura.Host.Administration.AdminAuthentication.AdminAuthFailureDefense? _adminAuthFailureDefense;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ActiveNotificationMonitor> _logger;

    private readonly Dictionary<string, DateTimeOffset> _lastNotifiedAt = new(StringComparer.Ordinal);

    /// <summary>現在保持している通知抑制エントリ数（テスト・可観測性用。#314 の掃引検証）。</summary>
    internal int SuppressionEntryCount => _lastNotifiedAt.Count;

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
        IAdminHttpsCertificateStatusProbe? adminHttpsCertificateProbe = null,
        IAdminHttpsCertificateStatusProbe? ingestionTlsCertificateProbe = null,
        Yagura.Host.Administration.AdminAuthentication.AdminAuthFailureDefense? adminAuthFailureDefense = null,
        SourceSilence.SourceSilenceDetector? sourceSilenceDetector = null,
        Func<Yagura.Ingestion.ListenerAvailabilitySnapshot>? listenerAvailabilityProbe = null,
        Func<CancellationToken, Task<IReadOnlyList<Yagura.Storage.SourceActivity>>>? sourceActivitySeedQuery = null)
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
        _ingestionTlsCertificateProbe = ingestionTlsCertificateProbe;
        _adminAuthFailureDefense = adminAuthFailureDefense;
        _sourceSilenceDetector = sourceSilenceDetector;
        _listenerAvailabilityProbe = listenerAvailabilityProbe;
        _sourceActivitySeedQuery = sourceActivitySeedQuery;
    }

    /// <summary>
    /// 起動時 seed の照会口（ADR-0018 決定 3。Issue #381。<c>ILogStore.QuerySourceActivityAsync</c> を
    /// 結線する）。未注入（テスト等）や照会失敗時は seed を行わず、全エントリが起動時刻仮基準の
    /// まま動く——決定 3 の照会失敗時の規定どおり。
    /// </summary>
    private readonly Func<CancellationToken, Task<IReadOnlyList<Yagura.Storage.SourceActivity>>>? _sourceActivitySeedQuery;

    /// <summary>
    /// 送信元の途絶検知（ADR-0018。opt-in のため <see langword="null"/> 可）。
    /// 判定の実体は <see cref="SourceSilence.SourceSilenceDetector"/> が持ち、本クラスは
    /// 既存の周期評価から呼ぶだけ——ADR-0018 の「新しい常駐機構は作らない」に従う。
    /// </summary>
    private readonly SourceSilence.SourceSilenceDetector? _sourceSilenceDetector;

    /// <summary>
    /// 受信リスナの現在の受信可否の問い合わせ口（ADR-0018 委任 6。
    /// <see cref="Yagura.Ingestion.IngestionPipeline.ListenerAvailability"/> を結線する）。
    /// 未注入（<see langword="null"/>。テスト等）の間は「受信断保留なし・経路状態は不明」として振る舞う。
    /// </summary>
    private readonly Func<Yagura.Ingestion.ListenerAvailabilitySnapshot>? _listenerAvailabilityProbe;

    /// <summary>
    /// 前回周期の受信断保留の観測値。true → false への遷移（受信経路の回復）で
    /// <see cref="SourceSilence.SourceSilenceDetector.RearmAfterReceptionRecovery"/> を呼ぶための状態。
    /// 判定器はスレッド安全でない（本クラスの単一ループからのみ触る前提）ため、回復の検知も
    /// <see cref="Yagura.Ingestion.IngestionPipeline.ListenerBindRecovered"/> の購読（バックグラウンド
    /// スレッドから発火する）ではなく、周期評価内のこのポーリングで行う——保留の解除が最大 1 周期
    /// （1 分）遅れるが、閾値の下限（10 分）に対して判定へ影響しない。
    /// </summary>
    private bool _receptionWasSuspended;

    /// <summary>周期監視ループを開始する。</summary>
    public void Start()
    {
        if (_stoppingCts is not null)
        {
            throw new InvalidOperationException("監視は既に開始されている。");
        }

        _stoppingCts = new CancellationTokenSource();

        // 起動時 seed（ADR-0018 決定 3。Issue #381）: 監視ループと並行に 1 回だけ DB を照会し、
        // ウォッチリスト該当エントリの基準を DB の最終受信時刻へ置き換える。ループ開始を
        // ブロックしない（seed 完了前の周期評価は起動時刻仮基準で判定される——閾値下限 10 分に
        // 対し seed は数秒で終わるため実害はない）。
        if (_sourceSilenceDetector is not null && _sourceActivitySeedQuery is not null)
        {
            _ = Task.Run(() => SeedSourceSilenceBaselineAsync(_stoppingCts.Token));
        }

        _loopTask = Task.Run(() => RunAsync(_stoppingCts.Token));
    }

    /// <summary>
    /// 起動時 seed の実行（ADR-0018 決定 3）。照会失敗は起動時刻仮基準へのフォールバックで
    /// あり機能停止ではないため、警告ではなく情報レベルで記録する。
    /// </summary>
    private async Task SeedSourceSilenceBaselineAsync(CancellationToken cancellationToken)
    {
        try
        {
            var activities = await _sourceActivitySeedQuery!(cancellationToken).ConfigureAwait(false);
            _sourceSilenceDetector!.SeedFromStore(activities);
        }
        catch (OperationCanceledException)
        {
            // 停止と競合しただけ。何もしない。
        }
        catch (Exception ex)
        {
            _logger.LogInformation(
                ex,
                "送信元の途絶検知の起動時 seed（最終受信時刻の照会）に失敗したため、" +
                "全エントリを起動時刻仮基準で追跡します（ADR-0018 決定 3 のフォールバック。" +
                "活発な送信元は直後の実受信で更新されるため実害はありません）。");
        }
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
        EvaluateIngestionTlsCertificate();
        EvaluateAdminAuthFailureDefense();
        EvaluateSourceSilence();
        PruneStaleNotificationSuppression();
    }

    /// <summary>
    /// アプリ独自認証の三層防御の能動通知への昇格（ADR-0011 決定 6）。<see cref="_adminAuthFailureDefense"/>
    /// が未注入（アプリ独自認証が結線されていない構成）の間は何もしない。バックオフ・IP レート制限・
    /// グローバルトークンバケットのいずれも、cap 到達/拒否状態が
    /// <see cref="Yagura.Host.Administration.AdminAuthenticationDefaults.EscalationThreshold"/>
    /// （仮値 15 分）以上継続した場合に通知する——通知本文には主因層・上位送信元 IP を含める
    /// （決定 6 の本文要件）。抑制窓はトリガキー（アカウントキー/送信元 IP 単位）ごとに独立させる
    /// （<see cref="EvaluateMonitoredVolumesFreeSpace"/> と同じパターン）。
    /// </summary>
    /// <remarks>
    /// <b>IP レート制限のアイドルエントリ掃引（Issue #233。PR #236 レビュー指摘で条件を修正）</b>:
    /// 評価の先頭で
    /// <see cref="Yagura.Host.Administration.AdminAuthentication.AdminAuthFailureDefense.SweepIdleIpRateLimitEntries"/>
    /// を毎周期（仮値 1 分）呼ぶ——送信元 IP をキーにした状態辞書は攻撃者が制御できる次元（IP
    /// アドレス）に無制限に増加し得るため（非実在ユーザー名に状態を持たせない設計の「状態空間は
    /// 運用者制御」という根拠が成立しない）、これを周期的に縮退させる。除去条件は「窓失効かつ
    /// （拒否ストリークを持たない、または staleness-cap ≒ 2×エスカレーション閾値を超えて窓が凍結）」
    /// ——①拒否ストリーク中（<c>DenyStreakStartAtUtc</c> 設定済み——下記ループのエスカレーション判定の
    /// 起点）で毎窓アクセスが続くペース調整型攻撃のエントリは保持し（消すと能動通知が永久に発火
    /// しなくなるため。それ自体が進行中の実攻撃でありエスカレーション対象）、②ストリークを立てて
    /// 放置された撃ち逃げピン（窓が凍結）は約 2×閾値 経過後に除去してメモリ有界性を回復する
    /// （放置ストリークも 15 分で 1 回はエスカレーションを出してから片付く）。<b>辞書サイズは
    /// 「現に進行中で通知対象の攻撃者数」で有界</b>であり、攻撃者が任意に膨らませられる恒久ピン留めは
    /// 残らない（詳細は <c>SweepIdleIpRateLimitEntries</c> の remarks 参照）。
    /// </remarks>
    /// <summary>
    /// 送信元の途絶を評価し、1027／1028／1029 を書き出す（ADR-0018 決定 3）。
    /// </summary>
    /// <remarks>
    /// <b>本メソッドは既存の抑制窓（<see cref="NotifyIfDue"/>）を通さない</b>——途絶検知は
    /// エントリ別の抑制窓を判定器側に持っており（決定 3。粒度が既存のトリガ別抑制窓と違う）、
    /// ここで二重に律速すると装置 A の発火が装置 B の初報を飲む。判定器が「出す」と決めたものは
    /// そのまま出す。
    /// </remarks>
    private void EvaluateSourceSilence()
    {
        if (_sourceSilenceDetector is null)
        {
            return;
        }

        // サーバ都合の受信断との区別（決定 3。委任 6）: 構成済みの全リスナが受信不能な間は
        // 途絶判定を保留し、回復（true → false の遷移）で全エントリを回復時点で再アームする
        // （起動時の再アームと同一規則——固定グレース値を置かず、各エントリの再検知は当該
        // エントリの閾値で律速する）。部分受信断は保留しない——警告 Detail への経路状態の
        // 併記（{ReceptionPath}）で対応する。
        var availability = _listenerAvailabilityProbe?.Invoke();
        var receptionSuspended = availability?.AllListenersDown ?? false;

        if (!_receptionWasSuspended && receptionSuspended)
        {
            _logger.LogInformation(
                "全受信リスナが受信不能のため、送信元の途絶判定を保留します（ADR-0018 決定 3。" +
                "受信経路の回復までは途絶への遷移を行いません）。");
        }
        else if (_receptionWasSuspended && !receptionSuspended)
        {
            var rearmedCount = _sourceSilenceDetector.RearmAfterReceptionRecovery();
            _logger.LogInformation(
                "受信経路の回復を検知したため、送信元の途絶判定を再開しました。保留中に閾値超過と" +
                "なったエントリ {RearmedCount} 件を回復時点で再アームしました（ADR-0018 決定 3。" +
                "対象外のエントリは追跡時計を保ち、本来の「最終受信 + 閾値」で判定されます）。",
                rearmedCount);
        }

        _receptionWasSuspended = receptionSuspended;

        var evaluation = _sourceSilenceDetector.Evaluate(receptionSuspended);

        if (evaluation.IsBurst)
        {
            _logger.LogWarning(
                SourceSilence.SourceSilenceEventIds.SourceSilenceBurstDetected,
                "登録済み送信元 {Count} 件が同一周期に一斉に途絶しました: {Entries}。" +
                "個別の装置障害より、サーバ側の受信経路（リスナ・ファイアウォール・経路）や" +
                "上流の共通機器の障害を先に確認してください" +
                "（サーバ側受信経路の現在の状態: {ReceptionPath}）。" +
                "なお、サービス起動後に同じ閾値のエントリが揃って再アームされた場合も" +
                "同時発火し得ます（独立した障害の寄せ集めである可能性）。",
                evaluation.Silences.Count,
                string.Join(", ", evaluation.Silences.Select(FormatEntry)),
                FormatReceptionPath(availability));
        }
        else
        {
            foreach (var silence in evaluation.Silences)
            {
                _logger.LogWarning(
                    SourceSilence.SourceSilenceEventIds.SourceSilenceDetected,
                    "登録済み送信元 {Entry} からの受信が途絶しています（閾値 {Threshold}・経過 {Elapsed}・" +
                    "サーバ側受信経路の状態: {ReceptionPath}）。" +
                    "装置の生死、意図した設定変更・機器障害の有無を確認してください。" +
                    "いずれでもない場合、証跡の遮断を伴うセキュリティ事象の可能性も検討してください。" +
                    "送信元アドレスが変わった（DHCP・機器リプレース）場合はウォッチリストの更新が必要です。",
                    FormatEntry(silence),
                    silence.Threshold,
                    silence.Elapsed,
                    FormatReceptionPath(availability));
            }
        }

        foreach (var recovery in evaluation.Recoveries)
        {
            // 情報レベル（決定 3）。能動通知はしないが、途絶警告と対で
            // 「ログが欠けていた期間」の終端を証跡に残す。
            _logger.LogInformation(
                SourceSilence.SourceSilenceEventIds.SourceSilenceRecovered,
                "登録済み送信元 {Entry} からの受信が再開しました。",
                FormatEntry(recovery));
        }
    }

    private static string FormatEntry(SourceSilence.SourceSilenceEvent entry) =>
        entry.Label is null ? entry.Address.ToString() : $"{entry.Address}（{entry.Label}）";

    /// <summary>
    /// 警告 Detail に併記するサーバ側受信経路の状態（決定 3——真因がサーバ側なのに運用者を
    /// 装置側の調査へ誘導しない。部分受信断はこの併記で対応する）。プローブ未注入の間は「不明」。
    /// </summary>
    private static string FormatReceptionPath(Yagura.Ingestion.ListenerAvailabilitySnapshot? availability) =>
        availability is null
            ? "不明"
            : $"UDP={(availability.Udp ? "受信中" : "受信不能")}・" +
              $"TCP={(availability.Tcp ? "受信中" : "受信不能")}・" +
              $"TLS={availability.Tls switch { null => "未構成", true => "受信中", false => "受信不能" }}";

    private void EvaluateAdminAuthFailureDefense()
    {
        if (_adminAuthFailureDefense is null)
        {
            return;
        }

        _adminAuthFailureDefense.SweepIdleIpRateLimitEntries();

        var now = _timeProvider.GetUtcNow();

        foreach (var escalation in _adminAuthFailureDefense.GetBackoffEscalations())
        {
            var duration = now - escalation.CapReachedSinceUtc;
            var listenerKind = escalation.IsLoopback ? "loopback" : "remote";

            NotifyIfDue($"admin-auth-backoff-cap:{escalation.UsernameNormalized}:{listenerKind}", () =>
                _logger.LogWarning(
                    ActiveNotificationEventIds.AdminAuthFailureDefenseEscalated,
                    "[admin-auth-backoff-cap-continuing] アプリ独自認証のアカウント {UsernameNormalized}" +
                    "（{ListenerKind} 経由）でバックオフが上限（cap）に張り付いた状態が {Duration} 以上" +
                    "継続しています（連続失敗回数 n={FailedAttemptCount}。主因層: バックオフ）。" +
                    "持続的な総当たり試行が疑われます。直近の送信元 IP（上位）: {RecentSourceAddresses}。" +
                    "同種の警告は {SuppressionWindow} の間は再表示を抑制します。",
                    escalation.UsernameNormalized,
                    listenerKind,
                    duration,
                    escalation.FailedAttemptCount,
                    string.Join(", ", escalation.RecentSourceAddresses),
                    ActiveNotificationConstants.SuppressionWindow));
        }

        foreach (var escalation in _adminAuthFailureDefense.GetIpRateLimitEscalations())
        {
            var duration = now - escalation.DenyStreakStartAtUtc;

            NotifyIfDue($"admin-auth-ip-rate-limit:{escalation.RemoteAddress}", () =>
                _logger.LogWarning(
                    ActiveNotificationEventIds.AdminAuthFailureDefenseEscalated,
                    "[admin-auth-ip-rate-limit-continuing] アプリ独自認証への送信元 IP {RemoteAddress} からの" +
                    "試行が IP レート制限により {Duration} 以上継続して拒否されています" +
                    "（主因層: IP レート制限）。同種の警告は {SuppressionWindow} の間は再表示を抑制します。",
                    escalation.RemoteAddress,
                    duration,
                    ActiveNotificationConstants.SuppressionWindow));
        }

        var bucketEscalation = _adminAuthFailureDefense.GetGlobalBucketEscalation();
        if (bucketEscalation is not null)
        {
            var duration = now - bucketEscalation.DenyStreakStartAtUtc;

            NotifyIfDue("admin-auth-global-bucket", () =>
                _logger.LogWarning(
                    ActiveNotificationEventIds.AdminAuthFailureDefenseEscalated,
                    "[admin-auth-global-bucket-continuing] アプリ独自認証のグローバルトークンバケットが" +
                    "涸渇した状態が {Duration} 以上継続しています（主因層: グローバルトークンバケット。" +
                    "プロセス全体の事象）。直近の拒否送信元 IP（上位）: {RecentSourceAddresses}。" +
                    "同種の警告は {SuppressionWindow} の間は再表示を抑制します。",
                    duration,
                    string.Join(", ", bucketEscalation.RecentSourceAddresses),
                    ActiveNotificationConstants.SuppressionWindow));
        }
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

    /// <summary>
    /// TLS 受信（RFC 5425。opt-in。security.md §6。Issue #137）証明書の期限接近・稼働中の
    /// 使用不能を検知する。TLS 受信が無効・起動時に証明書を解決できず縮小継続した構成
    /// （プローブ未注入 = <see langword="null"/>）では何もしない
    /// （<see cref="EvaluateAdminHttpsCertificate"/> と同じ「重複警告の抑制」判断——後者は起動時
    /// 警告 EventId 1016 が既に一度報告済みで、再起動なしに TLS 受信が有効化されることもない）。
    /// </summary>
    /// <remarks>
    /// <b>管理 UI HTTPS（<see cref="EvaluateAdminHttpsCertificate"/>）との非対称</b>: 管理 UI HTTPS は
    /// 期限切れを「新規ハンドシェイクを拒否している状態」として通知するが、TLS 受信は
    /// 「止めない」設計（security.md §6）のため、期限切れ後も新規ハンドシェイクは引き続き受理
    /// される——本メソッドが出す通知はいずれも状態の可視化のみを目的とし、リスナの挙動を変えない
    /// （文言もその前提で書く）。
    /// </remarks>
    private void EvaluateIngestionTlsCertificate()
    {
        if (_ingestionTlsCertificateProbe is null)
        {
            return;
        }

        var status = _ingestionTlsCertificateProbe.Check();
        var now = _timeProvider.GetUtcNow();

        if (!status.IsAvailable)
        {
            NotifyIfDue("ingestion-tls-certificate-unavailable", () =>
                _logger.LogWarning(
                    ActiveNotificationEventIds.IngestionTlsCertificateUnavailableWhileRunning,
                    "[ingestion-tls-certificate-unavailable-while-running] TLS 受信証明書がストアから" +
                    "参照できなくなりました（理由: {Reason}）。起動時に読み込み済みの証明書はプロセス内に" +
                    "保持されているため、TLS 受信リスナは動作を継続します（security.md §6「止めない」判断）。" +
                    "証明書を更新・再取り込みした場合は反映にサービス再起動が必要です。" +
                    "同種の警告は {SuppressionWindow} の間は再表示を抑制します。",
                    status.FailureReason,
                    ActiveNotificationConstants.SuppressionWindow));
            return;
        }

        if (now > status.NotAfter)
        {
            NotifyIfDue("ingestion-tls-certificate-unavailable", () =>
                _logger.LogWarning(
                    ActiveNotificationEventIds.IngestionTlsCertificateUnavailableWhileRunning,
                    "[ingestion-tls-certificate-unavailable-while-running] TLS 受信証明書の有効期限" +
                    "（{NotAfter}）が切れました。TLS 受信リスナは期限切れの証明書のまま受信を継続します" +
                    "（security.md §6「止めない」判断——受信断より通信の真正性の低下を許容する）。" +
                    "送信側の証明書検証ポリシー次第では TLS ハンドシェイクが拒否され得ます——送信元別の" +
                    "ハンドシェイク失敗カウンタ（yagura.ingestion.tcp.tls_handshake_failure）と送信元別の" +
                    "受信状況（無音化検出）をあわせて確認し、脱落があれば証明書を更新してください。" +
                    "同種の警告は {SuppressionWindow} の間は再表示を抑制します。",
                    status.NotAfter,
                    ActiveNotificationConstants.SuppressionWindow));
            return;
        }

        var remaining = status.NotAfter - now;
        if (remaining <= ActiveNotificationConstants.AdminHttpsCertificateExpiryWarningWindow)
        {
            NotifyIfDue("ingestion-tls-certificate-expiry-approaching", () =>
                _logger.LogWarning(
                    ActiveNotificationEventIds.IngestionTlsCertificateExpiryApproaching,
                    "[ingestion-tls-certificate-expiry-approaching] TLS 受信証明書の有効期限が接近しています" +
                    "（期限: {NotAfter}、残り {RemainingDays:F1} 日、警告閾値: {WarningWindow}）。期限切れに" +
                    "なっても TLS 受信リスナは止まりません（security.md §6）が、送信側の検証ポリシー次第で" +
                    "ハンドシェイクが拒否され始める可能性があります。証明書の更新を計画してください。" +
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

    /// <summary>
    /// 抑制窓を十分に超過した <see cref="_lastNotifiedAt"/> エントリを間引く（<see cref="EvaluateOnceAsync"/>
    /// の末尾から毎周期呼ぶ）。トリガキーには送信元 IP（<c>admin-auth-ip-rate-limit:{RemoteAddress}</c> 等、
    /// 攻撃者が制御できる次元）を含むものがあり、<see cref="NotifyIfDue"/> は挿入のみで除去しないため、
    /// 放置すると辞書が単調増加する（<c>AdminAuthFailureDefense.SweepIdleIpRateLimitEntries</c> #233 と
    /// 同種の掃引）。抑制窓を超えたエントリはもう抑制に寄与しない（<see cref="NotifyIfDue"/> の判定が必ず
    /// 通り、通れば <c>now</c> で上書きされる）ため安全に除去できる。単一スレッド前提（クラス remarks）の
    /// ため素の <see cref="Dictionary{TKey,TValue}"/> をロックなしで操作してよい。
    /// </summary>
    private void PruneStaleNotificationSuppression()
    {
        var cutoff = _timeProvider.GetUtcNow() - (ActiveNotificationConstants.SuppressionWindow * 2);

        List<string>? stale = null;
        foreach (var (key, lastAt) in _lastNotifiedAt)
        {
            if (lastAt <= cutoff)
            {
                (stale ??= new()).Add(key);
            }
        }

        if (stale is null)
        {
            return;
        }

        foreach (var key in stale)
        {
            _lastNotifiedAt.Remove(key);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
