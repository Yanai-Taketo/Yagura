using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Net;
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
/// <para>
/// <b>受信エラー（SocketException）時の扱い（Issue #142）</b>: 個々の <c>ReceiveAsync</c>
/// 失敗（Windows の UDP では、直前の送信に対する ICMP ポート到達不能の反映等、環境依存の
/// 一過性エラーが発生し得る）は読み取りループを止めない（§2.1「受信段はソケットからの読み取りに
/// 専念する」）が、無言では握り潰さない。<see cref="IngestionMetrics.RecordUdpReceiveError"/>
/// で計上し、SocketErrorCode 付きでログ出力する（<see cref="ReceiveErrorLogThrottleWindow"/>
/// の間隔で抑制・集約し、持続的エラー時のログ溢れを防ぐ）。さらに連続して失敗する間は
/// <see cref="ComputeReceiveErrorBackoff"/> による短い backoff を挟み、持続的エラー状態での
/// 密ループによる CPU 浪費を防ぐ（単発エラーは backoff しない）。
/// </para>
/// <para>
/// <b>IPv4/IPv6 デュアルスタック受信（Issue #133）</b>: <see cref="UdpSyslogListenerOptions.BindAddress"/>
/// が IPv6 ワイルドカード（<c>::</c>。既定値）のときは、<see cref="Socket.DualMode"/> を有効にした
/// 単一ソケットで bind し、IPv4・IPv6 双方の送信元から受信する（<see cref="DualStackBindAddress"/>
/// 参照）。DualMode ソケットが受ける IPv4 由来のデータグラムは <c>RemoteEndPoint.Address</c> が
/// IPv4-mapped IPv6（<c>::ffff:x.x.x.x</c>）として現れるため、<see cref="RawDatagram.SourceAddress"/>
/// へ書き込む前に <see cref="DualStackBindAddress.NormalizeSourceAddress"/> で純粋な IPv4 表現へ
/// 正規化する（ADR-0007 決定 2 が <c>ReverseDnsResolver</c> で既に採用している規約と同じ）。
/// </para>
/// </remarks>
public sealed class UdpSyslogListener : IAsyncDisposable
{
    /// <summary>
    /// 受信エラーログの抑制ウィンドウ（Issue #142）。この間隔内に発生した同種のログは
    /// 1 回にまとめ、抑制した件数を添えて出力する（持続的エラー時のログ溢れを防ぐ）。
    /// </summary>
    internal static readonly TimeSpan ReceiveErrorLogThrottleWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 連続受信エラー時の backoff の初期値（ミリ秒。Issue #142）。1 回目のエラーでは
    /// backoff しない（<see cref="ComputeReceiveErrorBackoff"/> 参照）ため、2 回目の
    /// エラーで最初に適用される値になる。
    /// </summary>
    internal const int ReceiveErrorBackoffBaseMilliseconds = 10;

    /// <summary>
    /// 連続受信エラー時の backoff の上限値（ミリ秒。Issue #142）。持続的エラー時の密ループに
    /// よる CPU 浪費を防ぎつつ、過大な遅延で受信段の応答性を損なわないための上限。
    /// </summary>
    internal const int ReceiveErrorBackoffMaxMilliseconds = 1000;

    private readonly UdpSyslogListenerOptions _options;
    private readonly ChannelWriter<RawDatagram> _q1Writer;
    private readonly IIngressGate _ingressGate;
    private readonly IngestionMetrics _metrics;
    private readonly ILogger<UdpSyslogListener>? _logger;
    private readonly TimeProvider _timeProvider;
    private UdpClient? _udpClient;
    private Task? _receiveLoopTask;
    private CancellationTokenSource? _stoppingCts;

    // 受信エラー時の backoff・ログ抑制の状態（Issue #142）。読み取りループ（単一タスク）
    // からのみ更新される想定だが、直接ハンドラを呼ぶ単体テストからも呼ばれ得るため
    // 複数回呼び出しの直列実行のみを前提とする（並行呼び出しは想定しない）。
    private int _consecutiveReceiveErrors;
    private DateTimeOffset _receiveErrorThrottleWindowStartedAt = DateTimeOffset.MinValue;
    private int _suppressedReceiveErrorLogCount;

