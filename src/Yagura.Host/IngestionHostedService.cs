using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yagura.Host.Observability;
using Yagura.Host.Observability.ActiveNotification;
using Yagura.Host.Observability.ActiveNotification.Email;
using Yagura.Host.Retention;
using Yagura.Ingestion;
using Yagura.Storage;

namespace Yagura.Host;

/// <summary>
/// Generic Host のライフサイクルに <see cref="IngestionPipeline"/> を結線する
/// （architecture.md §1.2 起動順序・§1.3 停止順序）。
/// </summary>
/// <remarks>
/// <para>
/// <b>起動順序は受信先行</b>（§1.2）: <see cref="StartAsync"/> はまずリスナの listen を
/// 開始し、その後 DB の <see cref="ILogStore.InitializeAsync"/> を待ってから消費ループ
/// （解析段・永続化段）を開始する。DB 初期化の完了までの間に受信したデータグラムは
/// Q1・Q2 が緩衝する。
/// </para>
/// <para>
/// <b>M4-4 で追加したメタデータ領域・受信断可視化の結線</b>: <see cref="ObservabilityCoordinator"/>
/// を本クラスが所有し、起動・停止の各手順の間に明示的に呼び出す（architecture.md §1.2・§1.3・
/// §4.3・§4.4）。
/// <list type="bullet">
/// <item>起動: 受信開始（手順 2）より前にメタデータ領域を読み込みカウンタを引き継ぐ →
/// 受信開始 → 受信断区間を確定し <see cref="ILogStore.WriteSystemEventAsync"/> で記録 →
/// 定期永続化ループを開始</item>
/// <item>停止: 定期永続化ループを止める → リスナを閉じた直後にカウンタを書く（手順 1）→
/// drain（手順 2）→ 最終値を書き正常停止イベントを記録する（手順 3）</item>
/// </list>
/// </para>
/// <para>
/// Windows サービス統合（<c>UseWindowsService()</c> 等）は M3 で行った。
/// </para>
/// </remarks>
public sealed class IngestionHostedService : IHostedService
{
    private readonly IngestionPipeline _pipeline;
    private readonly ILogStore _logStore;
    private readonly ObservabilityCoordinator _observability;
    private readonly RetentionScheduler _retentionScheduler;
    private readonly ActiveNotificationMonitor _activeNotificationMonitor;
    private readonly EmailNotificationDispatcher _emailNotificationDispatcher;
    private readonly ILogger<IngestionHostedService> _logger;

