using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Tcp;
using Yagura.Ingestion.Udp;
using Yagura.Storage;

namespace Yagura.Ingestion.Tests.Tcp;

/// <summary>
/// 既定 bind（<see cref="TcpSyslogListenerOptions.DefaultBindAddress"/> = <c>::</c>）による
/// IPv4/IPv6 デュアルスタック受信の確認（Issue #133）。
/// </summary>
public sealed class TcpSyslogListenerDualStackTests
{
    private static Channel<RawDatagram> CreateQ1() =>
        Channel.CreateBounded<RawDatagram>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

    [Fact]
    public async Task DefaultBindAddress_AcceptsIPv6LoopbackConnection()
    {
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();

        // BindAddress 未指定 = 既定値（"::"）。DualMode ソケットで bind される。
        var listener = new TcpSyslogListener(
            new TcpSyslogListenerOptions { Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetworkV6);
            await client.ConnectAsync(IPAddress.IPv6Loopback, listener.BoundPort);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("<34>ipv6-tcp-test\n"));
            await stream.FlushAsync();

            var datagram = await ReadWithTimeoutAsync(q1.Reader, TimeSpan.FromSeconds(10));

            Assert.Equal("::1", datagram.SourceAddress);
            Assert.Equal("<34>ipv6-tcp-test", Encoding.ASCII.GetString(datagram.Payload));
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    [Fact]
    public async Task DefaultBindAddress_AcceptsIPv4LoopbackConnectionAndNormalizesSourceAddress()
    {
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();

        var listener = new TcpSyslogListener(
            new TcpSyslogListenerOptions { Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            // 通常の IPv4 TcpClient から接続する——DualMode ソケットは IPv4-mapped IPv6
            // （::ffff:127.0.0.1）として受け取るはずだが、SourceAddress は正規化されて
            // 純粋な IPv4 表記になることを確認する（ADR-0007 決定 2 と同じ規約）。
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.BoundPort);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("<34>ipv4-dualstack-tcp-test\n"));
            await stream.FlushAsync();

            var datagram = await ReadWithTimeoutAsync(q1.Reader, TimeSpan.FromSeconds(10));

            Assert.Equal("127.0.0.1", datagram.SourceAddress);
            Assert.Equal("<34>ipv4-dualstack-tcp-test", Encoding.ASCII.GetString(datagram.Payload));
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    [Fact]
    public async Task ExplicitIPv4WildcardBindAddress_RejectsIPv6Connection()
    {
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();

        // 明示的な 0.0.0.0 指定 = 後方互換の逃げ道。IPv4 のみの単一ソケットで bind される。
        var listener = new TcpSyslogListener(
            new TcpSyslogListenerOptions { BindAddress = "0.0.0.0", Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetworkV6);
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            // IPv4 単独ソケットのポートへ IPv6 で接続しようとすると拒否される
            // （TCP は UDP と異なり "connection refused" を確定的に観測できる）。
            await Assert.ThrowsAsync<SocketException>(async () =>
                await client.ConnectAsync(IPAddress.IPv6Loopback, listener.BoundPort, connectCts.Token));
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    [Fact]
    public async Task ExplicitIPv4WildcardBindAddress_StillAcceptsIPv4Connection()
    {
        var q1 = CreateQ1();
        using var metrics = new IngestionMetrics();

        var listener = new TcpSyslogListener(
            new TcpSyslogListenerOptions { BindAddress = "0.0.0.0", Port = 0 },
            q1.Writer,
            new NoopIngressGate(),
            metrics);

        await listener.StartAsync();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.BoundPort);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("<34>ipv4-backward-compat-tcp-test\n"));
            await stream.FlushAsync();

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
