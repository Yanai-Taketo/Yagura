using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Net;
using Yagura.Ingestion.Tcp;
using Yagura.Ingestion.Udp;
using Yagura.Storage;

namespace Yagura.Ingestion.Tls;

/// <summary>
/// syslog over TLS の受信段（RFC 5425。TCP 6514。opt-in・既定無効。security.md §6。Issue #137）。
/// </summary>
/// <remarks>
/// <para>
/// <b>既存 TCP パイプラインとの関係</b>: Accept ループ・同時接続数上限・IPv4/IPv6 デュアルスタック
/// bind（<see cref="DualStackTcpListenerFactory"/>）は <see cref="Yagura.Ingestion.Tcp.TcpSyslogListener"/>
/// と同一の実装を共有する。Accept 後、平文 TCP は <c>NetworkStream</c> をそのまま読むのに対し、
/// 本クラスは <see cref="SslStream"/> でラップしてサーバ認証（相互 TLS はスコープ外——security.md §6・
/// Issue #137 のオーナー決定 2026-07-10「サーバ認証のみ」）のハンドシェイクを行ってから、
/// 同じ読み取りループ（<see cref="Yagura.Ingestion.Tcp.TcpFramedConnectionProcessor"/>）へ渡す。
/// </para>
/// <para>
/// <b>octet-counting の強制</b>（RFC 5425 §4.3）: <see cref="TcpFrameDecoderOptions.RequireOctetCounting"/>
/// を <c>true</c> にして構成する——non-transparent-framing を検出した接続は最初のチャンクで
/// 即座に切断する（PR #169 の A+B 天井を含む既存 <see cref="Yagura.Ingestion.Tcp.TcpFrameDecoder"/>
/// をそのまま流用。<see cref="Yagura.Ingestion.Tcp.TcpFrameDecoderOptions"/> 参照）。
/// </para>
/// <para>
/// <b>証明書の参照・期限切れ時の挙動</b>（security.md §6）: 証明書は
/// <paramref name="certificateSelector"/>（<c>Yagura.Host.Administration.Https.AdminCertificateProvider</c>
/// を Web UI の HTTPS と共有する形で Yagura.Host が結線する——本プロジェクトは Windows 証明書ストアに
/// 依存しない）が返す <see cref="X509Certificate2"/> を毎回のハンドシェイクで提示する。
/// <b>期限切れ・失効時もリスナは停止しない</b>——Web UI の HTTPS（Kestrel の
/// <c>ServerCertificateSelector</c> が期限切れで <c>null</c> を返しハンドシェイクを拒否する）とは
/// 非対称で、本クラスは期限を一切検査せず、常に <paramref name="certificateSelector"/> が返した
/// 証明書をそのまま提示し続ける（「ログを失わない」原則を通信の真正性より優先する。期限切れ中の
/// 実害は送信側の TLS ハンドシェイク失敗として現れ、<see cref="IngestionMetrics.RecordTlsHandshakeFailure"/>
/// で送信元別に計上する——security.md §6 の「止めない」判断の成否を運用者が観測できるようにする）。
/// </para>
/// </remarks>
public sealed class TlsSyslogListener : IAsyncDisposable
{
    private readonly TlsSyslogListenerOptions _options;
    private readonly ChannelWriter<RawDatagram> _q1Writer;
    private readonly IIngressGate _ingressGate;
    private readonly IngestionMetrics _metrics;
    private readonly Func<X509Certificate2?> _certificateSelector;
    private readonly ILogger<TlsSyslogListener>? _logger;
    private readonly TimeProvider _timeProvider;

    private TcpListener? _tcpListener;
    private Task? _acceptLoopTask;
    private CancellationTokenSource? _stoppingCts;

    private readonly ConcurrentDictionary<Task, byte> _connectionTasks = new();

    private int _currentConnectionCount;

