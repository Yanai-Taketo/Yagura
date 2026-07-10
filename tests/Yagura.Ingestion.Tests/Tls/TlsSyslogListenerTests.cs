using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Tls;
using Yagura.Ingestion.Udp;
using Yagura.Storage;

namespace Yagura.Ingestion.Tests.Tls;

/// <summary>
/// <see cref="TlsSyslogListener"/> の単体テスト（syslog over TLS。RFC 5425。opt-in。security.md §6。
/// Issue #137）。
/// </summary>
public sealed class TlsSyslogListenerTests
{
    private static Channel<RawDatagram> CreateQ1(int capacity = 1024) =>
        Channel.CreateBounded<RawDatagram>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

    /// <summary>
    /// テスト用の自己署名証明書を発行する（Windows 証明書ストアは経由しない——本テストは
    /// <see cref="TlsSyslogListener"/> 自体の TLS/フレーミング挙動を検証する単体テストであり、
    /// ストア参照は Yagura.Host 側（AdminCertificateProvider の再利用）の管轄。別途 E2E で検証）。
    /// </summary>
    /// <remarks>
    /// <b>エフェメラルキーのままではサーバロール認証に使えない（実機確認）</b>:
    /// <see cref="CertificateRequest.CreateSelfSigned"/> が返す証明書はエフェメラル（プロセス内のみの）
    /// CNG キーを持ち、<see cref="SslStream.AuthenticateAsServerAsync(SslServerAuthenticationOptions, CancellationToken)"/>
    /// （Windows では Schannel 経由）はこれを受け付けず
    /// <c>AuthenticationException: Authentication failed because the platform does not support
    /// ephemeral keys.</c>（内部的には Win32Exception 0x8009030E）で失敗することを実機確認した——
    /// クライアントロール（<c>AuthenticateAsClientAsync</c>）は問題なく通るため、この非対称に
    /// 気づきにくい。PFX へ一度書き出して読み直すことで、実際のキーコンテナを持つ非エフェメラルな
    /// 鍵へ変換する（<c>Yagura.E2E.Tests.AdminRemoteBindingRegressionTests.IssueAndInstallTestCertificate</c>
    /// と同じ回避策——あちらは証明書ストアへの導入まで行うが、本テストはプロセスローカルの
    /// キーコンテナへのインポートのみで足りる）。
    /// </remarks>
    private static TestCertificate IssueTestCertificate(TimeSpan? validityDuration = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=yagura-tls-listener-test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = notBefore.Add(validityDuration ?? TimeSpan.FromDays(30));

        using var ephemeral = request.CreateSelfSigned(notBefore, notAfter);
        var pfxBytes = ephemeral.Export(X509ContentType.Pfx);
        var persisted = X509CertificateLoader.LoadPkcs12(pfxBytes, password: null, X509KeyStorageFlags.Exportable);

        return new TestCertificate(persisted);
    }

    /// <summary>
    /// <see cref="IssueTestCertificate"/> が作るキーコンテナをテスト終了時に確実に削除する
    /// ラッパー（Windows のユーザープロファイル配下に鍵ファイルが残留しないようにする）。
    /// </summary>
    private sealed class TestCertificate : IDisposable
    {
        public TestCertificate(X509Certificate2 certificate) => Certificate = certificate;

        public X509Certificate2 Certificate { get; }

        public void Dispose()
        {
            if (Certificate.GetRSAPrivateKey() is RSACng rsaCng)
            {
                rsaCng.Key.Delete();
            }

            Certificate.Dispose();
        }
    }

    private static async Task<RawDatagram> ReadWithTimeoutAsync(ChannelReader<RawDatagram> reader, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await reader.ReadAsync(cts.Token);
    }

