using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Parsing;
using Yagura.Ingestion.Persistence;
using Yagura.Ingestion.Udp;
using Yagura.Storage;

namespace Yagura.Ingestion;

/// <summary>
/// UDP 受信 → 解析 → 永続化の 3 段パイプライン全体の結線（architecture.md §2.1）。
/// </summary>
/// <remarks>
/// ホスト（Yagura.Host）は本クラスを介してパイプラインを起動・停止する。
/// 起動順序（§1.2「受信を最初に開く」）・停止順序（§1.3）の制御はホスト側が担う——
/// <see cref="StartListenerAsync"/> と <see cref="StartConsumers"/> を分けているのは、
/// ホストが「受信開始 → DB 初期化 → 消費ループ開始」の順を組み立てられるようにするため。
/// </remarks>
public sealed class IngestionPipeline : IAsyncDisposable
{
    private readonly Channel<RawDatagram> _q1;
    private readonly Channel<LogRecord> _q2;
    private readonly UdpSyslogListener _listener;
    private readonly ParsingStage _parsingStage;
    private readonly PersistenceWriter _persistenceWriter;
    private readonly IngestionMetrics _metrics;

    private CancellationTokenSource? _consumerStoppingCts;
    private Task? _parsingTask;
    private Task? _persistenceTask;

    public IngestionPipeline(UdpSyslogListenerOptions listenerOptions, ILogStore logStore)
        : this(listenerOptions, logStore, new NoopIngressGate())
    {
    }

    /// <param name="listenerOptions">受信段の構成。</param>
    /// <param name="logStore">永続化先。</param>
    /// <param name="ingressGate">流量制御の挿入点（v0.1 は <see cref="NoopIngressGate"/> のみ）。</param>
    /// <param name="loggerFactory">
    /// 各段のロガーの生成元。<c>null</c> の場合はログを出力しない（テスト等でロガーなしのまま使える）。
    /// </param>
    public IngestionPipeline(
        UdpSyslogListenerOptions listenerOptions,
        ILogStore logStore,
        IIngressGate ingressGate,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(listenerOptions);
        ArgumentNullException.ThrowIfNull(logStore);
        ArgumentNullException.ThrowIfNull(ingressGate);

        _metrics = new IngestionMetrics();

        // Q1・Q2 の容量は実測確定待ちの暫定値（PipelineConstants 参照。M-1）。
        // FullMode は既定の Wait のままにする: 受信段（UdpSyslogListener）は TryWrite のみを
        // 呼び出し、満杯時は false が返る（ブロックしない）ため、これを破棄として計上する
        // （architecture.md §3.1「Q1 溢れ（UDP）を破棄とする理由」）。DropWrite 等の自動破棄
        // モードは TryWrite が常に true を返し破棄の検知点を失うため使わない。
        _q1 = Channel.CreateBounded<RawDatagram>(new BoundedChannelOptions(PipelineConstants.Q1Capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        _q2 = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(PipelineConstants.Q2Capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        _listener = new UdpSyslogListener(listenerOptions, _q1.Writer, ingressGate, _metrics);
        _parsingStage = new ParsingStage(_q1.Reader, _q2.Writer);
        _persistenceWriter = new PersistenceWriter(
            _q2.Reader,
            logStore,
            loggerFactory?.CreateLogger<PersistenceWriter>());
    }

    /// <summary>
    /// 実際に束縛された UDP ポート（<see cref="StartListenerAsync"/> 後に有効）。
    /// </summary>
    public int BoundPort => _listener.BoundPort;

    /// <summary>
    /// 計測点。テストからの検証・DI 登録に使う。
    /// </summary>
    public IngestionMetrics Metrics => _metrics;

    /// <summary>
    /// 受信段を開始する（architecture.md §1.2 手順 2「受信ソケットを開き、受信を開始する」）。
    /// DB 初期化の完了を待たずに呼び出してよい——Q1・Q2 が緩衝になる。
    /// </summary>
    public Task StartListenerAsync(CancellationToken cancellationToken = default) =>
        _listener.StartAsync(cancellationToken);

    /// <summary>
    /// 解析段・永続化段の消費ループを開始する。DB 初期化（<see cref="ILogStore.InitializeAsync"/>）
    /// の完了後に呼び出す（architecture.md §1.2 手順 3）。
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
    }

    /// <summary>
    /// パイプラインを停止する（architecture.md §1.3）。
    /// 手順: ①受信ソケットを閉じる ②Q1/Q2 を drain して書き切れる分は書く（ベストエフォート）。
    /// </summary>
    /// <remarks>
    /// 完全な停止順序保証（メタデータ領域へのカウンタ書き込み等）は M4 で扱う。
    /// 本実装は「受信停止 → drain」のベストエフォートのみを提供する。
    /// </remarks>
    public async Task StopAsync()
    {
        // 手順 1: 受信ソケットを閉じる。以降の到着分はロスになる（§1.3 手順 1 相当）。
        await _listener.StopAsync().ConfigureAwait(false);

        if (_consumerStoppingCts is null)
        {
            return;
        }

        // 手順 2 相当: Q1/Q2 を drain して書き切れる分は書く（ベストエフォート）。
        // 完全な停止順序（§1.3 の耐障害保証）は M4 まで持ち越す。
        _consumerStoppingCts.Cancel();

        var consumerTasks = new List<Task>(2);
        if (_parsingTask is not null)
        {
            consumerTasks.Add(_parsingTask);
        }

        if (_persistenceTask is not null)
        {
            consumerTasks.Add(_persistenceTask);
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
        await _listener.DisposeAsync().ConfigureAwait(false);
        _metrics.Dispose();
    }
}
