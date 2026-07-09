using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Udp;
using Yagura.Storage;

namespace Yagura.Ingestion.Tests.Udp;

/// <summary>
/// 既定 bind（<see cref="UdpSyslogListenerOptions.DefaultBindAddress"/> = <c>::</c>）による
/// IPv4/IPv6 デュアルスタック受信の確認（Issue #133）。
/// </summary>
public sealed class UdpSyslogListenerDualStackTests
{
    private static Channel<RawDatagram> CreateQ1() =>
        Channel.CreateBounded<RawDatagram>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

    [Fact]
    public async Task DefaultBindAddress_ReceivesFromIPv6Loopback()
    {
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();

        // BindAddress 未指定 = 既定値（"::"）。DualMode ソケットで bind される。
        var listener = new UdpSyslogListener(
            new UdpSyslogListenerOptions { Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            using var sender = new UdpClient(AddressFamily.InterNetworkV6);
            var target = new IPEndPoint(IPAddress.IPv6Loopback, listener.BoundPort);
            var payload = Encoding.ASCII.GetBytes("<34>ipv6-loopback-test");
            await sender.SendAsync(payload, target);

            var datagram = await ReadWithTimeoutAsync(q1.Reader, TimeSpan.FromSeconds(10));

            Assert.Equal("::1", datagram.SourceAddress);
            Assert.Equal("<34>ipv6-loopback-test", Encoding.ASCII.GetString(datagram.Payload));
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    [Fact]
    public async Task DefaultBindAddress_ReceivesFromIPv4AndNormalizesSourceAddress()
    {
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();

        var listener = new UdpSyslogListener(
            new UdpSyslogListenerOptions { Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            // 通常の IPv4 UdpClient から送る——DualMode ソケットは IPv4-mapped IPv6
            // （::ffff:127.0.0.1）として受け取るはずだが、SourceAddress は正規化されて
            // 純粋な IPv4 表記になることを確認する（ADR-0007 決定 2 と同じ規約）。
            using var sender = new UdpClient();
            var target = new IPEndPoint(IPAddress.Loopback, listener.BoundPort);
            var payload = Encoding.ASCII.GetBytes("<34>ipv4-dualstack-test");
            await sender.SendAsync(payload, target);

            var datagram = await ReadWithTimeoutAsync(q1.Reader, TimeSpan.FromSeconds(10));

            Assert.Equal("127.0.0.1", datagram.SourceAddress);
            Assert.Equal("<34>ipv4-dualstack-test", Encoding.ASCII.GetString(datagram.Payload));
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    [Fact]
    public async Task ExplicitIPv4WildcardBindAddress_DoesNotReceiveFromIPv6()
    {
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();

        // 明示的な 0.0.0.0 指定 = 後方互換の逃げ道。IPv4 のみの単一ソケットで bind される。
        var listener = new UdpSyslogListener(
            new UdpSyslogListenerOptions { BindAddress = "0.0.0.0", Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            using var sender = new UdpClient(AddressFamily.InterNetworkV6);
            var target = new IPEndPoint(IPAddress.IPv6Loopback, listener.BoundPort);
            var payload = Encoding.ASCII.GetBytes("<34>should-not-arrive");

            // IPv6 送信先にリスナが存在しない（IPv4 単独ソケット）ため、送信自体は
            // 成功し得るが Q1 には何も届かないはずである。
            try
            {
                await sender.SendAsync(payload, target);
            }
            catch (SocketException)
            {
                // 環境によっては即座に到達不能が返る場合もある——いずれにせよ受信しないことの
                // 確認が本テストの主眼のため、送信側の例外は無視してよい。
            }

            using var readCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await q1.Reader.ReadAsync(readCts.Token));
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    [Fact]
    public async Task ExplicitIPv4WildcardBindAddress_StillReceivesFromIPv4()
    {
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();

        var listener = new UdpSyslogListener(
            new UdpSyslogListenerOptions { BindAddress = "0.0.0.0", Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            using var sender = new UdpClient();
            var target = new IPEndPoint(IPAddress.Loopback, listener.BoundPort);
            var payload = Encoding.ASCII.GetBytes("<34>ipv4-backward-compat-test");
            await sender.SendAsync(payload, target);

            var datagram = await ReadWithTimeoutAsync(q1.Reader, TimeSpan.FromSeconds(10));

            Assert.Equal("127.0.0.1", datagram.SourceAddress);
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    private static async Task<RawDatagram> ReadWithTimeoutAsync(ChannelReader<RawDatagram> reader, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await reader.ReadAsync(cts.Token);
    }
}
