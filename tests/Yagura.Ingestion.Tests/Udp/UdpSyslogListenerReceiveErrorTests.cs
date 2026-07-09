using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Udp;
using Yagura.Storage;

namespace Yagura.Ingestion.Tests.Udp;

/// <summary>
/// UDP 受信の <see cref="SocketException"/> を無言で握り潰さず、ログ・メトリクス・backoff を
/// 行うことの確認（Issue #142）。
/// </summary>
/// <remarks>
/// 実際の <see cref="SocketException"/> をネットワーク経由で確実に再現するのは環境依存で
/// 難しい（issue が挙げる WSAECONNRESET 系の事象は、対象 OS・タイミングに依存する）ため、
/// <see cref="UdpSyslogListener.HandleReceiveErrorAsync"/>・
/// <see cref="UdpSyslogListener.ComputeReceiveErrorBackoff"/> を internal 公開し
/// （<c>InternalsVisibleTo</c>。Yagura.Ingestion.csproj）、ロジックを直接検証する。
/// </remarks>
public sealed class UdpSyslogListenerReceiveErrorTests
{
    private static UdpSyslogListener CreateListener(
        ILogger<UdpSyslogListener>? logger = null,
        TimeProvider? timeProvider = null)
    {
        var metrics = new IngestionMetrics();
        var q1 = Channel.CreateBounded<RawDatagram>(1);

        return new UdpSyslogListener(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics,
            logger,
            timeProvider);
    }

