using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Udp;

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
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(4, 80)]
    [InlineData(9, UdpSyslogListener.ReceiveErrorBackoffMaxMilliseconds)]
    [InlineData(100, UdpSyslogListener.ReceiveErrorBackoffMaxMilliseconds)]
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
        var listener = CreateListener();

        var started = DateTimeOffset.UtcNow;
        var canContinue = await listener.HandleReceiveErrorAsync(CreateSocketException(), CancellationToken.None);
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.True(canContinue);
        // 単発エラーは backoff しない（生成的な遅延の混入を許容する余裕を持たせつつ、
        // 2 回目以降の backoff 下限 20ms は明確に下回ることを確認する）。
        Assert.True(elapsed < TimeSpan.FromMilliseconds(20), $"elapsed={elapsed}");
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
    /// </summary>
    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }
}
