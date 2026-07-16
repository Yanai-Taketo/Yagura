using System.Net;
using System.Net.Sockets;
using System.Text;
using Yagura.Ingestion.Tcp;
using Yagura.Ingestion.Udp;
using Yagura.Storage;
using Yagura.Storage.Sqlite;

namespace Yagura.Ingestion.Tests;

/// <summary>
/// 無瞬断リスナ再構成（CF-4 層2。Issue #262）の結合テスト。実ソケットで
/// 「差分適用（変更なしは触れない）・新ポートでの受信再開・失敗時の旧構成復旧・瞬断区間の報告」
/// を固定する。
/// </summary>
public sealed class ListenerReconfigurationTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"yagura-reconfig-tests-{Guid.NewGuid():N}.db");
    private SqliteLogStore _logStore = null!;

    public async Task InitializeAsync()
    {
        _logStore = new SqliteLogStore(_databasePath);
        await _logStore.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _logStore.DisposeAsync();
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static int GetFreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static async Task<T> PollUntilAsync<T>(Func<Task<T?>> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe() is { } value)
            {
                return value;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("ポーリングがタイムアウトした。");
    }

    [Fact]
    public async Task Reconfigure_UdpToNewPort_ResumesReceivingOnNewPort_AndReportsGap()
    {
        await using var pipeline = new IngestionPipeline(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            _logStore);
        await pipeline.StartListenerAsync();
        pipeline.StartConsumers();

        var newPort = GetFreeUdpPort();
        var result = await pipeline.ReconfigureListenersAsync(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = newPort },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 });

        // TCP は options 不変のため触れられない（差分適用）。UDP は新ポートで再開し瞬断区間を報告する。
        Assert.Equal(ListenerReconfigurationStatus.NotChanged, result.Tcp.Status);
        Assert.Equal(ListenerReconfigurationStatus.Reconfigured, result.Udp.Status);
        Assert.NotNull(result.Udp.GapStartedAt);
        Assert.NotNull(result.Udp.GapEndedAt);
        Assert.True(result.Udp.GapStartedAt <= result.Udp.GapEndedAt);
        Assert.Equal(newPort, pipeline.BoundPort);

        // 新ポートでの受信 → 永続化が生きていること（Q1・解析段・永続化段は共有のまま継続）。
        using var sender = new UdpClient();
        var message = $"reconfig-test-{Guid.NewGuid():N}";
        await sender.SendAsync(Encoding.UTF8.GetBytes($"<34>{message}"), new IPEndPoint(IPAddress.Loopback, newPort));

        var found = await PollUntilAsync(
            async () => (await _logStore.QueryLatestAsync(limit: 50, timeout: TimeSpan.FromSeconds(5)))
                .FirstOrDefault(r => r.Message == message),
            TimeSpan.FromSeconds(10));

        await pipeline.StopAsync();
        Assert.NotNull(found);
    }

    [Fact]
    public async Task Reconfigure_SameOptions_DoesNotTouchListeners()
    {
        await using var pipeline = new IngestionPipeline(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            _logStore);
        await pipeline.StartListenerAsync();
        var boundBefore = pipeline.BoundPort;

        var result = await pipeline.ReconfigureListenersAsync(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 });

        // options が一致する限りリスナは触れられない（bind したままポートも変わらない）。
        Assert.Equal(ListenerReconfigurationStatus.NotChanged, result.Udp.Status);
        Assert.Equal(ListenerReconfigurationStatus.NotChanged, result.Tcp.Status);
        Assert.Equal(boundBefore, pipeline.BoundPort);

        await pipeline.StopAsync();
    }

    [Fact]
    public async Task Reconfigure_NewPortOccupied_RollsBackToOldConfiguration_AndKeepsReceiving()
    {
        await using var pipeline = new IngestionPipeline(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            _logStore);
        await pipeline.StartListenerAsync();
        pipeline.StartConsumers();

        // 他プロセス相当のソケットで新ポートを占有しておく → 新構成の bind は失敗する。
        using var occupant = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var occupiedPort = ((IPEndPoint)occupant.Client.LocalEndPoint!).Port;

        var result = await pipeline.ReconfigureListenersAsync(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = occupiedPort },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 });

        // 旧構成（Port=0 = OS 採番）で復旧し、受信は継続する（configuration.md §3——旧構成の維持）。
        Assert.Equal(ListenerReconfigurationStatus.RolledBack, result.Udp.Status);
        Assert.NotNull(result.Udp.Error);
        Assert.NotNull(result.Udp.GapEndedAt);
        Assert.NotEqual(occupiedPort, pipeline.BoundPort);

        using var sender = new UdpClient();
        var message = $"rollback-test-{Guid.NewGuid():N}";
        await sender.SendAsync(
            Encoding.UTF8.GetBytes($"<34>{message}"), new IPEndPoint(IPAddress.Loopback, pipeline.BoundPort));

        var found = await PollUntilAsync(
            async () => (await _logStore.QueryLatestAsync(limit: 50, timeout: TimeSpan.FromSeconds(5)))
                .FirstOrDefault(r => r.Message == message),
            TimeSpan.FromSeconds(10));

        await pipeline.StopAsync();
        Assert.NotNull(found);
    }

    [Fact]
    public async Task Reconfigure_TcpToNewPort_NewConnectionsArriveOnNewPort()
    {
        await using var pipeline = new IngestionPipeline(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            _logStore);
        await pipeline.StartListenerAsync();
        pipeline.StartConsumers();

        var newPort = GetFreeUdpPort();
        var result = await pipeline.ReconfigureListenersAsync(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = newPort });

        Assert.Equal(ListenerReconfigurationStatus.Reconfigured, result.Tcp.Status);
        Assert.Equal(ListenerReconfigurationStatus.NotChanged, result.Udp.Status);
        Assert.Equal(newPort, pipeline.TcpBoundPort);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, newPort);
        var message = $"tcp-reconfig-test-{Guid.NewGuid():N}";
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"<34>{message}\n"));
        await stream.FlushAsync();

        var found = await PollUntilAsync(
            async () => (await _logStore.QueryLatestAsync(limit: 50, timeout: TimeSpan.FromSeconds(5)))
                .FirstOrDefault(r => r.Message == message),
            TimeSpan.FromSeconds(10));

        await pipeline.StopAsync();
        Assert.NotNull(found);
    }
}
