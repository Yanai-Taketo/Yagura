using Yagura.Ingestion.Diagnostics;

namespace Yagura.Ingestion.Tests.Diagnostics;

/// <summary>
/// <see cref="IngestionMetrics"/> のカウンタ累積値の合成（architecture.md §4.3「前回までの累積 +
/// 今回プロセス分」。M4-4）。プロセス再起動をまたぐ継続は、メタデータ領域
/// （Yagura.Host.Observability）から読み込んだ <see cref="IngestionCounterSnapshot"/> を
/// <see cref="IngestionMetrics.SeedCumulativeCounters"/> で引き継いだうえで加算することで実現する
/// ——本テストは Yagura.Host を経由せず、この合成ロジック自体を単体で確認する。
/// </summary>
public sealed class IngestionMetricsCumulativeCounterTests
{
    [Fact]
    public void SnapshotCumulativeCounters_NoSeedNoRecord_AllZero()
    {
        using var metrics = new IngestionMetrics();

        var snapshot = metrics.SnapshotCumulativeCounters();

        Assert.Equal(IngestionCounterSnapshot.Zero, snapshot);
    }

    [Fact]
    public void SnapshotCumulativeCounters_WithoutSeed_ReflectsOnlyThisProcessRecords()
    {
        using var metrics = new IngestionMetrics();

        metrics.RecordInternalBufferDropped();
        metrics.RecordInternalBufferDropped();
        metrics.RecordSpoolDiscarded();

        var snapshot = metrics.SnapshotCumulativeCounters();

        Assert.Equal(2, snapshot.InternalBufferDropped);
        Assert.Equal(1, snapshot.SpoolDiscarded);
        Assert.Equal(0, snapshot.TcpConnectionRejected);
    }

    [Fact]
    public void SeedCumulativeCounters_ThenRecord_ComposesPreviousPlusThisProcess()
    {
        // 「前回までの累積」を模す（メタデータ領域から読み込んだ想定の値）。
        var previous = new IngestionCounterSnapshot(
            InternalBufferDropped: 100,
            TcpConnectionRejected: 10,
            SpoolEvacuated: 50,
            SpoolWriteFailed: 3,
            SpoolDiscarded: 7,
            PersistenceFailed: 2,
            FlowControlDropped: 0);

        using var metrics = new IngestionMetrics();
        metrics.SeedCumulativeCounters(previous);

        // 「今回プロセス分」を加算する。
        metrics.RecordInternalBufferDropped();
        metrics.RecordInternalBufferDropped();
        metrics.RecordTcpConnectionRejected();

        var snapshot = metrics.SnapshotCumulativeCounters();

        Assert.Equal(102, snapshot.InternalBufferDropped); // 100 + 2
        Assert.Equal(11, snapshot.TcpConnectionRejected); // 10 + 1
        Assert.Equal(50, snapshot.SpoolEvacuated); // 種のみ、今回の加算なし
        Assert.Equal(3, snapshot.SpoolWriteFailed);
        Assert.Equal(7, snapshot.SpoolDiscarded);
        Assert.Equal(2, snapshot.PersistenceFailed);
        Assert.Equal(0, snapshot.FlowControlDropped);
    }

    [Fact]
    public void SeedCumulativeCounters_WithZeroSnapshot_BehavesAsFirstEverStart()
    {
        using var metrics = new IngestionMetrics();
        metrics.SeedCumulativeCounters(IngestionCounterSnapshot.Zero);

        metrics.RecordSpoolEvacuated();

        var snapshot = metrics.SnapshotCumulativeCounters();
        Assert.Equal(1, snapshot.SpoolEvacuated);
    }
}
