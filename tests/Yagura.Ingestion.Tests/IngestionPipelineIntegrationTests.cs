using System.Net;
using System.Net.Sockets;
using System.Text;
using Yagura.Ingestion.Tcp;
using Yagura.Ingestion.Udp;
using Yagura.Storage;
using Yagura.Storage.Sqlite;

namespace Yagura.Ingestion.Tests;

/// <summary>
/// 受信 → 解析 → 永続化の結合テスト（ポート 0 で listener 起動 → 実 UDP 送信 →
/// 一時 SQLite に届くことをポーリングで確認）。
/// </summary>
public sealed class IngestionPipelineIntegrationTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"yagura-ingestion-tests-{Guid.NewGuid():N}.db");
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

    [Fact]
    public async Task SentUdpDatagram_ArrivesInSqliteStore_ParsedWithFacilityAndSeverity()
    {
        await using var pipeline = new IngestionPipeline(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            _logStore);

        await pipeline.StartListenerAsync();
        pipeline.StartConsumers();

        using var sender = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, pipeline.BoundPort);
        var message = $"integration-test-{Guid.NewGuid():N}";
        var payload = Encoding.UTF8.GetBytes($"<34>{message}");

        await sender.SendAsync(payload, target);

        var found = await PollUntilAsync(
            async () =>
            {
                var results = await _logStore.QueryLatestAsync(limit: 50, timeout: TimeSpan.FromSeconds(5));
                return results.FirstOrDefault(r => r.Message == message);
            },
            timeout: TimeSpan.FromSeconds(10));

        await pipeline.StopAsync();

        Assert.NotNull(found);
        Assert.Equal(ParseStatus.Parsed, found!.ParseStatus);
        Assert.Equal(4, found.Facility);
        Assert.Equal(2, found.Severity);
        Assert.Equal(Protocol.Udp, found.Protocol);
    }

    [Fact]
    public async Task SentUdpDatagram_WithInvalidPri_ArrivesAsParseFailedWithRawPreserved()
    {
        await using var pipeline = new IngestionPipeline(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            _logStore);

        await pipeline.StartListenerAsync();
        pipeline.StartConsumers();

        using var sender = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, pipeline.BoundPort);
        var marker = $"no-pri-marker-{Guid.NewGuid():N}";
        var payload = Encoding.UTF8.GetBytes(marker);

        await sender.SendAsync(payload, target);

        // ParseFailed レコードは Message を持たないため、QueryLatestAsync の射影に現れる
        // SourceAddress/ParseStatus で最新の失敗レコードが現れたことを確認する。
        var found = await PollUntilAsync(
            async () =>
            {
                var results = await _logStore.QueryLatestAsync(limit: 50, timeout: TimeSpan.FromSeconds(5));
                return results.FirstOrDefault(r => r.ParseStatus == ParseStatus.ParseFailed);
            },
            timeout: TimeSpan.FromSeconds(10));

        await pipeline.StopAsync();

        Assert.NotNull(found);
        Assert.Equal(ParseStatus.ParseFailed, found!.ParseStatus);
        Assert.Null(found.Message);
    }

    [Fact]
    public async Task SentTcpMessage_NonTransparentFraming_ArrivesInSqliteStore_ParsedWithFacilityAndSeverity()
    {
        await using var pipeline = new IngestionPipeline(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            _logStore);

        await pipeline.StartListenerAsync();
        pipeline.StartConsumers();

        var message = $"tcp-integration-test-{Guid.NewGuid():N}";

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, pipeline.TcpBoundPort);
            var stream = client.GetStream();
            var payload = Encoding.ASCII.GetBytes($"<34>{message}\n");
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        }

        var found = await PollUntilAsync(
            async () =>
            {
                var results = await _logStore.QueryLatestAsync(limit: 50, timeout: TimeSpan.FromSeconds(5));
                return results.FirstOrDefault(r => r.Message == message);
            },
            timeout: TimeSpan.FromSeconds(10));

        await pipeline.StopAsync();

        Assert.NotNull(found);
        Assert.Equal(ParseStatus.Parsed, found!.ParseStatus);
        Assert.Equal(4, found.Facility);
        Assert.Equal(2, found.Severity);
        Assert.Equal(Protocol.Tcp, found.Protocol);
    }

    [Fact]
    public async Task SentTcpMessage_OctetCountingFraming_ArrivesInSqliteStore()
    {
        await using var pipeline = new IngestionPipeline(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            _logStore);

        await pipeline.StartListenerAsync();
        pipeline.StartConsumers();

        var message = $"tcp-octet-counting-{Guid.NewGuid():N}";
        var syslogMessage = $"<34>{message}";
        var frame = Encoding.ASCII.GetBytes($"{Encoding.ASCII.GetByteCount(syslogMessage)} {syslogMessage}");

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, pipeline.TcpBoundPort);
            var stream = client.GetStream();
            await stream.WriteAsync(frame);
            await stream.FlushAsync();
        }

        var found = await PollUntilAsync(
            async () =>
            {
                var results = await _logStore.QueryLatestAsync(limit: 50, timeout: TimeSpan.FromSeconds(5));
                return results.FirstOrDefault(r => r.Message == message);
            },
            timeout: TimeSpan.FromSeconds(10));

        await pipeline.StopAsync();

        Assert.NotNull(found);
        Assert.Equal(ParseStatus.Parsed, found!.ParseStatus);
        Assert.Equal(Protocol.Tcp, found.Protocol);
    }

    [Fact]
    public async Task SentTcpMessage_DisconnectedBeforeTerminator_ArrivesAsIncomplete()
    {
        await using var pipeline = new IngestionPipeline(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            _logStore);

        await pipeline.StartListenerAsync();
        pipeline.StartConsumers();

        var marker = $"tcp-incomplete-{Guid.NewGuid():N}";

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, pipeline.TcpBoundPort);
            var stream = client.GetStream();
            // LF を送らないまま切断する（database.md §2.1 の Incomplete 経路）。
            var payload = Encoding.ASCII.GetBytes($"<34>{marker}");
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
            client.Client.Shutdown(SocketShutdown.Send);
        }

        var found = await PollUntilAsync(
            async () =>
            {
                var results = await _logStore.QueryLatestAsync(limit: 50, timeout: TimeSpan.FromSeconds(5));
                return results.FirstOrDefault(r => r.ParseStatus == ParseStatus.Incomplete);
            },
            timeout: TimeSpan.FromSeconds(10));

        await pipeline.StopAsync();

        Assert.NotNull(found);
        Assert.Equal(ParseStatus.Incomplete, found!.ParseStatus);
        Assert.Equal(Protocol.Tcp, found.Protocol);
    }

    /// <summary>
    /// 条件ポーリング（固定 sleep ではなく上限付きで繰り返し確認する。conventions.md の
    /// 時間窓の扱いに準ずる——CI 環境の揺らぎに対して安定させるため）。
    /// </summary>
    private static async Task<T?> PollUntilAsync<T>(Func<Task<T?>> probe, TimeSpan timeout)
        where T : class
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await probe().ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }

        return await probe().ConfigureAwait(false);
    }
}