    public TlsSyslogListener(
        TlsSyslogListenerOptions options,
        ChannelWriter<RawDatagram> q1Writer,
        IIngressGate ingressGate,
        IngestionMetrics metrics,
        Func<X509Certificate2?> certificateSelector,
        ILogger<TlsSyslogListener>? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(q1Writer);
        ArgumentNullException.ThrowIfNull(ingressGate);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(certificateSelector);

        _options = options;
        _q1Writer = q1Writer;
        _ingressGate = ingressGate;
        _metrics = metrics;
        _certificateSelector = certificateSelector;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>実際に束縛された TCP ポート（テスト用の OS 採番時に実ポートを取得するために使う）。</summary>
    public int BoundPort { get; private set; }

    /// <summary>現在の接続数（テスト・観測用）。</summary>
    public int CurrentConnectionCount => Volatile.Read(ref _currentConnectionCount);

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_tcpListener is not null)
        {
            throw new InvalidOperationException("TlsSyslogListener は既に開始されている。");
        }

        var bindAddress = IPAddress.Parse(_options.BindAddress);
        if (DualStackBindAddress.IsIPv6Wildcard(bindAddress))
        {
            _tcpListener = DualStackTcpListenerFactory.CreateOrFallBack(_options.Port, _options.BindAddressIsExplicit, _logger);
        }
        else
        {
            _tcpListener = new TcpListener(bindAddress, _options.Port);
            _tcpListener.Start();
        }

        BoundPort = ((IPEndPoint)_tcpListener.LocalEndpoint).Port;

