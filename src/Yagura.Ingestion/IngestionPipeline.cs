using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Parsing;
using Yagura.Ingestion.Persistence;
using Yagura.Ingestion.Tcp;
using Yagura.Ingestion.Tls;
using Yagura.Ingestion.Udp;
using Yagura.Storage;
using Yagura.Storage.Spool;

namespace Yagura.Ingestion;

/// <summary>
/// UDP/TCP 受信 → 解析 → 永続化の 3 段パイプライン全体の結線（architecture.md §2.1）。
/// </summary>
/// <remarks>
/// ホスト（Yagura.Host）は本クラスを介してパイプラインを起動・停止する。
/// 起動順序（§1.2「受信を最初に開く」）・停止順序（§1.3）の制御はホスト側が担う——
/// <see cref="StartListenerAsync"/> と <see cref="StartConsumers"/> を分けているのは、
/// ホストが「受信開始 → DB 初期化 → 消費ループ開始 → drain 開始」の順を組み立てられる
/// ようにするため。UDP・TCP の両リスナは同じ Q1 へ投入する（M4-1）。起動は「受信先行」の
/// 一部として同時に行い、停止は「リスナ停止 → 接続クローズ → drain（DB を待たずスプールへ
/// 退避。§1.3）」の順とする。
/// </remarks>
public sealed class IngestionPipeline : IAsyncDisposable
{
    private readonly Channel<RawDatagram> _q1;
    private readonly Channel<LogRecord> _q2;
    private UdpSyslogListener _udpListener;
    private TcpSyslogListener _tcpListener;
    private readonly TlsSyslogListener? _tlsListener;
    private readonly ParsingStage _parsingStage;
    private readonly PersistenceWriter _persistenceWriter;
    private readonly SpoolDrainCoordinator? _drainCoordinator;
    private readonly IngestionMetrics _metrics;
    private readonly ILogger<IngestionPipeline>? _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly IIngressGate _ingressGate;
    private readonly SemaphoreSlim _reconfigureGate = new(1, 1);

    private UdpSyslogListenerOptions _udpOptions;
    private TcpSyslogListenerOptions _tcpOptions;
    private CancellationTokenSource? _bindRetryCts;
    private Task? _bindRetryTask;

    private CancellationTokenSource? _consumerStoppingCts;
    private Task? _parsingTask;
    private Task? _persistenceTask;
    private Task? _drainTask;

    /// <summary>CF-6: bind 失敗後の定期再試行の間隔（仮値 30 秒。実測確定は CF-6）。</summary>
    internal static readonly TimeSpan DefaultBindRetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>再試行間隔（テストが短縮するための observation point。既定は <see cref="DefaultBindRetryInterval"/>）。</summary>
    internal TimeSpan BindRetryInterval { get; set; } = DefaultBindRetryInterval;

    public IngestionPipeline(UdpSyslogListenerOptions listenerOptions, ILogStore logStore)
        : this(listenerOptions, new TcpSyslogListenerOptions(), logStore, new NoopIngressGate())
    {
    }

    public IngestionPipeline(
        UdpSyslogListenerOptions udpListenerOptions,
        TcpSyslogListenerOptions tcpListenerOptions,
        ILogStore logStore)
        : this(udpListenerOptions, tcpListenerOptions, logStore, new NoopIngressGate())
    {
    }

