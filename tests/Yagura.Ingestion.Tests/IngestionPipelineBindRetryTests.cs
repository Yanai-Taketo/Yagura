using System.Net;
using System.Net.Sockets;
using Yagura.Ingestion.Tcp;
using Yagura.Ingestion.Udp;
using Yagura.Storage.Sqlite;

namespace Yagura.Ingestion.Tests;

/// <summary>
/// CF-6 bind 再試行の停止との競合・受信断始端の引き継ぎの回帰テスト（Issue #390）。
/// </summary>
/// <remarks>
/// <para>
/// <b>停止 × 再構成</b>: <see cref="IngestionPipeline.StopListenersAsync"/> は再構成ゲートを
/// 取らないため、進行中の <see cref="IngestionPipeline.ReconfigureListenersAsync"/> 末尾の
/// 再アームが停止完了「後」に再試行ループを張り直し得た。競合そのものは決定的に再現しづらい
/// ため、時系列を直列化した形——停止完了後に再構成（の末尾の再アーム）が走る——で
/// 「停止後は再アームが no-op」という構造的保証を検証する。
/// </para>
/// <para>
/// <b>受信断始端</b>: 起動時から bind できていないリスナを再構成した場合も、受信断区間の始端は
/// 最初に bind できなくなった時刻を引き継ぐ（#373 の原則の全経路への適用。再構成開始時刻に
/// リセットされると [起動時の bind 失敗, 再構成開始) が downtime 記録から欠落する）。
/// </para>
/// </remarks>
public sealed class IngestionPipelineBindRetryTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"yagura-bindretry-tests-{Guid.NewGuid():N}.db");
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

    /// <summary>
    /// 停止完了後に再構成（options 無変更 → 末尾の再アーム）が完了しても、CF-6 再試行は
    /// 張り直されず、リスナは復活しない（「停止後にリスナが勝手に復活しない」の保証）。
    /// </summary>
    [Fact]
    public async Task StopListeners_ThenReconfigureCompletion_DoesNotRearmBindRetry()
    {
        // 起動時に UDP ポートを占有し、縮小継続（CF-6 再試行中）の状態を作る。
        using var occupant = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var occupiedPort = ((IPEndPoint)occupant.Client.LocalEndPoint!).Port;

        await using var pipeline = new IngestionPipeline(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = occupiedPort },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            _logStore);
        pipeline.BindRetryInterval = TimeSpan.FromMilliseconds(100);

        var startup = await pipeline.StartListenerAsync();
        Assert.Equal(ListenerStartupStatus.DegradedRetrying, startup.Udp.Status);

        var recovered = false;
        pipeline.ListenerBindRecovered += _ => recovered = true;

        await pipeline.StopListenersAsync();

        // 進行中だった再構成の末尾（無変更リスナの再アーム）が停止完了後に走る状況を、
        // 時系列を直列化した形で再現する（options は無変更 = NotChanged → 再アーム経路へ入る）。
        await pipeline.ReconfigureListenersAsync(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = occupiedPort },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 });

        // ポートを解放しても再試行は張り直されておらず、リスナは復活しない
        // （張り直されていれば 100ms 間隔の再試行がすぐに bind へ成功してしまう）。
        occupant.Dispose();
        await Task.Delay(1000);

        Assert.False(recovered);

        // ポートが自分で bind し直せる = パイプラインが掴んでいない（復活していない）ことの確認。
        using var rebind = new UdpClient(new IPEndPoint(IPAddress.Loopback, occupiedPort));
    }

    /// <summary>
    /// 起動時から down のリスナを再構成し、新構成 bind 失敗 → 旧構成復旧も失敗 → CF-6 再試行と
    /// 進んだ場合、受信断区間の始端は再構成開始時刻ではなく起動時に bind できなくなった時刻を
    /// 引き継ぐ（Outcome と <see cref="IngestionPipeline.ListenerBindRecovered"/> の両方）。
    /// </summary>
    [Fact]
    public async Task Reconfigure_ListenerDownSinceStartup_InheritsGapStartFromStartupFailure()
    {
        using var occupant1 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port1 = ((IPEndPoint)occupant1.Client.LocalEndPoint!).Port;

        await using var pipeline = new IngestionPipeline(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = port1 },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 },
            _logStore);

        // 起動時縮小継続の再試行ループは寝かせておく（本テストの関心は再構成経由の始端引き継ぎ）。
        pipeline.BindRetryInterval = TimeSpan.FromMinutes(10);

        var startLowerBound = DateTimeOffset.UtcNow;
        var startup = await pipeline.StartListenerAsync();
        Assert.Equal(ListenerStartupStatus.DegradedRetrying, startup.Udp.Status);

        // 始端（起動時の bind 失敗時刻）と再構成開始時刻を確実に区別できるよう間隔を空ける。
        await Task.Delay(50);
        var reconfigureLowerBound = DateTimeOffset.UtcNow;

        // 新ポートも占有 → 新構成の bind は失敗。旧ポート（port1）も塞がったまま → 復旧も失敗
        // → RollBackOrRetryAsync が CF-6 再試行へ移行する。
        var occupant2 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port2 = ((IPEndPoint)occupant2.Client.LocalEndPoint!).Port;

        var recoveredTcs = new TaskCompletionSource<ListenerBindRecovery>(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.ListenerBindRecovered += recovery => recoveredTcs.TrySetResult(recovery);

        pipeline.BindRetryInterval = TimeSpan.FromMilliseconds(100);
        var result = await pipeline.ReconfigureListenersAsync(
            new UdpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = port2 },
            new TcpSyslogListenerOptions { BindAddress = "127.0.0.1", Port = 0 });

        Assert.Equal(ListenerReconfigurationStatus.DownRetrying, result.Udp.Status);

        // Outcome の始端が起動時の bind 失敗時刻（< 再構成開始）を引き継いでいる。
        Assert.NotNull(result.Udp.GapStartedAt);
        Assert.InRange(result.Udp.GapStartedAt!.Value, startLowerBound, reconfigureLowerBound);

        // 新ポートを解放すると CF-6 再試行が新構成で受信を再開し、復旧通知の受信断始端も
        // 同じ時刻で報告される（downtime.listener-bind-retry の記録が [T0, 復旧) を覆う）。
        occupant2.Dispose();
        var recovered = await recoveredTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(result.Udp.GapStartedAt!.Value, recovered.GapStartedAt);
        Assert.InRange(recovered.GapStartedAt, startLowerBound, reconfigureLowerBound);

        await pipeline.StopAsync();
    }
}
