using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Yagura.Ingestion.FlowControl;
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
    /// Issue #141: TCP bind 失敗時に、起動済みの UDP リスナがロールバックされずに
    /// 取り残される非原子的起動の回帰テスト。TCP ポートを事前に占有して bind を失敗させ、
    /// (1) 例外が伝播すること (2) 既に起動していた UDP ソケットが確実に解放される
    /// （＝同じポートへの再 bind が成功する）ことを確認する。
    /// </summary>
    [Fact]
    public async Task StartListenerAsync_WhenTcpBindFails_StopsAlreadyStartedUdpListener()
    {
        // TCP ポートを先に占有し、パイプライン側の TCP bind を確実に失敗させる。
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var occupiedTcpPort = ((IPEndPoint)portReservation.LocalEndpoint).Port;

        await using var pipeline = new IngestionPipeline(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = occupiedTcpPort },
            _logStore);

        await Assert.ThrowsAsync<SocketException>(() => pipeline.StartListenerAsync());

        var udpPort = pipeline.BoundPort;

        // ロールバックにより UDP ソケットが解放されていれば、同じポートへの再 bind が
        // 例外なく成功する（解放されていなければ AddressAlreadyInUse で失敗する）。
        using var rebind = new UdpClient(new IPEndPoint(IPAddress.Loopback, udpPort));

        portReservation.Stop();
    }

    /// <summary>
    /// Issue #141 の裏側のケース: 先頭（UDP）の bind そのものが失敗する部分失敗。
    /// このときロールバック対象は「まだ何も起動していない」ため、TCP リスナは一度も
    /// 起動されないこと（BoundPort が既定値のまま）を確認する。
    /// </summary>
    [Fact]
    public async Task StartListenerAsync_WhenUdpBindFails_NeverStartsTcpListener()
    {
        // UDP ポートを先に占有し、パイプライン側の UDP bind を最初の一歩で失敗させる。
        using var portReservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var occupiedUdpPort = ((IPEndPoint)portReservation.Client.LocalEndPoint!).Port;

        await using var pipeline = new IngestionPipeline(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = occupiedUdpPort },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            _logStore);

        await Assert.ThrowsAsync<SocketException>(() => pipeline.StartListenerAsync());

        // TCP は一度も起動されていない（UDP が最初の一歩で失敗し、ロールバック対象すら無い）。
        Assert.Equal(0, pipeline.TcpBoundPort);
    }

    /// <summary>
    /// Issue #141: 起動失敗→ロールバック時に、失敗の事実が Error レベルでログに記録される
    /// ことの検証（レビュー指摘 2・4 への対応——ログ出力自体をテストで確認する）。
    /// </summary>
    [Fact]
    public async Task StartListenerAsync_WhenTcpBindFails_LogsRollbackAsError()
    {
        // TCP ポートを先に占有し、パイプライン側の TCP bind を確実に失敗させる。
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var occupiedTcpPort = ((IPEndPoint)portReservation.LocalEndpoint).Port;

        var collector = new FakeLogCollector();
        using var loggerFactory = new LoggerFactory(new[] { new FakeLoggerProvider(collector) });

        await using var pipeline = new IngestionPipeline(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = occupiedTcpPort },
            _logStore,
            new NoopIngressGate(),
            loggerFactory);

        await Assert.ThrowsAsync<SocketException>(() => pipeline.StartListenerAsync());

        // ロールバックの Error ログが 1 件だけ出て、起動済み件数（UDP の 1 件）と
        // 元の bind 失敗例外がそのまま記録されていることを確認する。
        var record = Assert.Single(
            collector.GetSnapshot(),
            r => r.Category == typeof(IngestionPipeline).FullName && r.Level == LogLevel.Error);
        Assert.Contains("起動済みのリスナ 1 件を停止", record.Message);
        Assert.IsAssignableFrom<SocketException>(record.Exception);

        portReservation.Stop();
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
