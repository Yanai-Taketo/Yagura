using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Tcp;
using Yagura.Ingestion.Udp;
using Yagura.Storage;

namespace Yagura.Ingestion.Tests.FlowControlTests;

/// <summary>
/// 流量制御破棄カウンタの計上枠（architecture.md §3.1・§4.1「流量制御破棄」。M4-4）。
/// 「発火は必ず計測される」（§3.3）という挿入点の契約を、拒否する <see cref="IIngressGate"/>
/// フェイクで検証する（UDP・TCP の両受信段から到達できることを確認する）。判定ロジック自体の
/// 検証は <see cref="Yagura.Ingestion.Tests.FlowControlTests.TokenBucketIngressGateTests"/>
/// （Issue #260）が担う——本テストはゲートの実装によらない挿入点側の契約に限定する。
/// </summary>
public sealed class FlowControlDroppedCounterTests
{
    /// <summary>すべてのデータグラムを拒否するテスト用ゲート。</summary>
    private sealed class RejectAllIngressGate : IIngressGate
    {
        public bool ShouldAdmit(IPAddress sourceAddress, ReadOnlySpan<byte> payload) => false;
    }

    [Fact]
    public async Task Udp_GateRejects_IncrementsFlowControlDroppedCounter_AndDoesNotReachQ1()
    {
        var q1 = Channel.CreateBounded<RawDatagram>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var metrics = new IngestionMetrics();
        using var meterCollector = new MetricCollector<long>(metrics.FlowControlDroppedCounter, timeProvider: null);

        var listener = new UdpSyslogListener(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            q1.Writer,
            new RejectAllIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            using var sender = new UdpClient();
            var target = new IPEndPoint(IPAddress.Loopback, listener.BoundPort);
            var payload = Encoding.UTF8.GetBytes("<34>flow-control-reject-test");
            await sender.SendAsync(payload, target);

            await meterCollector.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(10));
        }
        finally
        {
            await listener.StopAsync();
        }

        var measurements = meterCollector.GetMeasurementSnapshot();
        Assert.True(measurements.Sum(m => m.Value) >= 1);
        Assert.False(q1.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Tcp_GateRejects_IncrementsFlowControlDroppedCounter_AndDoesNotReachQ1()
    {
        var q1 = Channel.CreateBounded<RawDatagram>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var metrics = new IngestionMetrics();
        using var meterCollector = new MetricCollector<long>(metrics.FlowControlDroppedCounter, timeProvider: null);

        var listener = new TcpSyslogListener(
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            q1.Writer,
            new RejectAllIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.BoundPort);

            var stream = client.GetStream();
            var message = Encoding.ASCII.GetBytes("<34>flow-control-reject-test\n");
            await stream.WriteAsync(message);
            await stream.FlushAsync();

            await meterCollector.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(10));
        }
        finally
        {
            await listener.StopAsync();
        }

        var measurements = meterCollector.GetMeasurementSnapshot();
        Assert.True(measurements.Sum(m => m.Value) >= 1);
        Assert.False(q1.Reader.TryRead(out _));
    }
}
