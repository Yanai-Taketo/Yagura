using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Time.Testing;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Tcp;
using Yagura.Ingestion.Udp;
using Yagura.Storage;

namespace Yagura.Ingestion.Tests.Tcp;

/// <summary>
/// <see cref="TcpSyslogListener"/> の単体テスト（M4-1）。
/// architecture.md §3.1「TCP は読み取り停止」・§4.1「TCP 接続拒否」、database.md §2.1
/// 「不完全は解析失敗に優先」の各挙動を確認する。
/// </summary>
public sealed class TcpSyslogListenerTests
{
    private static Channel<RawDatagram> CreateQ1(int capacity = 1024) =>
        Channel.CreateBounded<RawDatagram>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

    // ------------------------------------------------------------------
    // 切断時の Incomplete 化（database.md §2.1）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Disconnect_WithPendingNonTransparentData_ArrivesAsIncompleteInQ1()
    {
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();

        var listener = new TcpSyslogListener(
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.BoundPort);

            var stream = client.GetStream();
            var partial = Encoding.ASCII.GetBytes("<34>never-terminated-by-lf");
            await stream.WriteAsync(partial);
            await stream.FlushAsync();

            // 送信側から切断する（相手が読み取り中の TCP 接続を閉じる = FIN 送出）。
            client.Client.Shutdown(SocketShutdown.Send);

            var datagram = await ReadWithTimeoutAsync(q1.Reader, TimeSpan.FromSeconds(10));

            Assert.True(datagram.Incomplete);
            Assert.Equal(Protocol.Tcp, datagram.Protocol);
            Assert.Equal("<34>never-terminated-by-lf", Encoding.ASCII.GetString(datagram.Payload));
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    [Fact]
    public async Task Disconnect_WithPendingOctetCountingData_ArrivesAsIncompleteInQ1()
    {
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();

        var listener = new TcpSyslogListener(
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.BoundPort);

            var stream = client.GetStream();
            // MSG-LEN 50 を宣言するが、本体は 10 バイトしか送らずに切断する。
            var partial = Encoding.ASCII.GetBytes("50 <34>only");
            await stream.WriteAsync(partial);
            await stream.FlushAsync();
            client.Client.Shutdown(SocketShutdown.Send);

            var datagram = await ReadWithTimeoutAsync(q1.Reader, TimeSpan.FromSeconds(10));

            Assert.True(datagram.Incomplete);
            Assert.Equal("<34>only", Encoding.ASCII.GetString(datagram.Payload));
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    [Fact]
    public async Task CompleteMessage_DisconnectAfterward_DoesNotProduceIncompleteRecord()
    {
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();

        var listener = new TcpSyslogListener(
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, listener.BoundPort);
                var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes("<34>complete-message\n"));
                await stream.FlushAsync();
                client.Client.Shutdown(SocketShutdown.Send);

                var datagram = await ReadWithTimeoutAsync(q1.Reader, TimeSpan.FromSeconds(10));
                Assert.False(datagram.Incomplete);
                Assert.Equal("<34>complete-message", Encoding.ASCII.GetString(datagram.Payload));
            }
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    // ------------------------------------------------------------------
    // 同時接続数上限（architecture.md §3.1・§4.1「TCP 接続拒否」）
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConnectionLimitReached_RejectsNewConnectionAndIncrementsCounter()
    {
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();
        using var meterCollector = new MetricCollector<long>(metrics.TcpConnectionRejectedCounter, timeProvider: null);

        var listener = new TcpSyslogListener(
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0, MaxConcurrentConnections = 1 },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            using var firstClient = new TcpClient();
            await firstClient.ConnectAsync(IPAddress.Loopback, listener.BoundPort);

            // 1 件目の接続が listener 側で受理されるのを待つ（CurrentConnectionCount で確認）。
            await WaitUntilAsync(() => listener.CurrentConnectionCount >= 1, TimeSpan.FromSeconds(10));

            using var secondClient = new TcpClient();
            await secondClient.ConnectAsync(IPAddress.Loopback, listener.BoundPort);

            // 2 件目は上限到達により Accept 直後に閉じられる——相手側で切断（EOF）を観測できる。
            var stream = secondClient.GetStream();
            var buffer = new byte[16];
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var bytesRead = await stream.ReadAsync(buffer, readCts.Token);
            Assert.Equal(0, bytesRead); // EOF = 相手にクローズされた

            await meterCollector.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(10));
        }
        finally
        {
            await listener.StopAsync();
        }