    private static async Task<SslStream> ConnectAndAuthenticateClientAsync(TcpClient client, int port)
    {
        await client.ConnectAsync(IPAddress.Loopback, port);

        var sslStream = new SslStream(
            client.GetStream(),
            leaveInnerStreamOpen: false,
            userCertificateValidationCallback: (_, _, _, _) => true); // 自己署名を許容（テスト用）。

        await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "yagura-tls-listener-test",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        });

        return sslStream;
    }

    // ------------------------------------------------------------------
    // 正常系: TLS ハンドシェイク成功 → octet-counting フレーミング → Q1 到達
    // ------------------------------------------------------------------

    [Fact]
    public async Task OctetCountingMessageOverTls_ArrivesInQ1WithTlsProtocol()
    {
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();
        using var testCertificate = IssueTestCertificate();

        var listener = new TlsSyslogListener(
            new TlsSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics,
            () => testCertificate.Certificate);

        await listener.StartAsync();
        try
        {
            using var client = new TcpClient();
            await using var sslStream = await ConnectAndAuthenticateClientAsync(client, listener.BoundPort);

            var frame = Encoding.ASCII.GetBytes("9 <34>hello");
            await sslStream.WriteAsync(frame);
            await sslStream.FlushAsync();

            var datagram = await ReadWithTimeoutAsync(q1.Reader, TimeSpan.FromSeconds(10));

            Assert.Equal(Protocol.Tls, datagram.Protocol);
            Assert.False(datagram.Incomplete);
            Assert.Equal("<34>hello", Encoding.ASCII.GetString(datagram.Payload));
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    [Fact]
    public async Task MultipleOctetCountingMessages_AllArriveInQ1InOrder()
    {
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();
        using var testCertificate = IssueTestCertificate();

        var listener = new TlsSyslogListener(
            new TlsSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics,
            () => testCertificate.Certificate);

        await listener.StartAsync();
        try
        {
            using var client = new TcpClient();
            await using var sslStream = await ConnectAndAuthenticateClientAsync(client, listener.BoundPort);

            var frame = Encoding.ASCII.GetBytes("5 abcde3 xyz");
            await sslStream.WriteAsync(frame);
            await sslStream.FlushAsync();

            var first = await ReadWithTimeoutAsync(q1.Reader, TimeSpan.FromSeconds(10));
            var second = await ReadWithTimeoutAsync(q1.Reader, TimeSpan.FromSeconds(10));

            Assert.Equal("abcde", Encoding.ASCII.GetString(first.Payload));
            Assert.Equal("xyz", Encoding.ASCII.GetString(second.Payload));
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    [Fact]
    public async Task Disconnect_WithPendingOctetCountingData_ArrivesAsIncompleteInQ1()
    {
        // TcpSyslogListenerTests の同名テストと対称——TLS 経由でも切断時の読みかけデータが
        // Incomplete として Q1 へ流れること（TcpFramedConnectionProcessor の共有経路の確認）。
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();
        using var testCertificate = IssueTestCertificate();

        var listener = new TlsSyslogListener(
            new TlsSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics,
            () => testCertificate.Certificate);

        await listener.StartAsync();
        try
        {
            using var client = new TcpClient();
            await using var sslStream = await ConnectAndAuthenticateClientAsync(client, listener.BoundPort);

            // MSG-LEN 宣言（20）に対し本体を 5 バイトしか送らずに切断する。
            var partial = Encoding.ASCII.GetBytes("20 <34>part");
            await sslStream.WriteAsync(partial);
            await sslStream.FlushAsync();

            // TcpSyslogListenerTests の同名テストと同じ理由で、SslStream 越しの正常クローズ
            // （TLS close_notify を伴う）ではなく、ソケットの送信側を直接シャットダウンする
            // （FIN を即座に送出——相手の読み取りループを確実かつ迅速に終端させる）。
            // sslStream.Close()/DisposeAsync による正常クローズは CI 環境で完了までの時間が
            // 揺らぎ、10 秒のタイムアウト内に Q1 到達を確認できず flaky になることを実機
            // （GitHub Actions CI）で確認した——半クローズによる決定的な切断に変更した。
            client.Client.Shutdown(SocketShutdown.Send);

            var datagram = await ReadWithTimeoutAsync(q1.Reader, TimeSpan.FromSeconds(10));

            Assert.True(datagram.Incomplete);
            Assert.Equal(Protocol.Tls, datagram.Protocol);
            Assert.Equal("<34>part", Encoding.ASCII.GetString(datagram.Payload));
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    // ------------------------------------------------------------------
    // RFC 5425 §4.3: octet-counting のみを許容する（non-transparent-framing は拒否）
    // ------------------------------------------------------------------

    [Fact]
    public async Task NonTransparentFramingOverTls_ConnectionClosedWithoutDeliveringMessage()
    {
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();
        using var testCertificate = IssueTestCertificate();

        var listener = new TlsSyslogListener(
            new TlsSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics,
            () => testCertificate.Certificate);

        await listener.StartAsync();
        try
        {
            using var client = new TcpClient();
            await using var sslStream = await ConnectAndAuthenticateClientAsync(client, listener.BoundPort);

            // 先頭バイトが数字ではない = non-transparent-framing。RFC 5425 は許容しないため、
            // TlsSyslogListener はこの接続を即座に切断する（TcpFrameDecoderOptions.RequireOctetCounting）。
            var frame = Encoding.ASCII.GetBytes("<34>not-octet-counting\n");
            await sslStream.WriteAsync(frame);
            await sslStream.FlushAsync();

            // 接続が切断されること（読み取りが 0 バイトで終わる、または例外）を確認する。
            var buffer = new byte[16];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var closed = false;
            try
            {
                var read = await sslStream.ReadAsync(buffer, cts.Token);
                closed = read == 0;
            }
            catch (IOException)
            {
                closed = true;
            }

            Assert.True(closed, "non-transparent-framing の接続は切断されること。");
            Assert.Equal(0, q1.Reader.Count);
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    // ------------------------------------------------------------------
    // TLS ハンドシェイク失敗（security.md §6。送信元別カウンタ）
    // ------------------------------------------------------------------

    [Fact]
    public async Task NonTlsClient_HandshakeFails_RecordsTlsHandshakeFailureCounter()
    {
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();
        using var meterCollector = new MetricCollector<long>(metrics.TlsHandshakeFailureCounter, timeProvider: null);
        using var testCertificate = IssueTestCertificate();

        var listener = new TlsSyslogListener(
            new TlsSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics,
            () => testCertificate.Certificate);

        await listener.StartAsync();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.BoundPort);

            // TLS ハンドシェイクを開始せず、平文のゴミバイトを送る——サーバ側の
            // AuthenticateAsServerAsync が不正な TLS レコードとして失敗する。
            var garbage = Encoding.ASCII.GetBytes("this is not a TLS ClientHello at all, just plain garbage bytes");
            await client.GetStream().WriteAsync(garbage);
            await client.GetStream().FlushAsync();

            await meterCollector.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(10));
            var measurements = meterCollector.GetMeasurementSnapshot();
            Assert.Equal(1, measurements.Sum(m => m.Value));

            // 送信元別（source_address タグ）に計上されること（security.md §6）。
            var tags = measurements[0].Tags.ToArray();
            var tag = Assert.Single(tags);
            Assert.Equal("source_address", tag.Key);
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    // ------------------------------------------------------------------
    // TLS ハンドシェイクタイムアウト（PR #225 レビュー指摘 High——未認証 DoS の遮断）
    // ------------------------------------------------------------------

    [Fact]
    public async Task SilentClient_HandshakeTimesOut_RecordsFailureAndClosesConnection()
    {
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();
        using var meterCollector = new MetricCollector<long>(metrics.TlsHandshakeFailureCounter, timeProvider: null);
        using var testCertificate = IssueTestCertificate();

        var listener = new TlsSyslogListener(
            new TlsSyslogListenerOptions
            {
                BindAddress = "127.0.0.1",
                Port = 0,
                // 短い猶予で決定的に検証する（実時間ベースの CancelAfter。無言接続を素早く回収する）。
                HandshakeTimeout = TimeSpan.FromMilliseconds(300),
            },
            q1.Writer,
            new NoopIngressGate(),
            metrics,
            () => testCertificate.Certificate);

        await listener.StartAsync();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.BoundPort);

            // TCP 接続は確立するが、ClientHello を一切送らず黙る（未認証 DoS の模擬）。
            // サーバ側はハンドシェイク猶予（300ms）超過で接続を破棄し、TLS ハンドシェイク失敗として
            // 計上するはず。
            await meterCollector.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(10));
            var measurements = meterCollector.GetMeasurementSnapshot();
            Assert.Equal(1, measurements.Sum(m => m.Value));

            // 接続がサーバ側で回収され、同時接続枠が解放されること（DoS 遮断の実効）。
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (listener.CurrentConnectionCount > 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.Equal(0, listener.CurrentConnectionCount);
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    [Fact]
    public async Task ValidClient_CompletesHandshakeWithinTimeout_IsNotAffected()
    {
        // 正常なクライアントは猶予内にハンドシェイクを完了し、タイムアウトの巻き添えにならないこと
        // （HandshakeTimeout が正常系を壊さない回帰確認）。
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();
        using var testCertificate = IssueTestCertificate();

        var listener = new TlsSyslogListener(
            new TlsSyslogListenerOptions
            {
                BindAddress = "127.0.0.1",
                Port = 0,
                HandshakeTimeout = TimeSpan.FromSeconds(15),
            },
            q1.Writer,
            new NoopIngressGate(),
            metrics,
            () => testCertificate.Certificate);

        await listener.StartAsync();
        try
        {
            using var client = new TcpClient();
            await using var sslStream = await ConnectAndAuthenticateClientAsync(client, listener.BoundPort);

            var frame = Encoding.ASCII.GetBytes("9 <34>hello");
            await sslStream.WriteAsync(frame);
            await sslStream.FlushAsync();

            var datagram = await ReadWithTimeoutAsync(q1.Reader, TimeSpan.FromSeconds(10));
            Assert.Equal("<34>hello", Encoding.ASCII.GetString(datagram.Payload));
        }
        finally
        {
            await listener.StopAsync();
        }
    }
}
