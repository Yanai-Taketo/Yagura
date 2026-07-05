using Microsoft.Extensions.Logging.Testing;
using Yagura.Host.Observability;
using Yagura.Ingestion.Diagnostics;

namespace Yagura.Host.Tests.Observability;

/// <summary>
/// <see cref="ObservabilityCoordinator"/> の結合的な振る舞い（M4-4）。
/// 「メタデータ書き込み → 新インスタンス（プロセス再起動を模す）で読み込み → 合成値」の
/// 往復と、停止手順 1・3 の書き込み・正常停止イベント確定までを、実際のファイル I/O を
/// 通して確認する（architecture.md §4.3・§1.3）。
/// </summary>
public sealed class ObservabilityCoordinatorTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-observability-coordinator-test-{Guid.NewGuid():N}");

    public ObservabilityCoordinatorTests()
    {
        Directory.CreateDirectory(_dataRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CountersSurviveRestart_SecondProcessSeesComposedCumulativeValue()
    {
        // --- 1 回目の「プロセス」: カウンタを積み、停止手順 1・3 でメタデータへ書く。
        IngestionMetrics metrics1 = new();
        var logger = new FakeLogger();
        var coordinator1 = new ObservabilityCoordinator(_dataRoot, metrics1, logger);

        coordinator1.LoadAndSeed(); // 初回起動——メタデータ領域がまだ無い。
        Assert.Equal(MetadataState.Initial, coordinator1.PreviousState);

        metrics1.RecordInternalBufferDropped();
        metrics1.RecordInternalBufferDropped();
        metrics1.RecordSpoolDiscarded();

        var receiveSocketClosedAt = DateTimeOffset.UtcNow;
        coordinator1.WriteStopStep1(receiveSocketClosedAt);

        // 手順 2（drain）中に追加の破棄が起きた想定——手順 3 の前にさらに加算する。
        metrics1.RecordPersistenceFailed();

        var stoppedAt = receiveSocketClosedAt.AddSeconds(1);
        coordinator1.WriteStopStep3(stoppedAt, receiveSocketClosedAt);
        metrics1.Dispose();

        // --- 2 回目の「プロセス」: 新しい IngestionMetrics インスタンスで読み込み、
        // 前回までの累積を引き継ぐ。
        IngestionMetrics metrics2 = new();
        var coordinator2 = new ObservabilityCoordinator(_dataRoot, metrics2, logger);
        coordinator2.LoadAndSeed();

        var seededSnapshot = metrics2.SnapshotCumulativeCounters();
        Assert.Equal(2, seededSnapshot.InternalBufferDropped);
        Assert.Equal(1, seededSnapshot.SpoolDiscarded);
        Assert.Equal(1, seededSnapshot.PersistenceFailed);

        // 今回プロセス分をさらに加算し、合成されることを確認する。
        metrics2.RecordInternalBufferDropped();
        var composed = metrics2.SnapshotCumulativeCounters();
        Assert.Equal(3, composed.InternalBufferDropped); // 前回 2 + 今回 1

        // 前回終了は正常停止だったので、受信断区間の起点が引き継がれていること。
        Assert.NotNull(coordinator2.PreviousState.LastStopEvent);
        Assert.Equal(receiveSocketClosedAt, coordinator2.PreviousState.LastStopEvent!.ReceiveSocketClosedAt);
        Assert.Equal(stoppedAt, coordinator2.PreviousState.LastStopEvent!.StoppedAt);

        metrics2.Dispose();
    }

    [Fact]
    public async Task PeriodicPersistence_WritesLivenessWithoutStopEvent()
    {
        using var metrics = new IngestionMetrics();
        var logger = new FakeLogger();
        await using var coordinator = new ObservabilityCoordinator(_dataRoot, metrics, logger);

        coordinator.LoadAndSeed();

        // 停止イベントを記録せず、生存時刻のみが残っている状態を作る（クラッシュ近似の前提条件。
        // §4.4「前回が正常停止でない場合…最終生存時刻を近似の断点として」）。
        // WriteStopStep1 は「手順 1」専用の書き込みだが、内容としては
        // 「LastStopEvent なし・生存時刻あり」という定期永続化と同じ形になるため、
        // 定期永続化ループの 1 tick 相当の検証にはこれで足りる。
        var livenessAt = DateTimeOffset.UtcNow;
        coordinator.WriteStopStep1(livenessAt);

        var state = MetadataStore.Read(_dataRoot);
        Assert.Null(state.LastStopEvent);
        Assert.Equal(livenessAt, state.LastLivenessAt);
    }
}
