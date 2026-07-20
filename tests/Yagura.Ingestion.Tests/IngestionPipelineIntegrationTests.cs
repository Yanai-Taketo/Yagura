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
    /// Issue #291（#141 原子的起動の反転。2026-07-16 オーナー裁定）: TCP の bind が環境要因
    /// （ポート競合 = SocketException）で失敗しても、起動は失敗せず UDP のみで縮小継続する。
    /// TCP は DegradedRetrying として報告され、UDP は受信を継続する。
    /// </summary>
    [Fact]
    public async Task StartListenerAsync_WhenTcpBindFails_ContinuesDegradedWithUdp()
    {
        // TCP ポートを先に占有し、パイプライン側の TCP bind を確実に失敗させる。
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var occupiedTcpPort = ((IPEndPoint)portReservation.LocalEndpoint).Port;

        await using var pipeline = new IngestionPipeline(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = occupiedTcpPort },
            _logStore);

        var result = await pipeline.StartListenerAsync();
        pipeline.StartConsumers();

        Assert.Equal(ListenerStartupStatus.Started, result.Udp.Status);
        Assert.Equal(ListenerStartupStatus.DegradedRetrying, result.Tcp.Status);
        Assert.NotNull(result.Tcp.Error);
        Assert.True(result.IsDegraded);

        // UDP は縮小継続中も受信 → 永続化が生きている。
        using var sender = new UdpClient();
        var message = $"degraded-start-test-{Guid.NewGuid():N}";
        await sender.SendAsync(
            Encoding.UTF8.GetBytes($"<34>{message}"), new IPEndPoint(IPAddress.Loopback, pipeline.BoundPort));

        var found = await PollUntilAsync(
            async () => (await _logStore.QueryLatestAsync(limit: 50, timeout: TimeSpan.FromSeconds(5)))
                .FirstOrDefault(r => r.Message == message),
            TimeSpan.FromSeconds(10));

        await pipeline.StopAsync();
        Assert.NotNull(found);
        portReservation.Stop();
    }

    /// <summary>
    /// Issue #291: 占有が解消されると CF-6 の定期再試行が bind に成功し、受信を再開して
    /// <see cref="IngestionPipeline.ListenerBindRecovered"/> が発火する（受信断区間の入力）。
    /// </summary>
    [Fact]
    public async Task StartListenerAsync_DegradedTcp_RecoversViaBindRetry_AndRaisesRecoveryEvent()
    {
        var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var occupiedTcpPort = ((IPEndPoint)portReservation.LocalEndpoint).Port;

        await using var pipeline = new IngestionPipeline(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = occupiedTcpPort },
            _logStore);
        pipeline.BindRetryInterval = TimeSpan.FromMilliseconds(200);

        var recoveryTcs = new TaskCompletionSource<ListenerBindRecovery>(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.ListenerBindRecovered += recovery => recoveryTcs.TrySetResult(recovery);

        var result = await pipeline.StartListenerAsync();
        Assert.Equal(ListenerStartupStatus.DegradedRetrying, result.Tcp.Status);

        // 占有を解消 → 再試行が成功して受信再開の通知が発火する。
        portReservation.Stop();

        var completed = await Task.WhenAny(recoveryTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(recoveryTcs.Task, completed);

        var recovery = await recoveryTcs.Task;
        Assert.Equal("TCP", recovery.ProtocolLabel);
        Assert.True(recovery.GapStartedAt <= recovery.RecoveredAt);
        Assert.Equal(occupiedTcpPort, pipeline.TcpBoundPort);

        await pipeline.StopAsync();
    }

    /// <summary>
    /// Issue #291: UDP・TCP の両方が開けない（全リスナ縮小）場合でも起動は継続する
    /// （configuration.md §4.1「全リスナが開けない場合を含めて縮小継続」——管理 UI からの
    /// 復旧動線を残すため、受信ゼロでもプロセスは立つ）。
    /// </summary>
    [Fact]
    public async Task StartListenerAsync_WhenAllListenersFail_StillStartsDegraded()
    {
        using var udpReservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var occupiedUdpPort = ((IPEndPoint)udpReservation.Client.LocalEndPoint!).Port;
        using var tcpReservation = new TcpListener(IPAddress.Loopback, 0);
        tcpReservation.Start();
        var occupiedTcpPort = ((IPEndPoint)tcpReservation.LocalEndpoint).Port;

        await using var pipeline = new IngestionPipeline(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = occupiedUdpPort },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = occupiedTcpPort },
            _logStore);

        var result = await pipeline.StartListenerAsync();

        Assert.Equal(ListenerStartupStatus.DegradedRetrying, result.Udp.Status);
        Assert.Equal(ListenerStartupStatus.DegradedRetrying, result.Tcp.Status);

        await pipeline.StopAsync();
        tcpReservation.Stop();
    }

    /// <summary>
    /// ADR-0018 委任 6（Issue #351）: リスナ受信可否の状態面。起動前はすべて受信不能として
    /// 返り、起動成功で受信可能へ畳まれる（TLS 未構成は判定に数えない）。
    /// </summary>
    [Fact]
    public async Task ListenerAvailability_ReflectsStartupOutcomes()
    {
        await using var pipeline = new IngestionPipeline(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            _logStore);

        Assert.True(pipeline.ListenerAvailability.AllListenersDown);

        await pipeline.StartListenerAsync();

        var availability = pipeline.ListenerAvailability;
        Assert.True(availability.Udp);
        Assert.True(availability.Tcp);
        Assert.Null(availability.Tls);
        Assert.False(availability.AllListenersDown);

        await pipeline.StopAsync();
    }

    /// <summary>
    /// ADR-0018 委任 6: 部分受信断（TCP のみ bind 失敗）は「全リスナ受信不能」にならない
    /// （途絶検知は部分受信断を保留対象にしない——警告 Detail への併記で対応する。決定 3）。
    /// </summary>
    [Fact]
    public async Task ListenerAvailability_PartialBindFailure_IsNotAllListenersDown()
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var occupiedTcpPort = ((IPEndPoint)portReservation.LocalEndpoint).Port;

        await using var pipeline = new IngestionPipeline(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = occupiedTcpPort },
            _logStore);

        await pipeline.StartListenerAsync();

        var availability = pipeline.ListenerAvailability;
        Assert.True(availability.Udp);
        Assert.False(availability.Tcp);
        Assert.False(availability.AllListenersDown);

        await pipeline.StopAsync();
        portReservation.Stop();
    }

    /// <summary>
    /// ADR-0018 委任 6: 全リスナが開けない間は「全リスナ受信不能」として畳まれ、CF-6 の
    /// bind 再試行の成功（<see cref="IngestionPipeline.ListenerBindRecovered"/> の発火経路）で
    /// 現在状態へ反映される——起動 Outcome・復旧イベント・再構成 Outcome の 3 系統を 1 つの
    /// 状態面に畳む本委任の中核。
    /// </summary>
    [Fact]
    public async Task ListenerAvailability_AllListenersDown_RecoversViaBindRetry()
    {
        var udpReservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var occupiedUdpPort = ((IPEndPoint)udpReservation.Client.LocalEndPoint!).Port;
        var tcpReservation = new TcpListener(IPAddress.Loopback, 0);
        tcpReservation.Start();
        var occupiedTcpPort = ((IPEndPoint)tcpReservation.LocalEndpoint).Port;

        await using var pipeline = new IngestionPipeline(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = occupiedUdpPort },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = occupiedTcpPort },
            _logStore);
        pipeline.BindRetryInterval = TimeSpan.FromMilliseconds(200);

        await pipeline.StartListenerAsync();
        Assert.True(pipeline.ListenerAvailability.AllListenersDown);

        // 占有を解消 → 再試行の成功が現在状態へ畳まれる。
        udpReservation.Dispose();
        tcpReservation.Stop();

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (pipeline.ListenerAvailability.AllListenersDown && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        Assert.False(pipeline.ListenerAvailability.AllListenersDown);

        await pipeline.StopAsync();
    }

    /// <summary>
    /// Issue #291: 縮小継続時に警告レベルのログが出ること（#141 時代の「Error + 例外送出」から
    /// 「Warning + 継続」への変更を固定する）。
    /// </summary>
    [Fact]
    public async Task StartListenerAsync_WhenTcpBindFails_LogsDegradeAsWarning()
    {
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

        var result = await pipeline.StartListenerAsync();

        Assert.Equal(ListenerStartupStatus.DegradedRetrying, result.Tcp.Status);
        var record = Assert.Single(
            collector.GetSnapshot(),
            r => r.Category == typeof(IngestionPipeline).FullName && r.Level == LogLevel.Warning);
        Assert.Contains("縮小継続", record.Message);
        Assert.IsAssignableFrom<SocketException>(record.Exception);

        await pipeline.StopAsync();
        portReservation.Stop();
    }

    /// <summary>
    /// Issue #373: 再構成は options に変更のないリスナの CF-6 再試行を巻き添えで止めない。
    /// 「UDP が起動時縮小継続（再試行中）+ TCP だけ再構成」の組で、UDP の占有解消後に
    /// UDP が再試行で復旧すること（修正前はサービス再起動まで復旧しなかった）。
    /// </summary>
    [Fact]
    public async Task ReconfigureListenersAsync_UntouchedDownListener_KeepsItsBindRetryAlive()
    {
        var udpReservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var occupiedUdpPort = ((IPEndPoint)udpReservation.Client.LocalEndPoint!).Port;

        await using var pipeline = new IngestionPipeline(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = occupiedUdpPort },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            _logStore);
        pipeline.BindRetryInterval = TimeSpan.FromMilliseconds(200);

        var recoveryTcs = new TaskCompletionSource<ListenerBindRecovery>(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.ListenerBindRecovered += recovery => recoveryTcs.TrySetResult(recovery);

        var startResult = await pipeline.StartListenerAsync();
        Assert.Equal(ListenerStartupStatus.DegradedRetrying, startResult.Udp.Status);
        var gapObservedAtStart = DateTimeOffset.UtcNow;

        // UDP には触れず、TCP の bind アドレスだけを再構成する（UDP は NotChanged になる）。
        var reconfigureResult = await pipeline.ReconfigureListenersAsync(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = occupiedUdpPort },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0, BindAddressIsExplicit = true });

        Assert.Equal(ListenerReconfigurationStatus.NotChanged, reconfigureResult.Udp.Status);
        Assert.True(pipeline.ListenerAvailability.Tcp);
        Assert.False(pipeline.ListenerAvailability.Udp);

        // 占有を解消 → 張り直された再試行が受信を再開する。
        udpReservation.Dispose();

        var completed = await Task.WhenAny(recoveryTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(recoveryTcs.Task, completed);

        var recovery = await recoveryTcs.Task;
        Assert.Equal("UDP", recovery.ProtocolLabel);
        // 受信断区間の始端は再構成の時刻ではなく、最初に bind できなかった時刻を引き継ぐ。
        Assert.True(recovery.GapStartedAt <= gapObservedAtStart);
        Assert.True(pipeline.ListenerAvailability.Udp);

        await pipeline.StopAsync();
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