        var measurements = meterCollector.GetMeasurementSnapshot();
        Assert.NotEmpty(measurements);
        Assert.True(measurements.Sum(m => m.Value) >= 1);
    }

    // ------------------------------------------------------------------
    // Q1 満杯時の読み取り停滞（architecture.md §3.1「TCP は読み取り停止」）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Q1Full_StallsReadingInsteadOfDropping()
    {
        // Q1 の容量を 1 に絞り、読み手を配置しない状態で複数件送ると、2 件目以降の
        // WriteAsync が完了せず、結果としてソケット読み取りが進まなくなることを確認する
        // （UDP の破棄とは異なり、破棄カウンタは増えない——読み取りが「停滞」するだけ）。
        var q1 = CreateQ1(capacity: 1);
        using var metrics = new IngestionMetrics();

        var listener = new TcpSyslogListener(
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.BoundPort);
            var stream = client.GetStream();

            // 1 件目は Q1 の空き 1 枠に収まる。
            await stream.WriteAsync(Encoding.ASCII.GetBytes("<34>first\n"));
            // 2 件目は Q1 が満杯のため、受信段が WriteAsync で足止めされるはず。
            await stream.WriteAsync(Encoding.ASCII.GetBytes("<34>second\n"));
            await stream.FlushAsync();

            // 1 件目が Q1 に入るのを待ってから（固定 sleep のみだと、遅いランナーでは
            // 1 件目すら未到達のまま判定して誤失敗し得る——Issue #215 と同種の素地）、
            // 短い猶予を置いて 2 件目が Q1 に届いていないことを確認する
            // （1 件目は Q1 の中、2 件目は WriteAsync で足止め）。
            await WaitUntilAsync(() => q1.Reader.Count >= 1, TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromMilliseconds(200));

            Assert.Equal(1, q1.Reader.Count);

            // Q1 を drain すると、足止めされていた 2 件目の WriteAsync が完了し、
            // 2 件とも取り出せることを確認する（読み取りが「停止」であって「消失」でないことの証明）。
            var first = await ReadWithTimeoutAsync(q1.Reader, TimeSpan.FromSeconds(5));
            var second = await ReadWithTimeoutAsync(q1.Reader, TimeSpan.FromSeconds(5));

            Assert.Equal("<34>first", Encoding.ASCII.GetString(first.Payload));
            Assert.Equal("<34>second", Encoding.ASCII.GetString(second.Payload));
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    // ------------------------------------------------------------------
    // 1 メッセージの逸脱への耐性（architecture.md §4.5。Issue #143）:
    // サイズ上限超過は接続を切断せず当該メッセージのみ破棄する。再同期不能な破損のみ切断する。
    // ------------------------------------------------------------------

    [Fact]
    public async Task OversizedMessage_DiscardsOnlyThatMessageAndKeepsConnectionOpen()
    {
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();
        using var meterCollector = new MetricCollector<long>(metrics.TcpMessageDiscardedOversizedCounter, timeProvider: null);

        var listener = new TcpSyslogListener(
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0, MaxMessageLength = 10 },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.BoundPort);
            var stream = client.GetStream();

            // MSG-LEN 100 は上限 10 を超えるため破棄される。続けて正常なフレームを同じ接続で送る。
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"100 {new string('x', 100)}5 abcde"));
            await stream.FlushAsync();

            var datagram = await ReadWithTimeoutAsync(q1.Reader, TimeSpan.FromSeconds(10));

            Assert.Equal("abcde", Encoding.ASCII.GetString(datagram.Payload));
            Assert.False(datagram.Incomplete);

            await meterCollector.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(10));

            // 接続がまだ生きていることを確認する（切断されていれば CurrentConnectionCount は 0 になる）。
            Assert.Equal(1, listener.CurrentConnectionCount);
        }
        finally
        {
            await listener.StopAsync();
        }

        var measurements = meterCollector.GetMeasurementSnapshot();
        Assert.Equal(1, measurements.Sum(m => m.Value));
    }

    [Fact]
    public async Task InterFrameStrayLfCr_ResyncsWithoutDisconnecting()
    {
        // Issue #143: octet-counting のフレーム間に紛れた LF/CR は寛容にスキップして再同期する。
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();

        var listener = new TcpSyslogListener(
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.BoundPort);
            var stream = client.GetStream();

            await stream.WriteAsync(Encoding.ASCII.GetBytes("5 first\r\n5 secnd"));
            await stream.FlushAsync();

            var first = await ReadWithTimeoutAsync(q1.Reader, TimeSpan.FromSeconds(10));
            var second = await ReadWithTimeoutAsync(q1.Reader, TimeSpan.FromSeconds(10));

            Assert.Equal("first", Encoding.ASCII.GetString(first.Payload));
            Assert.Equal("secnd", Encoding.ASCII.GetString(second.Payload));
            Assert.Equal(1, listener.CurrentConnectionCount);
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    [Fact]
    public async Task UnrecoverableFrameCorruption_StillDisconnectsConnection()
    {
        // MSG-LEN の桁の途中に数字以外が混入するのは再同期不能な破損であり、従来どおり切断する。
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();
        using var closedCollector = new MetricCollector<long>(metrics.TcpConnectionClosedCounter, timeProvider: null);

        var listener = new TcpSyslogListener(
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.BoundPort);
            var stream = client.GetStream();

            await stream.WriteAsync(Encoding.ASCII.GetBytes("1x2 body"));
            await stream.FlushAsync();

            await WaitUntilAsync(() => listener.CurrentConnectionCount == 0, TimeSpan.FromSeconds(10));
            await closedCollector.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(10));
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    [Fact]
    public async Task UnrecoverableCorruption_CompletedMessagesInSameChunkStillReachQ1()
    {
        // PR #169 レビュー指摘 2: 1 チャンク内に「複数の正常フレーム + 末尾に再同期不能な破損」が
        // 同居しても、例外送出までに境界が確定していた正常メッセージは切断前に Q1 へ届くこと
        // （Q1 未到達・カウンタ計上なしのまま黙って消えないこと——architecture.md §3.1 の原則）。
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();

        var listener = new TcpSyslogListener(
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.BoundPort);
            var stream = client.GetStream();

            // 正常フレーム 2 件の直後に MSG-LEN 桁の途中の非数字（再同期不能な破損）。
            // 1 回の書き込みで送り、受信側で 1 チャンクにまとまって届く状況を作る。
            await stream.WriteAsync(Encoding.ASCII.GetBytes("5 abcde5 fghij1x2 broken"));
            await stream.FlushAsync();

            var first = await ReadWithTimeoutAsync(q1.Reader, TimeSpan.FromSeconds(10));
            var second = await ReadWithTimeoutAsync(q1.Reader, TimeSpan.FromSeconds(10));

            Assert.Equal("abcde", Encoding.ASCII.GetString(first.Payload));
            Assert.False(first.Incomplete);
            Assert.Equal("fghij", Encoding.ASCII.GetString(second.Payload));
            Assert.False(second.Incomplete);

            // 破損の検出により接続自体は切断される（確定済みメッセージを流し終えた後）。
            await WaitUntilAsync(() => listener.CurrentConnectionCount == 0, TimeSpan.FromSeconds(10));
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    [Fact]
    public async Task NormalDisconnect_IncrementsTcpConnectionClosedCounter()
    {
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();
        using var closedCollector = new MetricCollector<long>(metrics.TcpConnectionClosedCounter, timeProvider: null);

        var listener = new TcpSyslogListener(
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, listener.BoundPort);
                await WaitUntilAsync(() => listener.CurrentConnectionCount >= 1, TimeSpan.FromSeconds(10));
            }

            await closedCollector.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(10));
        }
        finally
        {
            await listener.StopAsync();
        }

        var measurements = closedCollector.GetMeasurementSnapshot();
        Assert.True(measurements.Sum(m => m.Value) >= 1);
    }

    // ------------------------------------------------------------------
    // 寛容化の天井（architecture.md §4.5。オーナー決定 2026-07-09）:
    // A = 再同期バイト数上限 / B = フレーミング進捗タイムアウト。
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResyncByteLimitExceeded_DisconnectsAndIncrementsCounter()
    {
        // A: 有効なメッセージが確定しないまま読み捨てたバイト数が上限を超えた接続は切断される。
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();
        using var resyncCollector = new MetricCollector<long>(metrics.TcpConnectionResyncLimitExceededCounter, timeProvider: null);
        using var closedCollector = new MetricCollector<long>(metrics.TcpConnectionClosedCounter, timeProvider: null);

        var listener = new TcpSyslogListener(
            new TcpSyslogListenerOptions
            {
                BindAddress = "127.0.0.1",
                Port = 0,
                MaxMessageLength = 10,
                MaxResyncBytes = 20,
            },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.BoundPort);
            var stream = client.GetStream();

            // MSG-LEN 100 は上限 10 超過で本体 100 バイトの読み飛ばしに入り、読み飛ばしが
            // 再同期バイト数上限 20 を超えた時点で切断される。
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"100 {new string('x', 100)}"));
            await stream.FlushAsync();

            await WaitUntilAsync(() => listener.CurrentConnectionCount == 0, TimeSpan.FromSeconds(10));
            await resyncCollector.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(10));
            await closedCollector.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(10));
        }
        finally
        {
            await listener.StopAsync();
        }

        Assert.Equal(1, resyncCollector.GetMeasurementSnapshot().Sum(m => m.Value));
    }

    [Fact]
    public async Task FramingProgressTimeout_TrickleGarbage_DisconnectsAndIncrementsCounter()
    {
        // B: バイトは届き続けているのに有効なメッセージが 1 件も確定しない接続は、
        // フレーミング進捗タイムアウトで切断される（読み取りが起き続けるためアイドル
        // タイムアウトでは回収できない低速トリクルの回収）。
        //
        // Issue #215（PR #212 CI で実フレーク）: 旧構造は実時間（300ms タイムアウト vs
        // 50ms 間隔書き込み）に依存し、さらに送信ループの継続条件が Accept 完了前の
        // CurrentConnectionCount == 0 を観測すると 1 バイトも送らないまま終わる競合が
        // あった。FakeTimeProvider を注入し、タイムアウト経過の判定を仮想時計で決定的に
        // 制御する——実ソケットはデータ搬送にのみ使い、時間はテストだけが Advance で
        // 進める（ランナーの負荷は判定に影響しない）。
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();
        using var framingCollector = new MetricCollector<long>(metrics.TcpConnectionFramingTimeoutCounter, timeProvider: null);
        var timeProvider = new FakeTimeProvider();
        var timeout = TimeSpan.FromMinutes(5); // 実時間では決して経過しない大きさ（仮想時計でのみ進める）。

        var listener = new TcpSyslogListener(
            new TcpSyslogListenerOptions
            {
                BindAddress = "127.0.0.1",
                Port = 0,
                FramingProgressTimeout = timeout,
                IdleTimeout = TimeSpan.Zero, // アイドル側は実時間タイマーのため無効化する。
            },
            q1.Writer,
            new NoopIngressGate(),
            metrics,
            logger: null,
            timeProvider: timeProvider);

        await listener.StartAsync();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.BoundPort);

            // ConnectAsync は OS のハンドシェイク完了（backlog 受理）で返るため、listener 側の
            // Accept 完了（= CurrentConnectionCount 反映）を待ってから送信を始める。
            await WaitUntilAsync(() => listener.CurrentConnectionCount >= 1, TimeSpan.FromSeconds(10));
            var stream = client.GetStream();

            // LF を送らない断片（non-transparent の未完の行）を送っては仮想時計をタイムアウト
            // 超過分だけ進める、を切断まで繰り返す。ガベージは有効なメッセージを確定させない
            // ため基準時刻は一度も取り直されない——ある読み取りが基準時刻を初期化した後は、
            // Advance 済みの時計を見る次の読み取りが必ず超過を検出する（書き込みの合流や
            // 読み取りの遅延がどう転んでも、高々数反復で決定的に切断へ到達する）。
            for (var i = 0; i < 200 && listener.CurrentConnectionCount > 0; i++)
            {
                try
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("<34>x"));
                    await stream.FlushAsync();
                }
                catch (IOException)
                {
                    // 切断済み（期待どおり）。
                    break;
                }

                timeProvider.Advance(timeout + TimeSpan.FromSeconds(1));
                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }

            await WaitUntilAsync(() => listener.CurrentConnectionCount == 0, TimeSpan.FromSeconds(10));
            await framingCollector.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(10));
        }
        finally
        {
            await listener.StopAsync();
        }

        Assert.True(framingCollector.GetMeasurementSnapshot().Sum(m => m.Value) >= 1);
    }

    [Fact]
    public async Task FramingProgressTimeout_ValidMessagesKeepArriving_TimerResetsAndConnectionStaysOpen()
    {
        // B のリセット規則: 有効なメッセージが確定し続ける限り、タイムアウト時間を累計で
        // 超えても切断されない（正常な送信元は巻き込まれない）。
        //
        // Issue #215: 旧構造は実時間（300ms タイムアウト vs 100ms 間隔書き込み）に依存する
        // 負のアサーションで、負荷の高いランナーで書き込み間隔がタイムアウトを超えると
        // 正常系のはずが誤って切断される flaky 素地があった（旧
        // IdleTimeout_PeriodicWrites_KeepsConnectionAlive と同型）。FakeTimeProvider の
        // 仮想時計で「1 回の間隔はタイムアウト未満・累計はタイムアウト超過」を決定的に
        // 構成する。各メッセージの Q1 到達を待ってから時計を進めるため、確定 1 件ごとの
        // 基準時刻の取り直しと時計の進行が競合して誤切断する経路は存在しない
        // （どの読み取り時点でも基準時刻からの経過は高々 Advance 1 回分 < タイムアウト）。
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();
        using var framingCollector = new MetricCollector<long>(metrics.TcpConnectionFramingTimeoutCounter, timeProvider: null);
        var timeProvider = new FakeTimeProvider();
        var timeout = TimeSpan.FromMinutes(5);

        var listener = new TcpSyslogListener(
            new TcpSyslogListenerOptions
            {
                BindAddress = "127.0.0.1",
                Port = 0,
                FramingProgressTimeout = timeout,
                IdleTimeout = TimeSpan.Zero, // アイドル側は実時間タイマーのため無効化する。
            },
            q1.Writer,
            new NoopIngressGate(),
            metrics,
            logger: null,
            timeProvider: timeProvider);

        await listener.StartAsync();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.BoundPort);
            var stream = client.GetStream();

            // 有効なメッセージを「タイムアウト未満の間隔（3 分）」で送り続ける。累計
            // （8 回 × 3 分 = 24 分）はタイムアウト（5 分）を大きく超えるが、確定のたびに
            // 基準時刻が取り直されるため切断されない。Q1 への到達（= 境界確定の完了目印）を
            // 待ってから時計を進める。
            for (var i = 0; i < 8; i++)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes("<34>valid message\n"));
                await stream.FlushAsync();

                var datagram = await ReadWithTimeoutAsync(q1.Reader, TimeSpan.FromSeconds(10));
                Assert.Equal("<34>valid message", Encoding.ASCII.GetString(datagram.Payload));

                timeProvider.Advance(TimeSpan.FromMinutes(3));
            }

            Assert.Equal(1, listener.CurrentConnectionCount);
            Assert.Empty(framingCollector.GetMeasurementSnapshot());
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    // ------------------------------------------------------------------
    // アイドルタイムアウト（architecture.md §4.5。Issue #140）:
    // 無通信接続を有限時間で切断し、同時接続枠を返す。
    // ------------------------------------------------------------------

    [Fact]
    public void RenewIdleReadCancellation_PreviousAlreadyCancelled_ReturnsFreshUncancelledSource()
    {
        // PR #169 レビュー指摘 1 の競合を決定的に再現する: 「タイマーが発火して CTS がキャンセル
        // 済みになった直後に、読み取りがたまたま受信済みデータで正常完了した」状況を、キャンセル
        // 済みの previous として模す。単一 CTS の CancelAfter 再スケジュール方式では
        // 「キャンセル済みソースへの CancelAfter は no-op」という .NET の仕様により、この
        // キャンセル状態が次の読み取りへ持ち越されて活性接続を誤アイドル判定していた。
        // 読み取りごとの CTS 再生成（RenewIdleReadCancellation）は、必ずキャンセル未要求の
        // 新しいソースを返すことで、この持ち越し経路を構造的に消す。
        using var stopping = new CancellationTokenSource();

        var first = TcpSyslogListener.RenewIdleReadCancellation(null, stopping.Token, TimeSpan.FromMinutes(5));
        first.Cancel(); // タイマー発火と読み取り成功の競合を模す。

        var second = TcpSyslogListener.RenewIdleReadCancellation(first, stopping.Token, TimeSpan.FromMinutes(5));
        try
        {
            Assert.NotSame(first, second);
            Assert.False(second.Token.IsCancellationRequested);

            // previous は破棄済みであること（リークしないこと）。
            Assert.Throws<ObjectDisposedException>(() => first.CancelAfter(TimeSpan.Zero));
        }
        finally
        {
            second.Dispose();
        }
    }

    [Fact]
    public void RenewIdleReadCancellation_LinkedToStoppingToken_CancelsWhenListenerStops()
    {
        // 生成されるソースがリスナ停止のトークンへ正しくリンクされていること
        // （停止要求が読み取りの打ち切りとして伝わる従来挙動の維持）。
        using var stopping = new CancellationTokenSource();

        var renewed = TcpSyslogListener.RenewIdleReadCancellation(null, stopping.Token, TimeSpan.FromMinutes(5));
        try
        {
            Assert.False(renewed.Token.IsCancellationRequested);

            stopping.Cancel();

            Assert.True(renewed.Token.IsCancellationRequested);
        }
        finally
        {
            renewed.Dispose();
        }
    }

    [Fact]
    public async Task IdleTimeout_NoDataSent_DisconnectsAndIncrementsCounters()
    {
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();
        using var idleCollector = new MetricCollector<long>(metrics.TcpConnectionIdleTimeoutCounter, timeProvider: null);
        using var closedCollector = new MetricCollector<long>(metrics.TcpConnectionClosedCounter, timeProvider: null);

        var listener = new TcpSyslogListener(
            new TcpSyslogListenerOptions
            {
                BindAddress = "127.0.0.1",
                Port = 0,
                IdleTimeout = TimeSpan.FromMilliseconds(200),
            },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.BoundPort);

            // 何も送らずに放置する——アイドルタイムアウトが発火して切断されるはず。
            await WaitUntilAsync(() => listener.CurrentConnectionCount == 0, TimeSpan.FromSeconds(10));

            await idleCollector.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(10));
            await closedCollector.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(10));
        }
        finally
        {
            await listener.StopAsync();
        }

        Assert.True(idleCollector.GetMeasurementSnapshot().Sum(m => m.Value) >= 1);
        Assert.True(closedCollector.GetMeasurementSnapshot().Sum(m => m.Value) >= 1);
    }

    [Fact]
    public void RenewIdleReadCancellation_CalledRepeatedlyBeforeExpiry_NeverCarriesOverCancellation()
    {
        // 旧: IdleTimeout_PeriodicWrites_KeepsConnectionAlive（PR #169 で追加）。実ソケット +
        // 実時間（IdleTimeout 300ms に対し 100ms 間隔で 5 回書き込み、切断されないことを負の
        // アサーションで確認する構造）だったため、負荷の高いランナーでは書き込み間隔がタイマー
        // 発火の余裕（200ms）を食い潰し、誤ってアイドル判定される flaky の兆候があった
        // （Issue #215）。
        //
        // HandleConnectionAsync が「アイドルタイムアウトより短い間隔で読み取りが成功し続ける
        // 限り接続が維持される」を実現している核心は、読み取りが成功するたびに
        // RenewIdleReadCancellation を呼び直し、キャンセル未要求の新しいソースに置き換える
        // ことにある（前回のソースがまだ発火していなくても、必ず破棄されて持ち越されない）。
        // この検証意図を、実ソケット・実時間を排した決定的な構造でこの関数の単体レベルへ
        // 移した（RenewIdleReadCancellation_PreviousAlreadyCancelled_ReturnsFreshUncancelledSource・
        // RenewIdleReadCancellation_LinkedToStoppingToken_CancelsWhenListenerStops と同系）。
        // IdleTimeout には実行時間内には発火し得ない値（5 分）を使い、タイマーの実発火では
        // なく「renew を繰り返した結果」だけを見る。
        using var stopping = new CancellationTokenSource();
        CancellationTokenSource? current = null;

        try
        {
            for (var i = 0; i < 5; i++)
            {
                var next = TcpSyslogListener.RenewIdleReadCancellation(current, stopping.Token, TimeSpan.FromMinutes(5));

                Assert.NotSame(current, next);
                Assert.False(next.Token.IsCancellationRequested);

                if (current is not null)
                {
                    // 前回のソースは破棄済み（リークしないこと）。
                    Assert.Throws<ObjectDisposedException>(() => current.CancelAfter(TimeSpan.Zero));
                }

                current = next;
            }

            // 5 回 renew を繰り返した末でも、一度もキャンセル状態が持ち越されていない——
            // これが「周期的な読み取り成功が続く限りアイドル判定されない」ことの核心。
            Assert.False(current!.Token.IsCancellationRequested);
        }
        finally
        {
            current?.Dispose();
        }
    }

    [Fact]
    public async Task IdleTimeout_DisabledWhenZero_NeverDisconnectsForIdleness()
    {
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();

        var listener = new TcpSyslogListener(
            new TcpSyslogListenerOptions
            {
                BindAddress = "127.0.0.1",
                Port = 0,
                IdleTimeout = TimeSpan.Zero,
            },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.BoundPort);

            // IdleTimeout = Zero は無効化を意味する——短時間放置しても切断されないはず。
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            Assert.Equal(1, listener.CurrentConnectionCount);
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    // ------------------------------------------------------------------
    // ヘルパー
    // ------------------------------------------------------------------

    private static async Task<RawDatagram> ReadWithTimeoutAsync(ChannelReader<RawDatagram> reader, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await reader.ReadAsync(cts.Token);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        Assert.True(condition(), "条件がタイムアウトまでに満たされなかった。");
    }
}
