using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.FlowControl;
using Yagura.Storage;

namespace Yagura.Ingestion.Udp;

/// <summary>
/// UDP の受信段（architecture.md §2.1）。
/// </summary>
/// <remarks>
/// <para>
/// 読み取りループは <b>解析・書き込みを一切行わない</b>。データグラムを受信したら
/// 即座に <see cref="RawDatagram"/> へ封筒化し、流量制御の挿入点（<see cref="IIngressGate"/>）
/// を通してから Q1 へ投入するだけである（§2.1「受信段はソケットからの読み取りに専念する」）。
/// </para>
/// <para>
/// Q1 が満杯のときは <b>ブロックせず破棄</b>する（§3.1「Q1 溢れ（UDP）を破棄とする理由」——
/// ブロックすると読み取りが停滞し、計測困難な OS 側ロスに転化するため）。破棄は
/// <see cref="IngestionMetrics.RecordInternalBufferDropped"/> で計上する。
/// </para>
/// </remarks>
public sealed class UdpSyslogListener : IAsyncDisposable
{
    private readonly UdpSyslogListenerOptions _options;
    private readonly ChannelWriter<RawDatagram> _q1Writer;
    private readonly IIngressGate _ingressGate;
    private readonly IngestionMetrics _metrics;
    private UdpClient? _udpClient;
    private Task? _receiveLoopTask;
    private CancellationTokenSource? _stoppingCts;

    public UdpSyslogListener(
        UdpSyslogListenerOptions options,
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
    /// 実際に束縛された UDP ポート。<see cref="UdpSyslogListenerOptions.Port"/> に 0
    /// を指定した場合（テスト用の OS 採番）に、開始後の実ポートを取得するために使う。
    /// </summary>
    public int BoundPort { get; private set; }

    /// <summary>
    /// ソケットを bind し、読み取りループを開始する（architecture.md §1.2「受信を最初に開く」）。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_udpClient is not null)
        {
            throw new InvalidOperationException("UdpSyslogListener は既に開始されている。");
        }

        var endpoint = new IPEndPoint(IPAddress.Parse(_options.BindAddress), _options.Port);
        _udpClient = new UdpClient(endpoint);
        BoundPort = ((IPEndPoint)_udpClient.Client.LocalEndPoint!).Port;

        _stoppingCts = new CancellationTokenSource();
        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_stoppingCts.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 読み取りループを止め、ソケットを閉じる（architecture.md §1.3 手順 1）。
    /// </summary>
    public async Task StopAsync()
    {
        if (_udpClient is null)
        {
            return;
        }

        // キャンセルトークンを渡した ReceiveAsync はトークンの cancel で
        // OperationCanceledException を送出して抜けるため、まずキャンセルのみ行い、
        // 読み取りループが実際に終了するのを待ってからソケットを閉じる。
        // ループが終了する前に Close()/Dispose() すると、ReceiveAsync 待機中の呼び出しが
        // NullReferenceException（内部状態が破棄されたことによる既知の挙動）で中断し得るため、
        // 正常系（OperationCanceledException）で確実に抜けさせてから解放する順序にする。
        _stoppingCts?.Cancel();

        if (_receiveLoopTask is not null)
        {
            await _receiveLoopTask.ConfigureAwait(false);
        }

        _stoppingCts?.Dispose();
        _udpClient.Close();
        _udpClient.Dispose();
        _udpClient = null;
    }

    private async Task ReceiveLoopAsync(CancellationToken stoppingToken)
    {
        var client = _udpClient!;

        while (!stoppingToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // StopAsync がトークンをキャンセルしたことによる正常な停止経路。
                // ソケットの Close()/Dispose() はこのループが終了した後に行う
                // （UdpSyslogListener.StopAsync 参照）。
                break;
            }
            catch (ObjectDisposedException)
            {
                // 防御的フォールバック: 通常経路ではソケットの破棄はループ終了後に行うため
                // 到達しないはずだが、予期しない破棄タイミングでの中断を停止経路として扱う。
                break;
            }
            catch (SocketException)
            {
                // 個々のデータグラムでの受信エラーは読み取りループを止めない
                // （§2.1「受信段はソケットからの読み取りに専念する」——エラーで停滞させない）。
                continue;
            }

            // 読み取り直後に ReceivedAt を刻印する（受信段の責務。解析はまだ行わない）。
            var receivedAt = DateTimeOffset.UtcNow;

            if (!_ingressGate.ShouldAdmit(result.RemoteEndPoint.Address, result.Buffer))
            {
                // v0.1 の NoopIngressGate は常に true を返すため、この分岐は到達しない。
                // 挿入点のみ（architecture.md §3.3）——判定・破棄の実装は後続マイルストーンで
                // 追加するが、計上の枠は M4-4 で確定する（「発火は必ず計測される」§3.3）。
                _metrics.RecordFlowControlDropped();
                continue;
            }

            var datagram = new RawDatagram(
                ReceivedAt: receivedAt,
                SourceAddress: result.RemoteEndPoint.Address.ToString(),
                SourcePort: result.RemoteEndPoint.Port,
                Protocol: Protocol.Udp,
                Payload: result.Buffer);

            if (!_q1Writer.TryWrite(datagram))
            {
                // Q1 満杯——ブロックせず破棄する（architecture.md §3.1）。
                _metrics.RecordInternalBufferDropped();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
