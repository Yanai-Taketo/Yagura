using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Udp;
using Yagura.Storage;

namespace Yagura.Ingestion.Tcp;

/// <summary>
/// TCP の受信段（architecture.md §2.1・§3.1・§4.5。M4-1）。
/// </summary>
/// <remarks>
/// <para>
/// UDP（<see cref="Yagura.Ingestion.Udp.UdpSyslogListener"/>）と構造を揃える: 読み取りは
/// 解析・書き込みを行わず、境界が確定したメッセージを <see cref="RawDatagram"/> へ封筒化して
/// Q1 へ投入するだけである。
/// </para>
/// <para>
/// <b>Q1 満杯時の挙動が UDP と異なる</b>（architecture.md §3.1）: UDP は破棄するが、TCP は
/// **読み取りを停止する**。本実装は Q1 への投入を <c>ChannelWriter.WriteAsync</c>（await）で
/// 行うことでこれを実現する——Q1 が満杯なら WriteAsync が完了せず、結果として当該接続の
/// ソケット読み取りが自然に停滞し、OS の TCP フロー制御（受信ウィンドウの縮小）を通じて
/// 送信側へ伝搬する。停止中であることの可視化（ゲージ）は M4-4 で追加する。
/// </para>
/// <para>
/// <b>同時接続数上限</b>（§3.1・M-14）: 上限到達時は Accept 直後に接続を閉じ（新規拒否）、
/// <see cref="IngestionMetrics.RecordTcpConnectionRejected"/> で計上する。既存接続の読み取り
/// 停止中に接続数が無限に積み上がることを防ぐ有限化である。
/// </para>
/// <para>
/// <b>切断時の不完全メッセージ</b>（database.md §2.1）: 接続が切断された時点で
/// <see cref="TcpFrameDecoder"/> に読みかけのデータが残っていれば、それを
/// <see cref="RawDatagram.Incomplete"/> = <c>true</c> として Q1 へ流す（捨てない）。
/// </para>
/// </remarks>
public sealed class TcpSyslogListener : IAsyncDisposable
{
    private readonly TcpSyslogListenerOptions _options;
    private readonly ChannelWriter<RawDatagram> _q1Writer;
    private readonly IIngressGate _ingressGate;
    private readonly IngestionMetrics _metrics;

    private TcpListener? _tcpListener;
    private Task? _acceptLoopTask;
    private CancellationTokenSource? _stoppingCts;

    // 現在接続中のソケットの読み取りループタスク一式。StopAsync でまとめて待つ。
    private readonly ConcurrentDictionary<Task, byte> _connectionTasks = new();

    // 同時接続数の有限化（§3.1）。Accept ごとにインクリメントし、接続終了時にデクリメントする。
    private int _currentConnectionCount;

    public TcpSyslogListener(
        TcpSyslogListenerOptions options,
        ChannelWriter<RawDatagram> q1Writer,
        IIngressGate ingressGate,
        IngestionMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(q1Writer);
        ArgumentNullException.ThrowIfNull(ingressGate);
        ArgumentNullException.ThrowIfNull(metrics);

        _options = options;
        _q1Writer = q1Writer;
        _ingressGate = ingressGate;
        _metrics = metrics;
    }

    /// <summary>
    /// 実際に束縛された TCP ポート。<see cref="TcpSyslogListenerOptions.Port"/> に 0
    /// を指定した場合（テスト用の OS 採番）に、開始後の実ポートを取得するために使う。
    /// </summary>
    public int BoundPort { get; private set; }

    /// <summary>
    /// 現在の接続数（テスト・観測用）。
    /// </summary>
    public int CurrentConnectionCount => Volatile.Read(ref _currentConnectionCount);

    /// <summary>
    /// ソケットを bind し、Accept ループを開始する（architecture.md §1.2「受信を最初に開く」）。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_tcpListener is not null)
        {
            throw new InvalidOperationException("TcpSyslogListener は既に開始されている。");
        }

        var endpoint = new IPEndPoint(IPAddress.Parse(_options.BindAddress), _options.Port);
        _tcpListener = new TcpListener(endpoint);
        _tcpListener.Start();
        BoundPort = ((IPEndPoint)_tcpListener.LocalEndpoint).Port;

        _stoppingCts = new CancellationTokenSource();
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_stoppingCts.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Accept ループ・全接続の読み取りループを止め、ソケットを閉じる（architecture.md §1.3 手順 1）。
    /// </summary>
    public async Task StopAsync()
    {
        if (_tcpListener is null)
        {
            return;
        }

        // UdpSyslogListener と同じ順序（キャンセル → ループ終了待ち → 破棄）を踏む。
        _stoppingCts?.Cancel();

        if (_acceptLoopTask is not null)
        {
            await _acceptLoopTask.ConfigureAwait(false);
        }

        _tcpListener.Stop();

        // 停止順序（依頼「リスナ停止 → 接続クローズ → drain」）: リスナは上で既に停止済み。
        // 個々の接続の読み取りループは stoppingToken のキャンセルを受けて自ら終了し、
        // 切断時に Incomplete マークを付けて Q1 へ流してから完了する（下記 HandleConnectionAsync）。
        var pending = _connectionTasks.Keys.ToArray();
        if (pending.Length > 0)
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }

        _stoppingCts?.Dispose();
        _stoppingCts = null;
        _tcpListener = null;
    }

    private async Task AcceptLoopAsync(CancellationToken stoppingToken)
    {
        var listener = _tcpListener!;

        while (!stoppingToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                // 個々の Accept 失敗でループを止めない（UDP の個別データグラムエラーと同じ方針）。
                continue;
            }

            // 同時接続数上限（architecture.md §3.1・M-14）: 上限到達時は Accept 後即クローズし、
            // 新規接続を拒否する（既存接続を守る）。
            if (Interlocked.Increment(ref _currentConnectionCount) > _options.MaxConcurrentConnections)
            {
                Interlocked.Decrement(ref _currentConnectionCount);
                _metrics.RecordTcpConnectionRejected();
                client.Close();
                client.Dispose();
                continue;
            }

            var connectionTask = Task.Run(() => HandleConnectionAsync(client, stoppingToken), CancellationToken.None);
            _connectionTasks[connectionTask] = 0;

            // 接続終了時に一覧から取り除く（無限に積み上がらないようにする）。継続はバックグラウンドで行い、
            // Accept ループ自体はブロックしない。
            _ = connectionTask.ContinueWith(
                t => _connectionTasks.TryRemove(t, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken stoppingToken)
    {
        var remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
        var sourceAddress = remoteEndPoint?.Address.ToString() ?? "unknown";
        var sourcePort = remoteEndPoint?.Port ?? 0;

        var decoder = new TcpFrameDecoder(new TcpFrameDecoderOptions { MaxMessageLength = _options.MaxMessageLength });
        var buffer = new byte[8192];

        try
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    int bytesRead;
                    try
                    {
                        bytesRead = await stream.ReadAsync(buffer, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (IOException)
                    {
                        // 相手の異常切断（RST 等）。接続断として扱い、読みかけデータを Incomplete で流す。
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    if (bytesRead == 0)
                    {
                        // 相手が正常にシャットダウン（FIN）した。接続断。
                        break;
                    }

                    var receivedAt = DateTimeOffset.UtcNow;

                    IReadOnlyList<byte[]> messages;
                    try
                    {
                        messages = decoder.Push(buffer.AsSpan(0, bytesRead));
                    }
                    catch (TcpFrameSizeExceededException)
                    {
                        // M4-1 依頼の安全側判断: 1 メッセージのサイズ上限（巨大な MSG-LEN を騙る、
                        // または LF が来ないまま流し込み続ける送信元）を超えたら、当該接続を
                        // 切断する。切断前に読みかけデータが残っていれば通常の切断経路と同じく
                        // Incomplete として Q1 へ流す（下のfinally相当の処理へ委ねるため、ここでは
                        // ループを抜けるだけでよい——decoder の内部バッファはそのまま残るため、
                        // ループ終了後の Flush() が拾う）。
                        break;
                    }

                    foreach (var message in messages)
                    {
                        if (!_ingressGate.ShouldAdmit(remoteEndPoint?.Address ?? IPAddress.None, message))
                        {
                            continue;
                        }

                        var datagram = new RawDatagram(
                            ReceivedAt: receivedAt,
                            SourceAddress: sourceAddress,
                            SourcePort: sourcePort,
                            Protocol: Protocol.Tcp,
                            Payload: message);

                        // architecture.md §3.1: TCP は Q1 満杯時に破棄せず読み取りを停止する。
                        // WriteAsync を await することで、Q1 が満杯の間はこの接続の読み取り
                        // ループ自体が進まなくなり、OS の TCP フロー制御が送信側へ伝搬する。
                        await _q1Writer.WriteAsync(datagram, CancellationToken.None).ConfigureAwait(false);
                    }
                }

                // 切断時（正常シャットダウン・異常切断・停止要求・サイズ超過のいずれか）に
                // 読みかけの不完全データが残っていれば、Incomplete として Q1 へ流す
                // （database.md §2.1「不完全は解析失敗に優先」——捨てない）。
                var incomplete = decoder.Flush();
                if (incomplete is not null)
                {
                    var datagram = new RawDatagram(
                        ReceivedAt: DateTimeOffset.UtcNow,
                        SourceAddress: sourceAddress,
                        SourcePort: sourcePort,
                        Protocol: Protocol.Tcp,
                        Payload: incomplete,
                        Incomplete: true);

                    // 停止経路でも drain のベストエフォートを揃えるため CancellationToken.None を使う
                    // （UDP 側 ParsingStage の drain 方針と同じ考え方）。
                    await _q1Writer.WriteAsync(datagram, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _currentConnectionCount);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