    public IngestionHostedService(
        IngestionPipeline pipeline,
        ILogStore logStore,
        ObservabilityCoordinator observability,
        RetentionScheduler retentionScheduler,
        ActiveNotificationMonitor activeNotificationMonitor,
        EmailNotificationDispatcher emailNotificationDispatcher,
        ILogger<IngestionHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(logStore);
        ArgumentNullException.ThrowIfNull(observability);
        ArgumentNullException.ThrowIfNull(retentionScheduler);
        ArgumentNullException.ThrowIfNull(activeNotificationMonitor);
        ArgumentNullException.ThrowIfNull(emailNotificationDispatcher);
        ArgumentNullException.ThrowIfNull(logger);

        _pipeline = pipeline;
        _logStore = logStore;
        _observability = observability;
        _retentionScheduler = retentionScheduler;
        _activeNotificationMonitor = activeNotificationMonitor;
        _emailNotificationDispatcher = emailNotificationDispatcher;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // architecture.md §4.3「起動時に復元して継続する」: 受信開始（手順 2）より前に
        // メタデータ領域を読み込み、カウンタ累積値を IngestionMetrics へ引き継ぐ。
        // 受信が始まってからだと、引き継ぎ前に発生した破棄が種となる前回値へ上書きされてしまう
        // （SeedCumulativeCounters は加算ではなく上書きのため、加算開始前に済ませる必要がある）。
        _observability.LoadAndSeed();

        // 手順 2（§1.2）: 受信ソケットを開き、受信を開始する。DB 初期化より先に行う。
        // UDP・TCP は同時に開始する（M4-1 依頼「起動順序: UDP と同時（受信先行の一部）」）。
        var receiveStartedAt = DateTimeOffset.UtcNow;
        var startupResult = await _pipeline.StartListenerAsync(cancellationToken).ConfigureAwait(false);

        // 環境要因の bind 失敗は縮小継続 + CF-6 再試行（Issue #291。#141 の原子的起動は
        // 環境要因以外に限定して維持——IngestionPipeline.StartListenerAsync の remarks 参照）。
        // 縮小継続は警告（1022）で可視化する。受信再開は ListenerBindRecovered（Program 側で
        // 購読）が受信断区間として記録する。
        if (startupResult.IsDegraded)
        {
            var degraded = new List<string>(3);
            if (startupResult.Udp.Status == ListenerStartupStatus.DegradedRetrying)
            {
                degraded.Add($"UDP({startupResult.Udp.Error})");
            }

            if (startupResult.Tcp.Status == ListenerStartupStatus.DegradedRetrying)
            {
                degraded.Add($"TCP({startupResult.Tcp.Error})");
            }

            if (startupResult.Tls?.Status == ListenerStartupStatus.DegradedRetrying)
            {
                degraded.Add($"TLS({startupResult.Tls.Error})");
            }

            _logger.LogWarning(
                Yagura.Host.Configuration.ConfigurationEventIds.ListenerBindFailedDegradedStartup,
                "[listener-bind-degraded] 受信リスナの一部が bind できず、開けたリスナのみで縮小継続しています: {Degraded}。" +
                "開けなかったリスナは定期再試行（CF-6）が受信再開を試み続けます（configuration.md §4.1。Issue #291）。",
                string.Join(", ", degraded));
        }

        // 以下 2 行の英語文面は意図的に維持する（日本語化の対象外）。tools/Yagura.Bench の
        // BenchHostProcess と tests/Yagura.E2E.Tests 配下 5 ファイル（ZeroConfigFirstRunE2ETests・
        // ListenerSeparationE2ETests・SpoolDegradedStartupE2ETests・LoopbackBindingRegressionTests・
        // ListenerGuardAuditE2ETests）が、子プロセスの標準出力からこの英語文面
        // （"UDP/TCP syslog listener started on port"）を正規表現・文字列一致で読み取り、
        // 実バインドポートを取得する起動待ちマーカーとして使っている（grep で実体確認済み。
        // 2026-07-06）。Console と Windows イベントログは同じ ILogger 呼び出しを共有する配線
        // （Program.cs のコメント参照）のため、ここだけ文面を分離することはコンソール出力側の
        // 契約を保ったまま行えない。将来分離したい場合は Console 向け・イベントログ向けを
        // 別々の Log 呼び出しにする設計変更が必要（本 PR のスコープ外）。
        // 起動マーカーは実際に開いたリスナのみ出力する（縮小継続中のリスナの「port 0」出力で
        // E2E テスト・実機確認の起動待ちマーカーを偽装しない。Issue #291）。
        if (startupResult.Udp.Status == ListenerStartupStatus.Started)
        {
            _logger.LogInformation("UDP syslog listener started on port {Port}.", _pipeline.BoundPort);
        }

        if (startupResult.Tcp.Status == ListenerStartupStatus.Started)
        {
            _logger.LogInformation("TCP syslog listener started on port {Port}.", _pipeline.TcpBoundPort);
        }

        // TLS 受信（RFC 5425。opt-in。Issue #137）: 構成されている場合のみ出力する
        // （TlsBoundPort は TLS 受信が未構成——証明書未解決を含む——の間は null。
        // UDP/TCP と同じ文面規約に揃え、E2E テスト・実機確認での起動待ちマーカーとして使える
        // ようにする——本行が出ない場合は「TLS 受信は構成されていない/縮小継続した」ことを
        // 意味する）。
        if (_pipeline.TlsBoundPort is { } tlsBoundPort)
        {
            _logger.LogInformation("TLS syslog listener started on port {Port}.", tlsBoundPort);
        }

        // architecture.md §4.4: 受信開始が確定した時点で、前回終了までの記録から受信断区間を
        // 判定する（区間の確定は起動時に行う。§4.4「区間の確定は起動時に行い、保存は通常の
        // パイプラインを通す」）。DB 初期化前でも呼び出せるよう、書き込み自体は
        // ILogStore.WriteSystemEventAsync に委ねる（DB 障害時の扱いは M5 の契約完全化まで
        // ベストエフォート——本 Issue の依頼コメント「M5 の契約完全化で正式化される前提でよい」
        // に対応する）。
        //
        // 書き込みゲート（Issue #151。LogStoreWriteGate）を意図的に通さない: この呼び出しは
        // 消費ループ（永続化段・drain。StartConsumers）と保持期間スケジューラ
        // （_retentionScheduler.Start()）の開始より厳密に前——他の書き込み経路がまだ 1 つも
        // 動いていない時点——で実行されるため、非同時実行は起動順序により保証される
        // （ゲートによる保証ではない。起動順序をリファクタする場合はこの前提が崩れないか
        // 確認すること。ILogStore の doc コメント「ゲートを通らない第 4 の書き込み経路」参照）。
        var downtimeEvent = DowntimeRecorder.DetermineDowntimeEvent(_observability.PreviousState, receiveStartedAt);
        if (downtimeEvent is not null)
        {
            try
            {
                await _logStore.WriteSystemEventAsync(downtimeEvent, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "[downtime-recorded] 受信断区間を記録しました: {Kind} {StartAt:o} 〜 {EndAt:o} (Approximate={Approximate})",
                    downtimeEvent.Kind,
                    downtimeEvent.StartAt,
                    downtimeEvent.EndAt,
                    downtimeEvent.Approximate);
            }
            catch (Exception ex)
            {
                // DB 未初期化・障害中でも起動そのものは止めない（§1.2「DB や UI の初期化失敗・
                // 遅延が受信開始を遅らせない」と同じ原則）。M5 の契約完全化で耐障害経路
                // （スプール等）を通す前提のため、現時点では記録できなかったことを警告に留める。
                _logger.LogWarning(
                    ex,
                    "[downtime-record-failed] 受信断区間の記録に失敗しました: {Kind} {StartAt:o} 〜 {EndAt:o}",
                    downtimeEvent.Kind,
                    downtimeEvent.StartAt,
                    downtimeEvent.EndAt);
            }
        }

        // 手順 3（§1.2）: DB provider を初期化する。完了までの間は Q1・Q2 が緩衝になる
        // （スプールへの退避は M4。M2 時点は Q2 の容量とバックプレッシャで持ちこたえる）。
        await _logStore.InitializeAsync(cancellationToken).ConfigureAwait(false);

        _pipeline.StartConsumers();
        _logger.LogInformation("受信パイプラインの消費ループ（解析・永続化）を開始しました。");

        // メタデータ領域の定期永続化（§4.3・§4.4）はコンシューマ開始後に始める
        // （受信・消費が動き出してからカウンタ・生存時刻の定期観測を始めれば十分なため）。
        _observability.StartPeriodicPersistence();

        // 保持期間削除の定期実行（database.md §3・M5-1）もコンシューマ開始後に始める。
        // RetentionScheduler は容量枯渇契機の前倒し実行（ICapacityExhaustionHandler）としても
        // IngestionPipeline へ渡し済みのため、ここでは定期実行ループの開始のみを行う。
        _retentionScheduler.Start();
        _logger.LogInformation("保持期間削除の定期実行を開始しました。");

        // 能動通知の周期監視（architecture.md §4.6。M4-6。Issue #149）もコンシューマ開始後に
        // 始める。スプール使用率・退避継続・データルートの空き容量・Express 上限接近を
        // 定期評価し、閾値超過をイベントログへ警告として書き出す（トリガごとの抑制窓あり）。
        _activeNotificationMonitor.Start();
        _logger.LogInformation("能動通知の周期監視を開始しました。");

        // メール送信ループ（ADR-0017 決定 5。opt-in）。機能が無効・構成不備なら
        // ループは動くが何も送らない（設定の即時反映で有効化された時点から送り始める）。
        // 投入側（ILoggerProvider）は本ループの状態に関わらずキューへ積むため、
        // ここより前に発火した allowlist 内の通知も、開始後の最初の周期で送られる。
        _emailNotificationDispatcher.Start();
        _logger.LogInformation("メール通知の送信ループを開始しました。");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // 能動通知の周期監視を止める（保持期間削除と同様、受信・drain の停止順序とは独立のため
        // 早期に止めてよい）。
        await _activeNotificationMonitor.StopAsync().ConfigureAwait(false);

        // メール送信ループも同様に早期に止める。停止時にキューへ残った未送信通知は捨てる
        // ——SMTP の応答待ち（最大で接続 10 秒 + 送信 30 秒）でサービス停止を引き延ばさない。
        // メールは at-most-once であり、正本のイベントログには既に書かれている（決定 5）。
        await _emailNotificationDispatcher.StopAsync().ConfigureAwait(false);

        // 保持期間削除の定期実行を止める。
        await _retentionScheduler.StopAsync().ConfigureAwait(false);

        // 定期永続化ループを止める（以降はここで明示的に手順 1・3 の書き込みを行う）。
        await _observability.StopAsync().ConfigureAwait(false);

        // 手順 1（§1.3）: 受信ソケットを閉じ、その時点のカウンタをメタデータ領域へ書く。
        var receiveSocketClosedAt = DateTimeOffset.UtcNow;
        await _pipeline.StopListenersAsync().ConfigureAwait(false);
        _observability.WriteStopStep1(receiveSocketClosedAt);

        // 手順 2（§1.3）: メモリ上の未永続化ログをスプールへ退避する（DB を待たない）。
        // 退避中の破棄はレコード単位でカウンタへ逐次反映済み（ParsingStage/PersistenceWriter）。
        await _pipeline.DrainConsumersAsync().ConfigureAwait(false);

        // 手順 3（§1.3）: カウンタを最終値で永続化し、正常停止イベントを記録して終了する。
        _observability.WriteStopStep3(DateTimeOffset.UtcNow, fallbackReceiveSocketClosedAt: receiveSocketClosedAt);

        _logger.LogInformation("受信パイプラインを停止しました。");
    }
}
