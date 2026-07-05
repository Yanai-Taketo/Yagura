using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Parsing;
using Yagura.Ingestion.Persistence;
using Yagura.Ingestion.Tcp;
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
    private readonly UdpSyslogListener _udpListener;
    private readonly TcpSyslogListener _tcpListener;
    private readonly ParsingStage _parsingStage;
    private readonly PersistenceWriter _persistenceWriter;
    private readonly SpoolDrainCoordinator? _drainCoordinator;
    private readonly IngestionMetrics _metrics;

    private CancellationTokenSource? _consumerStoppingCts;
    private Task? _parsingTask;
    private Task? _persistenceTask;
    private Task? _drainTask;

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
    public IngestionPipeline(
        UdpSyslogListenerOptions udpListenerOptions,
        TcpSyslogListenerOptions tcpListenerOptions,
        ILogStore logStore,
        IIngressGate ingressGate,
        ILoggerFactory? loggerFactory = null,
        DiskSpool? spool = null)
    {
        ArgumentNullException.ThrowIfNull(udpListenerOptions);
        ArgumentNullException.ThrowIfNull(tcpListenerOptions);
        ArgumentNullException.ThrowIfNull(logStore);
        ArgumentNullException.ThrowIfNull(ingressGate);

        _metrics = new IngestionMetrics();

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

        _udpListener = new UdpSyslogListener(udpListenerOptions, _q1.Writer, ingressGate, _metrics);
        _tcpListener = new TcpSyslogListener(tcpListenerOptions, _q1.Writer, ingressGate, _metrics);
        _parsingStage = new ParsingStage(
            _q1.Reader,
            _q2.Writer,
            spool,
            _metrics,
            loggerFactory?.CreateLogger<ParsingStage>());
        _persistenceWriter = new PersistenceWriter(
            _q2.Reader,
            logStore,
            spool,
            _metrics,
            loggerFactory?.CreateLogger<PersistenceWriter>());

        // スプールがある場合のみ drain コーディネータを組み立てる（縮退運転時は drain 対象が無い）。
        _drainCoordinator = spool is null
            ? null
            : new SpoolDrainCoordinator(
                spool,
                _q2.Reader,
                logStore,
                _metrics,
                loggerFactory?.CreateLogger<SpoolDrainCoordinator>());
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
    /// 計測点。テストからの検証・DI 登録に使う。
    /// </summary>
    public IngestionMetrics Metrics => _metrics;

    /// <summary>
    /// 受信段を開始する（architecture.md §1.2 手順 2「受信ソケットを開き、受信を開始する」）。
    /// DB 初期化の完了を待たずに呼び出してよい——Q1・Q2 が緩衝になる。UDP・TCP は同時に開始する
    /// （依頼「起動順序: UDP と同時（受信先行の一部）」）。
    /// </summary>
    public async Task StartListenerAsync(CancellationToken cancellationToken = default)
    {
        await _udpListener.StartAsync(cancellationToken).ConfigureAwait(false);
        await _tcpListener.StartAsync(cancellationToken).ConfigureAwait(false);
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
        await Task.WhenAll(_udpListener.StopAsync(), _tcpListener.StopAsync()).ConfigureAwait(false);
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
        _metrics.Dispose();
    }
}