    /// <param name="udpListenerOptions">UDP 受信段の構成。</param>
    /// <param name="tcpListenerOptions">TCP 受信段の構成。</param>
    /// <param name="logStore">永続化先。</param>
    /// <param name="ingressGate">流量制御の挿入点（v0.1 は <see cref="NoopIngressGate"/> のみ）。</param>
    /// <param name="loggerFactory">
    /// 各段のロガーの生成元。<c>null</c> の場合はログを出力しない（テスト等でロガーなしのまま使える）。
    /// </param>
    /// <param name="spool">
    /// ディスクスプール（architecture.md §3.2）。<c>null</c> は「スプール領域を開けなかった」
    /// ためのスプールなし縮退運転（§1.2）を表す——呼び出し側（ホスト）が
    /// <see cref="DiskSpool.TryOpen"/> の結果をそのまま渡す想定。
    /// <b>所有権は呼び出し側に残る</b>——本クラスは借用するだけで <see cref="IDisposable.Dispose"/>
    /// を呼ばない（<see cref="DisposeAsync"/> の実装参照）。開いた側（ホスト）が
    /// プロセス終了時に解放する。
    /// </param>
    /// <param name="capacityExhaustionHandler">
    /// 容量枯渇（database.md §1.2 契約 3）を契機とした保持期間削除の前倒し実行の挿入点
    /// （M5-1。<see cref="ICapacityExhaustionHandler"/> 参照）。<c>null</c> は「自走復旧を
    /// 行わない」構成（保持期間スケジューラ未構成時）を表す。
    /// </param>
    /// <param name="defaultRfc3164TimeZone">
    /// RFC 3164 TIMESTAMP の既定タイムゾーン（Issue #134。<see cref="ParsingStage"/> 経由で
    /// <see cref="Parsing.SyslogParser.Parse"/> へそのまま渡す）。<c>null</c> は UTC（現状互換）。
    /// </param>
    /// <param name="writeGate">
    /// ILogStore の書き込みゲート（Issue #151。<see cref="LogStoreWriteGate"/> 参照）。
    /// ライブ書き込み（<see cref="PersistenceWriter"/>）と drain（<see cref="SpoolDrainCoordinator"/>）
    /// へ同じインスタンスを渡し、保持期間削除（<c>Yagura.Host.Retention.RetentionScheduler</c>）と
    /// 直列化する。<c>null</c> はゲートなし（排他なし。テスト等で並行を意識しない構成向け——
    /// 本番結線（<c>Yagura.Host.Program</c>）は常に非 <c>null</c> を渡す）。
    /// </param>
    /// <param name="selfTestTracker">
    /// 定期自己検証（architecture.md §3.2.5。Issue #152）の照合状態。<see cref="SpoolDrainCoordinator"/>
    /// へそのまま渡し、drain が自己検証の合成レコードを破棄するたびに通知させる。投入側
    /// （<c>Yagura.Host.Observability.ActiveNotification.ActiveNotificationMonitor</c>）と
    /// 同一インスタンスを呼び出し側（ホスト）が共有する想定。<c>null</c>（既定）は「自己検証を
    /// 行わない」構成（<paramref name="spool"/> が <c>null</c> の縮退運転時など）を表す。
    /// </param>
    /// <param name="tlsListenerOptions">
    /// TLS 受信（RFC 5425。opt-in。Issue #137）の構成。<c>null</c>（既定）は「TLS 受信を構成しない」
    /// ——設定で無効化されている、または起動時に証明書を解決できず縮小継続した構成を表す
    /// （呼び出し側のホストが判断する。security.md §6）。非 <c>null</c> の場合は
    /// <paramref name="tlsCertificateSelector"/> も必ず指定すること。
    /// </param>
    /// <param name="tlsCertificateSelector">
    /// TLS 受信のハンドシェイクで提示する証明書を返す関数（<see cref="TlsSyslogListener"/> の
    /// remarks 参照——期限切れでも呼び出し元の判断でそのまま返し続けてよい。「止めない」は本クラスの
    /// 責務ではなくホスト側の証明書解決の責務）。<paramref name="tlsListenerOptions"/> が非 <c>null</c>
    /// のときのみ使用する。
    /// </param>
    public IngestionPipeline(
        UdpSyslogListenerOptions udpListenerOptions,
        TcpSyslogListenerOptions tcpListenerOptions,
        ILogStore logStore,
        IIngressGate ingressGate,
        ILoggerFactory? loggerFactory = null,
        DiskSpool? spool = null,
        ICapacityExhaustionHandler? capacityExhaustionHandler = null,
        TimeZoneInfo? defaultRfc3164TimeZone = null,
        LogStoreWriteGate? writeGate = null,
        SpoolSelfTestTracker? selfTestTracker = null,
        TlsSyslogListenerOptions? tlsListenerOptions = null,
        Func<X509Certificate2?>? tlsCertificateSelector = null)
    {
        ArgumentNullException.ThrowIfNull(udpListenerOptions);
        ArgumentNullException.ThrowIfNull(tcpListenerOptions);
        ArgumentNullException.ThrowIfNull(logStore);
        ArgumentNullException.ThrowIfNull(ingressGate);

        if (tlsListenerOptions is not null && tlsCertificateSelector is null)
        {
            throw new ArgumentException(
                $"{nameof(tlsListenerOptions)} を指定する場合は {nameof(tlsCertificateSelector)} も" +
                "必ず指定すること（TLS 受信は証明書なしにハンドシェイクを開始できない）。",
                nameof(tlsCertificateSelector));
        }

        _metrics = new IngestionMetrics();
        _logger = loggerFactory?.CreateLogger<IngestionPipeline>();
        _loggerFactory = loggerFactory;
        _ingressGate = ingressGate;
        _udpOptions = udpListenerOptions;
        _tcpOptions = tcpListenerOptions;

        // Q1・Q2 の容量は実測確定待ちの暫定値（PipelineConstants 参照。M-1）。
        // FullMode は既定の Wait のままにする: UDP 受信段（UdpSyslogListener）は TryWrite のみを
        // 呼び出し、満杯時は false が返る（ブロックしない）ため、これを破棄として計上する
        // （architecture.md §3.1「Q1 溢れ（UDP）を破棄とする理由」）。TCP 受信段
        // （TcpSyslogListener）は WriteAsync を await するため、Wait の本来の意味（バック
        // プレッシャ）がそのまま TCP の「読み取り停止」を実現する（§3.1 の TCP 行）。
        // DropWrite 等の自動破棄モードは TryWrite が常に true を返し破棄の検知点を失う、
        // かつ WriteAsync が即座に完了してしまい TCP の停止も実現できないため使わない。
        _q1 = Channel.CreateBounded<RawDatagram>(new BoundedChannelOptions(PipelineConstants.Q1Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        _q2 = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(PipelineConstants.Q2Capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        _udpListener = new UdpSyslogListener(
            udpListenerOptions,
            _q1.Writer,
            ingressGate,
            _metrics,
            loggerFactory?.CreateLogger<UdpSyslogListener>());
        _tcpListener = new TcpSyslogListener(
            tcpListenerOptions,
            _q1.Writer,
            ingressGate,
            _metrics,
            loggerFactory?.CreateLogger<TcpSyslogListener>());

        // TLS 受信（Issue #137）は opt-in——tlsListenerOptions が null の間は構成しない
        // （§4.1「受信は最初に開く」の対象から外れる。ホスト側が有効/無効を判断する）。
        _tlsListener = tlsListenerOptions is null
            ? null
            : new TlsSyslogListener(
                tlsListenerOptions,
                _q1.Writer,
                ingressGate,
                _metrics,
                tlsCertificateSelector!,
                loggerFactory?.CreateLogger<TlsSyslogListener>());

        _parsingStage = new ParsingStage(
            _q1.Reader,
            _q2.Writer,
            spool,
            _metrics,
            defaultRfc3164TimeZone,
            loggerFactory?.CreateLogger<ParsingStage>());
        _persistenceWriter = new PersistenceWriter(
            _q2.Reader,
            logStore,
            spool,
            _metrics,
            loggerFactory?.CreateLogger<PersistenceWriter>(),
            capacityExhaustionHandler,
            timeProvider: null,
            writeGate: writeGate);

        // スプールがある場合のみ drain コーディネータを組み立てる（縮退運転時は drain 対象が無い）。
        _drainCoordinator = spool is null
            ? null
            : new SpoolDrainCoordinator(
                spool,
                _q2.Reader,
                logStore,
                _metrics,
                loggerFactory?.CreateLogger<SpoolDrainCoordinator>(),
                capacityExhaustionHandler,
                writeGate,
                selfTestTracker);
    }

    /// <summary>
    /// 実際に束縛された UDP ポート（<see cref="StartListenerAsync"/> 後に有効）。
    /// </summary>
    public int BoundPort => _udpListener.BoundPort;

    /// <summary>
    /// 実際に束縛された TCP ポート（<see cref="StartListenerAsync"/> 後に有効）。
    /// </summary>
    public int TcpBoundPort => _tcpListener.BoundPort;

    /// <summary>
    /// 実際に束縛された TLS 受信ポート（TLS 受信が構成されている場合のみ。<see cref="StartListenerAsync"/>
    /// 後に有効。Issue #137）。TLS 受信が構成されていない場合は <c>null</c>。
    /// </summary>
    public int? TlsBoundPort => _tlsListener?.BoundPort;

    /// <summary>
    /// 計測点。テストからの検証・DI 登録に使う。
    /// </summary>
    public IngestionMetrics Metrics => _metrics;

    /// <summary>
    /// 受信段を開始する（architecture.md §1.2 手順 2「受信ソケットを開き、受信を開始する」）。
    /// DB 初期化の完了を待たずに呼び出してよい——Q1・Q2 が緩衝になる。UDP・TCP は同時に開始する
    /// （依頼「起動順序: UDP と同時（受信先行の一部）」）。
    /// </summary>
    /// <remarks>
    /// <b>原子的起動</b>（Issue #141）: 片方の bind に成功した後にもう片方が失敗すると、
    /// 何もしなければ成功済みのリスナ（ソケット + 受信ループ）が起動したまま取り残される。
    /// 本メソッドは起動済みのリスナを記録しておき、失敗時にそれらをすべて停止してから
    /// 例外を再送出する——「両方成功」か「（ログを残した上で）全停止」のいずれかにする。
    /// </remarks>
    public async Task StartListenerAsync(CancellationToken cancellationToken = default)
    {
        var startedListeners = new List<Func<Task>>(3);
        try
        {
            await _udpListener.StartAsync(cancellationToken).ConfigureAwait(false);
            startedListeners.Add(() => _udpListener.StopAsync());

            await _tcpListener.StartAsync(cancellationToken).ConfigureAwait(false);
            startedListeners.Add(() => _tcpListener.StopAsync());

            if (_tlsListener is not null)
            {
                // TLS 受信（Issue #137）も同じ原子的起動の対象に含める——TLS の bind 失敗で
                // UDP/TCP まで巻き添えでロールバックされる（「両方成功」か「全停止」のいずれか
                // という本メソッドの不変条件を 3 リスナへ拡張する）。
                await _tlsListener.StartAsync(cancellationToken).ConfigureAwait(false);
                startedListeners.Add(() => _tlsListener.StopAsync());
            }
        }
        catch (Exception ex)
        {
            // この失敗は例外の再送出を通じてホスト起動全体の失敗（IHostedService.StartAsync の
            // 中断）につながる致命的事象のため、Error で記録する。
            _logger?.LogError(
                ex,
                "受信リスナの起動に失敗したため、起動済みのリスナ {StartedCount} 件を停止して起動全体を失敗として扱います。",
                startedListeners.Count);

            foreach (var stopStartedListener in startedListeners)
            {
                try
                {
                    await stopStartedListener().ConfigureAwait(false);
                }
                catch (Exception rollbackEx)
                {
                    // ロールバック中の停止失敗で元の起動失敗例外（本来の bind 失敗原因）を
                    // 握り潰さない。記録した上で残りのリスナの停止も試み、最後に必ず元の
                    // 例外を再送出する。
                    _logger?.LogError(
                        rollbackEx,
                        "起動失敗のロールバック中、起動済みリスナの停止に失敗しました。元の起動失敗例外を優先して再送出します。");
                }
            }

            throw;
        }
    }

    /// <summary>
    /// RFC 3164 TIMESTAMP の既定タイムゾーンを実行中に更新する（設定ライブ再読み込み。
    /// CF-4 層1。Issue #262。<see cref="ParsingStage.UpdateDefaultRfc3164TimeZone"/> への
    /// パススルー——解析段はパイプラインの内部部品のため、ホストにはこの口だけを見せる）。
    /// </summary>
    public void UpdateDefaultRfc3164TimeZone(TimeZoneInfo? timeZone) =>
        _parsingStage.UpdateDefaultRfc3164TimeZone(timeZone);

    /// <summary>
    /// UDP・TCP リスナを新しい構成で再構成する（CF-4 層2。Issue #262。configuration.md §3）。
    /// options に変更のないリスナには一切触れない（差分適用——瞬断なし）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>手順（リスナごと）</b>: ①旧リスナを graceful に停止（TCP は進行中フレームを Q1 へ
    /// 吐き切ってから返る——リスナ側 StopAsync の性質）②新 options でリスナを生成し起動
    /// ③失敗時は<b>旧 options で復旧</b>（configuration.md §3「再構成の失敗時の縮退 = 旧構成の
    /// 維持」。ポートは直前まで自分が使っていたため復旧は通常成功する）④復旧も失敗した場合、
    /// リスナは停止のまま <b>CF-6 の定期再試行</b>（<see cref="BindRetryInterval"/> 間隔で
    /// 新構成の bind を試み続ける）へ移行する。同一ポートの再構成（受信バッファ変更等）は
    /// 旧を閉じてから新を開くため短い瞬断を伴う——瞬断区間は戻り値で報告し、呼び出し側
    /// （ホスト）が受信断のシステムイベントとして記録する（記録の責務分離: 本クラスは
    /// ILogStore・書き込みゲートを直接持たない）。
    /// </para>
    /// <para>
    /// <b>継続するもの</b>: Q1・解析段・永続化段・スプール・計器・流量制御ゲートはすべて
    /// 共有のまま継続する（受信段のインスタンスだけが入れ替わる）。Q1 に滞留中のデータグラムは
    /// 再構成中も消費され続ける。
    /// </para>
    /// <para>
    /// <b>並行性</b>: 再構成は直列化される（<see cref="_reconfigureGate"/>）。進行中の CF-6
    /// 再試行は新しい再構成の開始で打ち切られる（望ましい構成が変わったため）。
    /// </para>
    /// </remarks>
    public async Task<ListenerReconfigurationResult> ReconfigureListenersAsync(
        UdpSyslogListenerOptions newUdpOptions,
        TcpSyslogListenerOptions newTcpOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newUdpOptions);
        ArgumentNullException.ThrowIfNull(newTcpOptions);

        await _reconfigureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 進行中の CF-6 再試行があれば打ち切る（望ましい構成が変わった）。
            await CancelBindRetryAsync().ConfigureAwait(false);

            var udpOutcome = UdpOptionsEqual(_udpOptions, newUdpOptions)
                ? new ListenerReconfigurationOutcome(ListenerReconfigurationStatus.NotChanged)
                : await ReconfigureUdpAsync(newUdpOptions, cancellationToken).ConfigureAwait(false);

            var tcpOutcome = TcpOptionsEqual(_tcpOptions, newTcpOptions)
                ? new ListenerReconfigurationOutcome(ListenerReconfigurationStatus.NotChanged)
                : await ReconfigureTcpAsync(newTcpOptions, cancellationToken).ConfigureAwait(false);

            return new ListenerReconfigurationResult(udpOutcome, tcpOutcome);
        }
        finally
        {
            _reconfigureGate.Release();
        }
    }

    private static bool UdpOptionsEqual(UdpSyslogListenerOptions a, UdpSyslogListenerOptions b) =>
        string.Equals(a.BindAddress, b.BindAddress, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port
        && a.ReceiveBufferBytes == b.ReceiveBufferBytes
        && a.BindAddressIsExplicit == b.BindAddressIsExplicit;

    private static bool TcpOptionsEqual(TcpSyslogListenerOptions a, TcpSyslogListenerOptions b) =>
        string.Equals(a.BindAddress, b.BindAddress, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port
        && a.BindAddressIsExplicit == b.BindAddressIsExplicit;

    private async Task<ListenerReconfigurationOutcome> ReconfigureUdpAsync(
        UdpSyslogListenerOptions newOptions, CancellationToken cancellationToken)
    {
        var oldOptions = _udpOptions;
        var gapStartedAt = DateTimeOffset.UtcNow;
        await _udpListener.StopAsync().ConfigureAwait(false);

        var newListener = CreateUdpListener(newOptions);
        try
        {
            await newListener.StartAsync(cancellationToken).ConfigureAwait(false);
            _udpListener = newListener;
            _udpOptions = newOptions;
            _logger?.LogInformation(
                "UDP リスナを再構成しました（{Address}:{Port}）。", newOptions.BindAddress, newListener.BoundPort);
            return new ListenerReconfigurationOutcome(
                ListenerReconfigurationStatus.Reconfigured, gapStartedAt, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return await RollBackOrRetryAsync(
                "UDP",
                ex,
                gapStartedAt,
                restartOld: async ct =>
                {
                    var rollback = CreateUdpListener(oldOptions);
                    await rollback.StartAsync(ct).ConfigureAwait(false);
                    _udpListener = rollback;
                },
                retryNew: async ct =>
                {
                    var retried = CreateUdpListener(newOptions);
                    await retried.StartAsync(ct).ConfigureAwait(false);
                    _udpListener = retried;
                    _udpOptions = newOptions;
                }).ConfigureAwait(false);
        }
    }

    private async Task<ListenerReconfigurationOutcome> ReconfigureTcpAsync(
        TcpSyslogListenerOptions newOptions, CancellationToken cancellationToken)
    {
        var oldOptions = _tcpOptions;
        var gapStartedAt = DateTimeOffset.UtcNow;
        await _tcpListener.StopAsync().ConfigureAwait(false);

        var newListener = CreateTcpListener(newOptions);
        try
        {
            await newListener.StartAsync(cancellationToken).ConfigureAwait(false);
            _tcpListener = newListener;
            _tcpOptions = newOptions;
            _logger?.LogInformation(
                "TCP リスナを再構成しました（{Address}:{Port}）。", newOptions.BindAddress, newListener.BoundPort);
            return new ListenerReconfigurationOutcome(
                ListenerReconfigurationStatus.Reconfigured, gapStartedAt, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return await RollBackOrRetryAsync(
                "TCP",
                ex,
                gapStartedAt,
                restartOld: async ct =>
                {
                    var rollback = CreateTcpListener(oldOptions);
                    await rollback.StartAsync(ct).ConfigureAwait(false);
                    _tcpListener = rollback;
                },
                retryNew: async ct =>
                {
                    var retried = CreateTcpListener(newOptions);
                    await retried.StartAsync(ct).ConfigureAwait(false);
                    _tcpListener = retried;
                    _tcpOptions = newOptions;
                }).ConfigureAwait(false);
        }
    }

    private UdpSyslogListener CreateUdpListener(UdpSyslogListenerOptions options) => new(
        options, _q1.Writer, _ingressGate, _metrics, _loggerFactory?.CreateLogger<UdpSyslogListener>());

    private TcpSyslogListener CreateTcpListener(TcpSyslogListenerOptions options) => new(
        options, _q1.Writer, _ingressGate, _metrics, _loggerFactory?.CreateLogger<TcpSyslogListener>());

    /// <summary>
    /// 新構成の bind 失敗後の共通処理: 旧構成での復旧を試み、それも失敗したら CF-6 の
    /// 定期再試行（新構成を望ましい状態として試み続ける）へ移行する。
    /// </summary>
    private async Task<ListenerReconfigurationOutcome> RollBackOrRetryAsync(
        string protocolLabel,
        Exception newBindFailure,
        DateTimeOffset gapStartedAt,
        Func<CancellationToken, Task> restartOld,
        Func<CancellationToken, Task> retryNew)
    {
        _logger?.LogWarning(
            newBindFailure,
            "{Protocol} リスナの新構成での bind に失敗したため、旧構成での復旧を試みます（旧構成の維持——configuration.md §3）。",
            protocolLabel);

        try
        {
            await restartOld(CancellationToken.None).ConfigureAwait(false);
            return new ListenerReconfigurationOutcome(
                ListenerReconfigurationStatus.RolledBack, gapStartedAt, DateTimeOffset.UtcNow, newBindFailure.Message);
        }
        catch (Exception rollbackEx)
        {
            _logger?.LogError(
                rollbackEx,
                "{Protocol} リスナの旧構成での復旧にも失敗しました。リスナは停止中です。新構成の bind を {Interval} 間隔で再試行します（CF-6）。",
                protocolLabel,
                BindRetryInterval);

            StartBindRetryLoop(protocolLabel, retryNew);
            return new ListenerReconfigurationOutcome(
                ListenerReconfigurationStatus.DownRetrying, gapStartedAt, GapEndedAt: null, newBindFailure.Message);
        }
    }

    /// <summary>
    /// CF-6: bind 失敗後の定期再試行ループ（間隔は <see cref="BindRetryInterval"/> の仮値 30 秒。
    /// configuration.md §4.1「bind 失敗後は定期的に再試行する」の再構成経路への適用）。
    /// 成功またはパイプライン停止・次の再構成で終了する。
    /// </summary>
    private void StartBindRetryLoop(string protocolLabel, Func<CancellationToken, Task> retryNew)
    {
        _bindRetryCts ??= new CancellationTokenSource();
        var token = _bindRetryCts.Token;

        _bindRetryTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(BindRetryInterval, token).ConfigureAwait(false);
                    await retryNew(token).ConfigureAwait(false);
                    _logger?.LogInformation("{Protocol} リスナの bind 再試行に成功し、受信を再開しました（CF-6）。", protocolLabel);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(
                        ex, "{Protocol} リスナの bind 再試行に失敗しました。{Interval} 後に再試行します（CF-6）。",
                        protocolLabel, BindRetryInterval);
                }
            }
        }, CancellationToken.None);
    }

    private async Task CancelBindRetryAsync()
    {
        if (_bindRetryCts is null)
        {
            return;
        }

        await _bindRetryCts.CancelAsync().ConfigureAwait(false);
        if (_bindRetryTask is not null)
        {
            try
            {
                await _bindRetryTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _bindRetryCts.Dispose();
        _bindRetryCts = null;
        _bindRetryTask = null;
    }

    /// <summary>
    /// 解析段・永続化段の消費ループと、スプールの drain ループを開始する。
    /// DB 初期化（<see cref="ILogStore.InitializeAsync"/>）の完了後に呼び出す
    /// （architecture.md §1.2 手順 3・4「DB provider を初期化する…drain 開始」）。
    /// </summary>
    public void StartConsumers()
    {
        if (_consumerStoppingCts is not null)
        {
            throw new InvalidOperationException("消費ループは既に開始されている。");
        }

        _consumerStoppingCts = new CancellationTokenSource();
        _parsingTask = Task.Run(() => _parsingStage.RunAsync(_consumerStoppingCts.Token));
        _persistenceTask = Task.Run(() => _persistenceWriter.RunAsync(_consumerStoppingCts.Token));

        if (_drainCoordinator is not null)
        {
            _drainTask = Task.Run(() => _drainCoordinator.RunAsync(_consumerStoppingCts.Token));
        }
    }

    /// <summary>
    /// パイプラインを停止する（architecture.md §1.3）。
    /// 手順: ①受信ソケットを閉じる（TCP は接続クローズまで含む） ②消費ループを停止し、
    /// Q1/Q2 に残る分を DB へ書き切るのを待たずスプールへ退避する。
    /// </summary>
    /// <remarks>
    /// <see cref="StopListenersAsync"/>（手順 1）と <see cref="DrainConsumersAsync"/>（手順 2）
    /// をこの順で呼ぶだけの結合メソッド。ホスト側（<see cref="Yagura.Host.IngestionHostedService"/>）
    /// は M4-4 でメタデータ領域への書き込み（手順 1 直後のカウンタ書き込み・手順 2 完了後の
    /// 最終値書き込みと正常停止イベント記録）を手順の間に挟む必要があるため、2 メソッドを
    /// 個別に呼び出す。本メソッドは「間に何も挟まない」呼び出し元（既存テスト等）向けに残す。
    /// </remarks>
    public async Task StopAsync()
    {
        await StopListenersAsync().ConfigureAwait(false);
        await DrainConsumersAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 停止手順 1（architecture.md §1.3）: 受信ソケットを閉じる（TCP は接続クローズまで含む）。
    /// 以降の到着分はロスになる。UDP・TCP を並行して停止する（依頼「停止順序: リスナ停止 →
    /// 接続クローズ → drain」——TCP 側の接続クローズは <see cref="TcpSyslogListener.StopAsync"/>
    /// 内で行われる）。
    /// </summary>
    public async Task StopListenersAsync()
    {
        // CF-6 の再試行が走っていれば止める（停止後にリスナが勝手に復活しないように）。
        await CancelBindRetryAsync().ConfigureAwait(false);

        var stopTasks = new List<Task>(3) { _udpListener.StopAsync(), _tcpListener.StopAsync() };
        if (_tlsListener is not null)
        {
            stopTasks.Add(_tlsListener.StopAsync());
        }

        await Task.WhenAll(stopTasks).ConfigureAwait(false);
    }

    /// <summary>
    /// 停止手順 2（architecture.md §1.3）: 消費ループを停止する。<see cref="ParsingStage.RunAsync"/> /
    /// <see cref="PersistenceWriter.RunAsync"/> は停止要求を検知した時点で、DB を待たず Q1/Q2 の
    /// 残りをスプールへ退避する。drain コーディネータも同じトークンで停止する（drain 中の
    /// セグメントは未消化のまま残り、次回起動時に再開される）。<see cref="StopListenersAsync"/>
    /// の後に呼ぶ想定（受信を止めてから drain する。§1.3 の順序）。
    /// </summary>
    public async Task DrainConsumersAsync()
    {
        if (_consumerStoppingCts is null)
        {
            return;
        }

        _consumerStoppingCts.Cancel();

        var consumerTasks = new List<Task>(3);
        if (_parsingTask is not null)
        {
            consumerTasks.Add(_parsingTask);
        }

        if (_persistenceTask is not null)
        {
            consumerTasks.Add(_persistenceTask);
        }

        if (_drainTask is not null)
        {
            consumerTasks.Add(_drainTask);
        }

        if (consumerTasks.Count > 0)
        {
            await Task.WhenAll(consumerTasks).ConfigureAwait(false);
        }

        _consumerStoppingCts.Dispose();
        _consumerStoppingCts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _udpListener.DisposeAsync().ConfigureAwait(false);
        await _tcpListener.DisposeAsync().ConfigureAwait(false);
        if (_tlsListener is not null)
        {
            await _tlsListener.DisposeAsync().ConfigureAwait(false);
        }

        _metrics.Dispose();
    }
}