    public UdpSyslogListener(
        UdpSyslogListenerOptions options,
        ChannelWriter<RawDatagram> q1Writer,
        IIngressGate ingressGate,
        IngestionMetrics metrics,
        ILogger<UdpSyslogListener>? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(q1Writer);
        ArgumentNullException.ThrowIfNull(ingressGate);
        ArgumentNullException.ThrowIfNull(metrics);

        _options = options;
        _q1Writer = q1Writer;
        _ingressGate = ingressGate;
        _metrics = metrics;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 実際に束縛された UDP ポート。<see cref="UdpSyslogListenerOptions.Port"/> に 0
    /// を指定した場合（テスト用の OS 採番）に、開始後の実ポートを取得するために使う。
    /// </summary>
    public int BoundPort { get; private set; }

    /// <summary>
    /// 現在の連続受信エラー回数（テスト用。Issue #142）。「受信が 1 回成立するたびに
    /// 連続エラー回数がリセットされる」という <see cref="ReceiveLoopAsync"/> の中核挙動を
    /// 単体テストから観測するために internal 公開する。
    /// </summary>
    internal int ConsecutiveReceiveErrors => Volatile.Read(ref _consecutiveReceiveErrors);

    /// <summary>
    /// ソケットを bind し、読み取りループを開始する（architecture.md §1.2「受信を最初に開く」）。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_udpClient is not null)
        {
            throw new InvalidOperationException("UdpSyslogListener は既に開始されている。");
        }

        var bindAddress = IPAddress.Parse(_options.BindAddress);
        if (DualStackBindAddress.IsIPv6Wildcard(bindAddress))
        {
            // DualMode ソケット（Issue #133）。UdpClient(IPEndPoint) は指定エンドポイントの
            // アドレスファミリ単独のソケットを作るため、DualMode を有効にするには
            // AddressFamily 指定のコンストラクタで未 bind のソケットを作ってから
            // 明示的に DualMode を立て、その後 Bind する必要がある。
            _udpClient = new UdpClient(AddressFamily.InterNetworkV6);
            _udpClient.Client.DualMode = true;
            _udpClient.Client.Bind(new IPEndPoint(bindAddress, _options.Port));
        }
        else
        {
            // 明示的な 0.0.0.0（IPv4 のみ）・特定の IPv4/IPv6 アドレス指定は、
            // 従来どおりそのアドレスファミリ単独のソケットで bind する。
            _udpClient = new UdpClient(new IPEndPoint(bindAddress, _options.Port));
        }

        BoundPort = ((IPEndPoint)_udpClient.Client.LocalEndPoint!).Port;

        ApplyReceiveBufferSize(_udpClient.Client, _options.ReceiveBufferBytes, _logger);

        _stoppingCts = new CancellationTokenSource();
        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_stoppingCts.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <summary>
    /// UDP 受信ソケットの <c>SO_RCVBUF</c> を設定し、実効値をログへ記録する
    /// （architecture.md §9 M-2・§4.2「受信バッファサイズを設定項目にする」）。
    /// </summary>
    /// <remarks>
    /// <b>実効値を読み戻してログに出す理由</b>: OS が要求値を丸める、または一部の環境で
    /// setsockopt 自体が失敗する可能性があるため（legacy-lessons.md A-1 の記録。ただし
    /// 開発機（Windows 11 ARM64 10.0.26200・.NET 10.0.301）の実機検証では丸め・失敗のいずれも
    /// 再現しなかった——<see cref="UdpSyslogListenerOptions.DefaultReceiveBufferBytes"/> の
    /// remarks 参照）。設定した値と実際に効いた値が異なる環境が将来現れても、この一致・不一致を
    /// 起動ログで確認できるようにする。
    /// </remarks>
    private static void ApplyReceiveBufferSize(Socket socket, int requestedBytes, ILogger<UdpSyslogListener>? logger)
    {
        try
        {
            socket.ReceiveBufferSize = requestedBytes;
        }
        catch (SocketException ex)
        {
            // setsockopt 自体が失敗した環境（legacy-lessons.md A-1 が記録する仮説上の制約。
            // 本開発機では再現しないが、防御的に catch する）。ソケットは OS 既定のバッファの
            // まま受信を継続する——受信の成立に不可欠なキーではないため、ここで例外を
            // 再送出して起動を止めない（configuration.md §1「既定値で継続」と同じ判断）。
            logger?.LogWarning(
                ex,
                "UDP 受信バッファサイズ {RequestedBytes} バイトの設定に失敗したため、" +
                "OS 既定のバッファサイズ {EffectiveBytes} バイトのまま継続します。",
                requestedBytes,
                socket.ReceiveBufferSize);
            return;
        }

        var effectiveBytes = socket.ReceiveBufferSize;
        if (effectiveBytes == requestedBytes)
        {
            logger?.LogInformation(
                "UDP 受信バッファサイズを {EffectiveBytes} バイトに設定しました。",
                effectiveBytes);
        }
        else
        {
            // OS が要求値を丸めた場合の観測点（本開発機では未観測。§4.2 M-2 の実機記録参照）。
            logger?.LogWarning(
                "UDP 受信バッファサイズは要求値 {RequestedBytes} バイトに対し、" +
                "OS により {EffectiveBytes} バイトへ丸められました。",
                requestedBytes,
                effectiveBytes);
        }
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
            catch (SocketException ex)
            {
                // 個々のデータグラムでの受信エラーは読み取りループを止めない
                // （§2.1「受信段はソケットからの読み取りに専念する」——エラーで停滞させない）。
                // ただし無言では握り潰さない——ログ・メトリクス・backoff を行う（Issue #142）。
                if (!await HandleReceiveErrorAsync(ex, stoppingToken).ConfigureAwait(false))
                {
                    // backoff の待機中に停止要求（トークンのキャンセル）を受けた。
                    // OperationCanceledException と同じ正常な停止経路として扱う。
                    break;
                }

                continue;
            }

            // 受信が成立したので連続エラーのカウントをリセットする（Issue #142。
            // 次に SocketException が起きても「連続」の 1 回目からやり直す）。
            // Volatile.Write は ConsecutiveReceiveErrors（テスト用の観測点）が別スレッドから
            // リセットを確実に観測できるようにするため。
            Volatile.Write(ref _consecutiveReceiveErrors, 0);

            // 読み取り直後に ReceivedAt を刻印する（受信段の責務。解析はまだ行わない）。
            var receivedAt = DateTimeOffset.UtcNow;

            // DualMode ソケットが受けた IPv4 送信元は ::ffff:x.x.x.x として現れるため、
            // 判定・記録の前に正規化する（Issue #133。DualStackBindAddress の remarks 参照）。
            var remoteAddress = DualStackBindAddress.NormalizeSourceAddress(result.RemoteEndPoint.Address);

            if (!_ingressGate.ShouldAdmit(remoteAddress, result.Buffer))
            {
                // v0.1 の NoopIngressGate は常に true を返すため、この分岐は到達しない。
                // 挿入点のみ（architecture.md §3.3）——判定・破棄の実装は後続マイルストーンで
                // 追加するが、計上の枠は M4-4 で確定する（「発火は必ず計測される」§3.3）。
                _metrics.RecordFlowControlDropped();
                continue;
            }

            var datagram = new RawDatagram(
                ReceivedAt: receivedAt,
                SourceAddress: remoteAddress.ToString(),
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

    /// <summary>
    /// UDP 受信ソケットの受信エラー（<see cref="SocketException"/>）を処理する（Issue #142）:
    /// メトリクスへ計上し、抑制付きでログ出力し、連続失敗の度合いに応じた backoff を待つ。
    /// </summary>
    /// <returns>
    /// 読み取りループを継続してよければ <c>true</c>。backoff の待機中に <paramref name="stoppingToken"/>
    /// がキャンセルされた場合は <c>false</c>（呼び出し元は正常な停止経路として扱う）。
    /// </returns>
    /// <remarks>
    /// 単体テストから直接呼び出せるよう <c>internal</c> にする（実際の <see cref="SocketException"/>
    /// をネットワーク経由で確実に再現するのは環境依存で難しいため、
    /// <c>InternalsVisibleTo(&quot;Yagura.Ingestion.Tests&quot;)</c> 経由でロジックを直接検証する）。
    /// </remarks>
    internal async Task<bool> HandleReceiveErrorAsync(SocketException ex, CancellationToken stoppingToken)
    {
        // int.MaxValue で頭打ちにする（PR #163 レビュー指摘 2）: 無条件インクリメントだと
        // 折り返し（オーバーフロー）で負値 → ComputeReceiveErrorBackoff の
        // consecutiveErrorCount <= 1 判定に該当し、持続的エラーの真っ最中に backoff が
        // 突然ゼロへ戻る。実務上は到達しない回数（最大 backoff 1000ms 換算で数十年オーダー）
        // だが、「持続的エラー時の CPU 浪費防止」という目的の理論上の穴を残さない。
        if (_consecutiveReceiveErrors < int.MaxValue)
        {
            _consecutiveReceiveErrors++;
        }

        var consecutiveErrors = _consecutiveReceiveErrors;

        _metrics.RecordUdpReceiveError();
        LogReceiveErrorThrottled(ex, consecutiveErrors);

        var backoff = ComputeReceiveErrorBackoff(consecutiveErrors);
        if (backoff <= TimeSpan.Zero)
        {
            return true;
        }

        try
        {
            await Task.Delay(backoff, _timeProvider, stoppingToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// 連続受信エラー回数から backoff 時間を求める（Issue #142）。1 回目（単発）のエラーは
    /// backoff しない——transient な単発エラーを不必要に遅延させないため。2 回目のエラーで
    /// 最初の backoff <see cref="ReceiveErrorBackoffBaseMilliseconds"/>（10ms）が適用され、
    /// 以降は指数的に伸ばし、<see cref="ReceiveErrorBackoffMaxMilliseconds"/> で頭打ちにする
    /// （持続的エラー時の密ループによる CPU 浪費を防ぐ）。
    /// </summary>
    internal static TimeSpan ComputeReceiveErrorBackoff(int consecutiveErrorCount)
    {
        if (consecutiveErrorCount <= 1)
        {
            return TimeSpan.Zero;
        }

        // 指数は「2 回目 = 底値そのもの（シフト 0）」を起点にする（PR #163 レビュー指摘 1:
        // 「10ms 起点」という文言と、実際に発生する最小 backoff を一致させる）。
        // シフト量はオーバーフロー防止のため頭打ちにする（上限到達後は Math.Min が効くため
        // 大きすぎるシフト量そのものは結果に影響しない）。
        var exponent = Math.Min(consecutiveErrorCount - 2, 10);
        var delayMilliseconds = Math.Min(
            (long)ReceiveErrorBackoffBaseMilliseconds << exponent,
            ReceiveErrorBackoffMaxMilliseconds);

        return TimeSpan.FromMilliseconds(delayMilliseconds);
    }

    /// <summary>
    /// 受信エラーを抑制付きでログ出力する（Issue #142）。<see cref="ReceiveErrorLogThrottleWindow"/>
    /// 内に発生した同種のログは 1 回にまとめ、抑制した件数を添えて出力する。
    /// </summary>
    private void LogReceiveErrorThrottled(SocketException ex, int consecutiveErrorCount)
    {
        if (_logger is null)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        if (_receiveErrorThrottleWindowStartedAt != DateTimeOffset.MinValue &&
            now - _receiveErrorThrottleWindowStartedAt < ReceiveErrorLogThrottleWindow)
        {
            _suppressedReceiveErrorLogCount++;
            return;
        }

        var suppressedCount = _suppressedReceiveErrorLogCount;
        _suppressedReceiveErrorLogCount = 0;
        _receiveErrorThrottleWindowStartedAt = now;

        if (suppressedCount > 0)
        {
            _logger.LogWarning(
                ex,
                "UDP 受信でエラーが発生しました（SocketErrorCode={SocketErrorCode}、連続 {ConsecutiveErrorCount} 件目）。" +
                "直近 {ThrottleWindowSeconds} 秒間に、さらに {SuppressedCount} 件の受信エラーログを抑制しました。",
                ex.SocketErrorCode,
                consecutiveErrorCount,
                ReceiveErrorLogThrottleWindow.TotalSeconds,
                suppressedCount);
        }
        else
        {
            _logger.LogWarning(
                ex,
                "UDP 受信でエラーが発生しました（SocketErrorCode={SocketErrorCode}、連続 {ConsecutiveErrorCount} 件目）。",
                ex.SocketErrorCode,
                consecutiveErrorCount);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
