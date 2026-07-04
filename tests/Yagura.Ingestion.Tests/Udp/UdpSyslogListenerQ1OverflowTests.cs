using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Udp;

namespace Yagura.Ingestion.Tests.Udp;

/// <summary>
/// Q1（受信段 → 解析段、UDP 由来）が満杯のときに破棄され、内部バッファ破棄カウンタが
/// 増えることの確認（architecture.md §3.1）。
/// </summary>
public class UdpSyslogListenerQ1OverflowTests
{
    [Fact]
    public async Task Q1Overflow_DropsDatagramAndIncrementsInternalBufferDroppedCounter()
    {
        // Q1 の容量を 1 に絞り、読み手を止めた状態で複数件送ることで確実に溢れさせる。
        var q1 = Channel.CreateBounded<RawDatagram>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var metrics = new IngestionMetrics();
        using var meterCollector = new MetricCollector<long>(metrics.InternalBufferDroppedCounter, timeProvider: null);

        var listener = new UdpSyslogListener(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            using var sender = new UdpClient();
            var target = new IPEndPoint(IPAddress.Loopback, listener.BoundPort);

            // Q1 の読み手がいない状態で複数件送る。容量 1 のため、2 件目以降は溢れて破棄されるはず。
            const int datagramCount = 20;
            for (var i = 0; i < datagramCount; i++)
            {
                var payload = System.Text.Encoding.UTF8.GetBytes($"<34>overflow-test-{i}");
                await sender.SendAsync(payload, target);
            }

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
}