        _stoppingCts = new CancellationTokenSource();
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_stoppingCts.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_tcpListener is null)
        {
            return;
        }

        _stoppingCts?.Cancel();

        if (_acceptLoopTask is not null)
        {
            await _acceptLoopTask.ConfigureAwait(false);
        }

        _tcpListener.Stop();

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
                continue;
            }

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
        var remoteAddress = remoteEndPoint is null ? null : DualStackBindAddress.NormalizeSourceAddress(remoteEndPoint.Address);
        var sourceAddress = remoteAddress?.ToString() ?? "unknown";
        var sourcePort = remoteEndPoint?.Port ?? 0;

        try
        {
            using (client)
            await using (var networkStream = client.GetStream())
            {
                var certificate = _certificateSelector();
                if (certificate is null)
                {
                    // 構成上 TLS 受信は有効だが、証明書を一切提示できない状態（起動時に証明書ストア
                    // 参照が失敗した場合。Yagura.Host 側は本来この状態ではリスナ自体を起動しないが、
                    // 万一 certificateSelector が実行時に null を返した場合の安全側フォールバックとして
                    // ハンドシェイク失敗として扱う——秘密鍵の無い TLS ハンドシェイクは成立しない）。
                    _metrics.RecordTlsHandshakeFailure(sourceAddress);
                    _logger?.LogWarning(
                        "TLS 接続 {SourceAddress}:{SourcePort} を拒否しました（提示できる証明書がありません）。",
                        sourceAddress,
                        sourcePort);
                    return;
                }

                await using var sslStream = new SslStream(networkStream, leaveInnerStreamOpen: false);

                // TLS ハンドシェイクの完了猶予（PR #225 レビュー指摘 High——未認証 DoS の遮断）:
                // ClientHello を送らない（または途中で黙る）接続がハンドシェイク段階のまま同時接続枠を
                // 占有し続けることを防ぐ。アイドル・フレーミング進捗タイムアウトはハンドシェイク成功後の
                // 読み取りループにしか効かないため、ハンドシェイク専用の天井を張る（TlsSyslogListenerOptions
                // 参照）。stoppingToken にリンクした CTS へ CancelAfter で猶予を設定する。
                var handshakeTimeoutEnabled = _options.HandshakeTimeout > TimeSpan.Zero;
                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                if (handshakeTimeoutEnabled)
                {
                    handshakeCts.CancelAfter(_options.HandshakeTimeout);
                }

                try
                {
                    // サーバ認証のみ（相互 TLS はスコープ外。security.md §6・Issue #137 オーナー決定
                    // 2026-07-10）。期限切れの証明書であっても提示し続ける——止めない判断は
                    // クラス remarks のとおり。TLS 1.2 以上・1.3 優先（configuration.md §6・
                    // security.md §2.5 の管理 UI HTTPS と同じ固定値）。
                    var authOptions = new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate,
                        ClientCertificateRequired = false,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    };

                    await sslStream.AuthenticateAsServerAsync(authOptions, handshakeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (handshakeTimeoutEnabled
                    && handshakeCts.IsCancellationRequested
                    && !stoppingToken.IsCancellationRequested)
                {
                    // ハンドシェイクが猶予時間内に完了しなかった（未認証 DoS の疑い。無言・低速な
                    // ClientHello 待ち）。他のハンドシェイク失敗と同じカウンタで計上し、送信元別に
                    // 観測可能にする（専用カウンタを増やさない——「TLS ハンドシェイクが成立しなかった」
                    // という単一の事象の一種）。ログはタイムアウトである旨を区別できる文言にする。
                    _metrics.RecordTlsHandshakeFailure(sourceAddress);
                    _logger?.LogWarning(
                        "TLS 接続 {SourceAddress}:{SourcePort} のハンドシェイクが猶予時間 {HandshakeTimeout} 以内に" +
                        "完了しなかったため切断します（未認証接続の資源占有を防ぐ）。",
                        sourceAddress,
                        sourcePort,
                        _options.HandshakeTimeout);
                    return;
                }
                catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException)
                {
                    // TLS ハンドシェイク失敗（security.md §6）: 証明書期限切れ時の送信側検証拒否・
                    // プロトコル不一致・相手の即時切断等がここに現れる。送信元別に計上する——
                    // 「止めない」判断の成否（期限切れ中にどの送信元が脱落しているか）を運用者が
                    // 観測できるようにする一次シグナル。
                    _metrics.RecordTlsHandshakeFailure(sourceAddress);
                    _logger?.LogWarning(
                        ex,
                        "TLS 接続 {SourceAddress}:{SourcePort} のハンドシェイクに失敗しました。",
                        sourceAddress,
                        sourcePort);
                    return;
                }

                var processor = new TcpFramedConnectionProcessor(_q1Writer, _ingressGate, _metrics, _logger, _timeProvider);

                var outcome = await processor.RunAsync(
                    sslStream,
                    Protocol.Tls,
                    sourceAddress,
                    sourcePort,
                    remoteAddress,
                    new TcpFrameDecoderOptions
                    {
                        MaxMessageLength = _options.MaxMessageLength,
                        MaxResyncBytes = _options.MaxResyncBytes,
                        // RFC 5425 §4.3: syslog over TLS は octet-counting のみを許容する。
                        RequireOctetCounting = true,
                    },
                    _options.IdleTimeout,
                    _options.FramingProgressTimeout,
                    "TLS",
                    stoppingToken).ConfigureAwait(false);

                if (outcome.IdleTimedOut)
                {
                    _metrics.RecordTcpConnectionIdleTimeout();
                    _logger?.LogInformation(
                        "TLS 接続 {SourceAddress}:{SourcePort} をアイドルタイムアウト（{IdleTimeout}）により切断しました。",
                        sourceAddress,
                        sourcePort,
                        _options.IdleTimeout);
                }
                else if (outcome.ResyncLimitExceeded)
                {
                    _metrics.RecordTcpConnectionResyncLimitExceeded();
                }
                else if (outcome.FramingProgressTimedOut)
                {
                    _metrics.RecordTcpConnectionFramingTimeout();
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _currentConnectionCount);

            // TLS 受信も「TCP 接続断」の内訳に含める（architecture.md §4.5。プロトコルを問わず
            // 接続終了そのものを 1 件計上する既存の意味論をそのまま踏襲する）。
            _metrics.RecordTcpConnectionClosed();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