    private static SocketException CreateSocketException() =>
        new((int)SocketError.ConnectionReset);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    // 2 回目のエラーで最初の backoff = 底値 10ms が適用される（「10ms 起点」の文言と
    // 実際に発生する最小 backoff の一致。PR #163 レビュー指摘 1）。
    [InlineData(2, UdpSyslogListener.ReceiveErrorBackoffBaseMilliseconds)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    [InlineData(5, 80)]
    [InlineData(8, 640)]
    [InlineData(9, UdpSyslogListener.ReceiveErrorBackoffMaxMilliseconds)]
    [InlineData(100, UdpSyslogListener.ReceiveErrorBackoffMaxMilliseconds)]
    [InlineData(int.MaxValue, UdpSyslogListener.ReceiveErrorBackoffMaxMilliseconds)]
    public void ComputeReceiveErrorBackoff_ReturnsExpectedDelay_AndCapsAtMaximum(
        int consecutiveErrorCount,
        int expectedMilliseconds)
    {
        var backoff = UdpSyslogListener.ComputeReceiveErrorBackoff(consecutiveErrorCount);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), backoff);
    }

    [Fact]
    public async Task HandleReceiveErrorAsync_IncrementsUdpReceiveErrorCounter()
    {
        using var metrics = new IngestionMetrics();
        using var meterCollector = new MetricCollector<long>(metrics.UdpReceiveErrorCounter, timeProvider: null);
        var q1 = Channel.CreateBounded<RawDatagram>(1);

        var listener = new UdpSyslogListener(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        var canContinue = await listener.HandleReceiveErrorAsync(CreateSocketException(), CancellationToken.None);

        Assert.True(canContinue);
        await meterCollector.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(5));
        var measurements = meterCollector.GetMeasurementSnapshot();
        Assert.Equal(1, measurements.Sum(m => m.Value));
    }

    [Fact]
    public async Task HandleReceiveErrorAsync_EachCallIncrementsCounterBySeparateOne()
    {
        using var metrics = new IngestionMetrics();
        using var meterCollector = new MetricCollector<long>(metrics.UdpReceiveErrorCounter, timeProvider: null);
        var q1 = Channel.CreateBounded<RawDatagram>(1);

        var listener = new UdpSyslogListener(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        // 3 回発生させる（2・3 回目は短い backoff を挟むが、テストを長時間化させるほどではない）。
        for (var i = 0; i < 3; i++)
        {
            await listener.HandleReceiveErrorAsync(CreateSocketException(), CancellationToken.None);
        }

        await meterCollector.WaitForMeasurementsAsync(minCount: 3, timeout: TimeSpan.FromSeconds(5));
        var measurements = meterCollector.GetMeasurementSnapshot();
        Assert.Equal(3, measurements.Sum(m => m.Value));
    }

    [Fact]
    public async Task HandleReceiveErrorAsync_LogsWithSocketErrorCode()
    {
        var logger = new CapturingLogger<UdpSyslogListener>();
        var listener = CreateListener(logger);

        await listener.HandleReceiveErrorAsync(CreateSocketException(), CancellationToken.None);

        Assert.Single(logger.Messages);
        Assert.Contains("ConnectionReset", logger.Messages[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleReceiveErrorAsync_WithinThrottleWindow_SuppressesRepeatedLogs()
    {
        var logger = new CapturingLogger<UdpSyslogListener>();
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var listener = CreateListener(logger, timeProvider);
        var ex = CreateSocketException();

        // 1 回目: 即座にログ出力される（抑制ウィンドウが開始する）。
        await listener.HandleReceiveErrorAsync(ex, CancellationToken.None);
        Assert.Single(logger.Messages);

        // 2・3 回目: ウィンドウ内（時刻を進めない）のため抑制され、ログは増えない。
        await listener.HandleReceiveErrorAsync(ex, CancellationToken.None);
        await listener.HandleReceiveErrorAsync(ex, CancellationToken.None);
        Assert.Single(logger.Messages);

        // ウィンドウを過ぎたら次の発生で再びログ出力され、抑制した件数が添えられる。
        timeProvider.Advance(UdpSyslogListener.ReceiveErrorLogThrottleWindow + TimeSpan.FromMilliseconds(1));
        await listener.HandleReceiveErrorAsync(ex, CancellationToken.None);

        Assert.Equal(2, logger.Messages.Count);
        Assert.Contains("抑制", logger.Messages[1], StringComparison.Ordinal);
        Assert.Contains("2", logger.Messages[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleReceiveErrorAsync_FirstError_DoesNotBackoff()
    {
        // backoff は Task.Delay(…, _timeProvider, …) 経由で行われ、遅延が発生する場合は
        // TimeProvider.CreateTimer が呼ばれる。タイマー生成回数を数えることで、実時間の
        // 経過計測（揺らぎで不安定）に頼らず「1 回目は backoff しない」ことを決定的に検証する。
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var listener = CreateListener(timeProvider: timeProvider);
        var ex = CreateSocketException();

        var canContinue = await listener.HandleReceiveErrorAsync(ex, CancellationToken.None);

        Assert.True(canContinue);
        Assert.Equal(0, timeProvider.TimersCreated);

        // 対照: 2 回目（連続エラー）では backoff のタイマーが生成される。
        await listener.HandleReceiveErrorAsync(ex, CancellationToken.None);
        Assert.Equal(1, timeProvider.TimersCreated);
    }

    [Fact]
    public async Task ReceiveLoop_SuccessfulReceive_ResetsConsecutiveErrorCount()
    {
        // 「受信が 1 回成立するたびに連続エラー回数がリセットされる」（ReceiveLoopAsync の
        // 成功パス）の検証（PR #163 レビュー指摘 3）。エラー側は HandleReceiveErrorAsync を
        // 直接呼んで連続エラー状態を作り、成功側は実ソケットへの実送信で受信ループを
        // 1 周させる。データグラムが Q1 に到達した時点で、同じループ内で直前に実行される
        // リセット（Volatile.Write）は完了している（単一スレッドの逐次実行 + Channel の
        // 同期化により観測できる）。
        var q1 = Channel.CreateBounded<RawDatagram>(16);
        using var metrics = new IngestionMetrics();

        var listener = new UdpSyslogListener(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        // 連続エラー状態を作る（2 回 = 短い backoff 1 回を挟むのみ）。
        var ex = CreateSocketException();
        await listener.HandleReceiveErrorAsync(ex, CancellationToken.None);
        await listener.HandleReceiveErrorAsync(ex, CancellationToken.None);
        Assert.Equal(2, listener.ConsecutiveReceiveErrors);

        await listener.StartAsync();
        try
        {
            using var sender = new UdpClient();
            var target = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, listener.BoundPort);
            var payload = System.Text.Encoding.UTF8.GetBytes("<34>reset-test");
            await sender.SendAsync(payload, target);

            // 受信成立（Q1 到達）を待つ。
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var received = await q1.Reader.ReadAsync(cts.Token);
            Assert.Equal(Protocol.Udp, received.Protocol);
        }
        finally
        {
            await listener.StopAsync();
        }

        Assert.Equal(0, listener.ConsecutiveReceiveErrors);
    }

    [Fact]
    public async Task HandleReceiveErrorAsync_AtIntMaxValue_DoesNotOverflowAndKeepsBackoff()
    {
        // 連続エラーカウンタの折り返し防止（PR #163 レビュー指摘 2）: int.MaxValue に達したら
        // それ以上インクリメントしない（折り返して負値 → backoff が突然ゼロへ戻ることを防ぐ）。
        // int.MaxValue 回の実呼び出しは不可能なため、private フィールドへ reflection で直接
        // 上限値を注入して境界だけを検証する。
        var listener = CreateListener();
        var field = typeof(UdpSyslogListener).GetField(
            "_consecutiveReceiveErrors",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        field.SetValue(listener, int.MaxValue);

        // backoff（上限 1000ms）を実際に待たせないため、キャンセル済みトークンで即座に戻す。
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var canContinue = await listener.HandleReceiveErrorAsync(CreateSocketException(), cts.Token);

        // 折り返していない（int.MaxValue のまま）こと、および backoff 経路に入っている
        // （= キャンセルで false が返る = backoff がゼロへ戻っていない）ことを確認する。
        Assert.Equal(int.MaxValue, listener.ConsecutiveReceiveErrors);
        Assert.False(canContinue);
    }

    [Fact]
    public async Task HandleReceiveErrorAsync_CancelledDuringBackoff_ReturnsFalse()
    {
        var listener = CreateListener();
        var ex = CreateSocketException();

        // 1 回目は backoff しない（consecutiveErrorCount == 1）。
        Assert.True(await listener.HandleReceiveErrorAsync(ex, CancellationToken.None));

        // 2 回目は backoff が発生する（consecutiveErrorCount == 2）。事前にキャンセル済みの
        // トークンを渡すと、待機せず false（= 呼び出し元は停止経路として扱う）を返すはず。
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var canContinue = await listener.HandleReceiveErrorAsync(ex, cts.Token);

        Assert.False(canContinue);
    }

    /// <summary>
    /// 依存パッケージを増やさず、ログメッセージのみを捕捉する最小限の <see cref="ILogger{T}"/> 実装
    /// （<c>UdpSyslogListenerReceiveBufferTests.CapturingLogger&lt;T&gt;</c> と同じ実装意図）。
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages => _messages;

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _messages.Add(formatter(state, exception));
        }
    }

    /// <summary>
    /// テストから時刻を明示的に進められる最小限の <see cref="TimeProvider"/> 実装
    /// （ログ抑制ウィンドウの境界を、実時間待機なしで決定的に検証するため）。
    /// あわせてタイマー生成回数（= backoff の発生回数）を数える——タイマー自体の発火は
    /// 実時間（基底実装）に委ねるため、backoff を伴うテストがハングすることはない。
    /// </summary>
    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public int TimersCreated { get; private set; }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TimersCreated++;
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }
}
