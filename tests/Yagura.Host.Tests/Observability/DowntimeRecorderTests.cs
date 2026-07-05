using Yagura.Host.Observability;
using Yagura.Ingestion.Diagnostics;

namespace Yagura.Host.Tests.Observability;

/// <summary>
/// <see cref="DowntimeRecorder"/> の単体テスト（M4-4。architecture.md §4.4）。
/// 正常停止の受信断区間・クラッシュ近似断点・初回起動（記録なし）の 3 パターンを確認する。
/// </summary>
public sealed class DowntimeRecorderTests
{
    // 時間窓 boundary テストは 1 基準時刻から両端を構築する（feedback:
    // Test fixture DateTime drift。DateTimeOffset.UtcNow の複数回読み取りによる
    // マイクロ秒単位のずれで flaky 化することを避ける）。
    private static readonly DateTimeOffset Baseline = new(2026, 7, 5, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NormalStop_ReturnsDowntimeEventFromSocketClosedToReceiveStarted()
    {
        var stopEvent = new StopEventRecord(
            ReceiveSocketClosedAt: Baseline,
            StoppedAt: Baseline.AddSeconds(1));
        var previousState = new MetadataState(IngestionCounterSnapshot.Zero, stopEvent, LastLivenessAt: Baseline.AddSeconds(1));
        var receiveStartedAt = Baseline.AddMinutes(5);

        var result = DowntimeRecorder.DetermineDowntimeEvent(previousState, receiveStartedAt);

        Assert.NotNull(result);
        Assert.Equal(ObservabilityConstants.SystemEventKindDowntimeNormalStop, result.Kind);
        Assert.Equal(Baseline, result.StartAt);
        Assert.Equal(receiveStartedAt, result.EndAt);
        Assert.False(result.Approximate);
    }

    [Fact]
    public void CrashApproximate_NoStopEventButLivenessPresent_ReturnsApproximateDowntimeEvent()
    {
        var lastLivenessAt = Baseline;
        var previousState = new MetadataState(IngestionCounterSnapshot.Zero, LastStopEvent: null, LastLivenessAt: lastLivenessAt);
        var receiveStartedAt = Baseline.AddMinutes(3);

        var result = DowntimeRecorder.DetermineDowntimeEvent(previousState, receiveStartedAt);

        Assert.NotNull(result);
        Assert.Equal(ObservabilityConstants.SystemEventKindDowntimeCrashApproximate, result.Kind);
        Assert.Equal(lastLivenessAt, result.StartAt);
        Assert.Equal(receiveStartedAt, result.EndAt);
        Assert.True(result.Approximate);
    }

    [Fact]
    public void FirstEverStart_NoStopEventAndNoLiveness_ReturnsNull()
    {
        var previousState = MetadataState.Initial;
        var receiveStartedAt = Baseline;

        var result = DowntimeRecorder.DetermineDowntimeEvent(previousState, receiveStartedAt);

        Assert.Null(result);
    }

    [Fact]
    public void NormalStop_TakesPrecedenceOverLivenessTimestamp()
    {
        // 正常停止イベントと生存時刻の両方がある場合（通常はこの組み合わせになる。
        // §1.3 手順 3 が両方を同時刻近辺で書くため）——正常停止を優先する。
        var stopEvent = new StopEventRecord(
            ReceiveSocketClosedAt: Baseline,
            StoppedAt: Baseline.AddSeconds(1));
        var previousState = new MetadataState(
            IngestionCounterSnapshot.Zero,
            stopEvent,
            LastLivenessAt: Baseline.AddSeconds(1));
        var receiveStartedAt = Baseline.AddMinutes(1);

        var result = DowntimeRecorder.DetermineDowntimeEvent(previousState, receiveStartedAt);

        Assert.NotNull(result);
        Assert.False(result.Approximate);
        Assert.Equal(ObservabilityConstants.SystemEventKindDowntimeNormalStop, result.Kind);
    }
}
