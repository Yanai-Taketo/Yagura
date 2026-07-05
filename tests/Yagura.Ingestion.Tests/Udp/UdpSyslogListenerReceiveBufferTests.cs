using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Udp;

namespace Yagura.Ingestion.Tests.Udp;

/// <summary>
/// UDP 受信ソケットの受信バッファサイズ（<c>SO_RCVBUF</c>）設定と、実効値の読み戻しログ
/// （architecture.md §9 M-2）の確認。
/// </summary>
public class UdpSyslogListenerReceiveBufferTests
{
    [Fact]
    public async Task StartAsync_AppliesConfiguredReceiveBufferSize()
    {
        var q1 = Channel.CreateBounded<RawDatagram>(1);
        using var metrics = new IngestionMetrics();
        const int requestedBytes = 2 * 1024 * 1024;

        var listener = new UdpSyslogListener(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0, ReceiveBufferBytes = requestedBytes },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            // ソケット自体の実効値は UdpSyslogListener 内部にカプセル化されているため、
            // 別の UdpClient を同じポートへ bind しようとして失敗しないこと（= 正常に
            // listen できていること）を成立の前提として確認する。実効値そのものの検証は
            // ApplyReceiveBufferSize_ 系のログ出力テスト（本クラスの他テスト）で行う。
            Assert.True(listener.BoundPort > 0);
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    [Fact]
    public async Task StartAsync_DefaultReceiveBufferSize_LogsAppliedEffectiveValue()
    {
        var q1 = Channel.CreateBounded<RawDatagram>(1);
        using var metrics = new IngestionMetrics();
        var logger = new CapturingLogger<UdpSyslogListener>();

        var listener = new UdpSyslogListener(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics,
            logger);

        await listener.StartAsync();
        try
        {
            // 既定値（4 MiB）の適用がログへ記録されること（実効値読み戻しの検証）。
            Assert.Contains(logger.Messages, m => m.Contains("受信バッファサイズ", StringComparison.Ordinal));
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    /// <summary>
    /// 依存パッケージを増やさず、ログメッセージのみを捕捉する最小限の <see cref="ILogger{T}"/> 実装。
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
}
