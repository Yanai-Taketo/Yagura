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

    // ------------------------------------------------------------------
    // Issue #143・#140 で追加した TCP 接続断・アイドルタイムアウト・オーバーサイズ破棄の 3 カウンタ
    // ------------------------------------------------------------------

    [Fact]
    public void SnapshotCumulativeCounters_WithoutSeed_ReflectsTcpCounters()
    {
        using var metrics = new IngestionMetrics();

        metrics.RecordTcpConnectionClosed();
        metrics.RecordTcpConnectionClosed();
        metrics.RecordTcpConnectionIdleTimeout();
        metrics.RecordTcpMessageDiscardedOversized();

        var snapshot = metrics.SnapshotCumulativeCounters();

        Assert.Equal(2, snapshot.TcpConnectionClosed);
        Assert.Equal(1, snapshot.TcpConnectionIdleTimeout);
        Assert.Equal(1, snapshot.TcpMessageOversizedDiscarded);
    }

    [Fact]
    public void SeedCumulativeCounters_ThenRecord_ComposesTcpCountersPreviousPlusThisProcess()
    {
        var previous = new IngestionCounterSnapshot(
            InternalBufferDropped: 0,
            TcpConnectionRejected: 0,
            SpoolEvacuated: 0,
            SpoolWriteFailed: 0,
            SpoolDiscarded: 0,
            PersistenceFailed: 0,
            FlowControlDropped: 0,
            TcpConnectionClosed: 20,
            TcpConnectionIdleTimeout: 5,
            TcpMessageOversizedDiscarded: 3);

        using var metrics = new IngestionMetrics();
        metrics.SeedCumulativeCounters(previous);

        metrics.RecordTcpConnectionClosed();
        metrics.RecordTcpMessageDiscardedOversized();

        var snapshot = metrics.SnapshotCumulativeCounters();

        Assert.Equal(21, snapshot.TcpConnectionClosed); // 20 + 1
        Assert.Equal(5, snapshot.TcpConnectionIdleTimeout); // 種のみ、今回の加算なし
        Assert.Equal(4, snapshot.TcpMessageOversizedDiscarded); // 3 + 1
    }

    [Fact]
    public void IngestionCounterSnapshot_ConstructedWithoutTcpFields_DefaultsToZero()
    {
        // 追加前の 7 引数の呼び出し（旧テスト・旧メタデータ領域ファイル相当）が、末尾の
        // 3 引数を既定値 0 として扱えること（additive-only なスキーマ変更の後方互換性）。
        var snapshot = new IngestionCounterSnapshot(1, 2, 3, 4, 5, 6, 7);

        Assert.Equal(0, snapshot.TcpConnectionClosed);
        Assert.Equal(0, snapshot.TcpConnectionIdleTimeout);
        Assert.Equal(0, snapshot.TcpMessageOversizedDiscarded);
    }
}
