using Yagura.Storage.Spool;

namespace Yagura.Storage.Tests.Spool;

/// <summary>
/// <see cref="SpoolSelfTestTracker"/>（architecture.md §3.2.5。Issue #152）の投入・照合・
/// タイムアウト判定を検証する。時間窓を扱うため、基準時刻を 1 つ作って両端を構築する
/// （conventions.md「時間窓を扱うテストは 1 つの基準時刻から両端を構築する」）。
/// </summary>
public sealed class SpoolSelfTestTrackerTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 7, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    [Fact]
    public void IsPendingTimedOut_NoMarkerEverBegun_ReturnsFalse()
    {
        var tracker = new SpoolSelfTestTracker();

        Assert.False(tracker.IsPendingTimedOut(Baseline + Timeout + TimeSpan.FromDays(1), Timeout));
    }

    [Fact]
    public void IsPendingTimedOut_WithinTimeout_ReturnsFalse()
    {
        var tracker = new SpoolSelfTestTracker();
        tracker.BeginNewMarker(Baseline);

        Assert.False(tracker.IsPendingTimedOut(Baseline + Timeout - TimeSpan.FromSeconds(1), Timeout));
    }

    [Fact]
    public void IsPendingTimedOut_AtOrPastTimeout_ReturnsTrue()
    {
        var tracker = new SpoolSelfTestTracker();
        tracker.BeginNewMarker(Baseline);

        Assert.True(tracker.IsPendingTimedOut(Baseline + Timeout, Timeout));
    }

    [Fact]
    public void OnSelfTestRecordDrained_MatchingMarker_ClearsPending_TimeoutNoLongerTriggers()
    {
        var tracker = new SpoolSelfTestTracker();
        var marker = tracker.BeginNewMarker(Baseline);

        tracker.OnSelfTestRecordDrained(marker);

        Assert.False(tracker.IsPendingTimedOut(Baseline + Timeout + TimeSpan.FromDays(1), Timeout));
    }

    [Fact]
    public void OnSelfTestRecordDrained_NonMatchingMarker_DoesNotClearPending()
    {
        var tracker = new SpoolSelfTestTracker();
        tracker.BeginNewMarker(Baseline);

        tracker.OnSelfTestRecordDrained("some-unrelated-marker");

        Assert.True(tracker.IsPendingTimedOut(Baseline + Timeout, Timeout));
    }

    [Fact]
    public void BeginNewMarker_CalledAgain_OverwritesPreviousPending_OldMarkerAckNoLongerClears()
    {
        var tracker = new SpoolSelfTestTracker();
        var firstMarker = tracker.BeginNewMarker(Baseline);

        var secondMarker = tracker.BeginNewMarker(Baseline + TimeSpan.FromMinutes(1));

        Assert.NotEqual(firstMarker, secondMarker);

        // 上書き前のマーカーが遅れて drain された場合、無視される（現在未照合のマーカーではないため）。
        tracker.OnSelfTestRecordDrained(firstMarker);
        Assert.True(tracker.IsPendingTimedOut(Baseline + TimeSpan.FromMinutes(1) + Timeout, Timeout));

        // 現在のマーカーで照合すれば正しくクリアされる。
        tracker.OnSelfTestRecordDrained(secondMarker);
        Assert.False(tracker.IsPendingTimedOut(Baseline + TimeSpan.FromMinutes(1) + Timeout, Timeout));
    }

    [Fact]
    public void BeginNewMarker_ReturnsNonEmptyUniqueMarkers()
    {
        var tracker = new SpoolSelfTestTracker();

        var first = tracker.BeginNewMarker(Baseline);
        var second = tracker.BeginNewMarker(Baseline);

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.False(string.IsNullOrWhiteSpace(second));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CancelPending_MatchingMarker_ClearsPending_TimeoutNoLongerTriggers()
    {
        // 投入（スプール書込）失敗時の登録取消（PR #200 レビュー指摘への対応）: 書き込まれなかった
        // マーカーが未照合のまま残ると、drain に照合される見込みが無いままタイムアウト通知が
        // 次回投入まで反復するため、投入側は書込失敗の時点で登録を取り消す。
        var tracker = new SpoolSelfTestTracker();
        var marker = tracker.BeginNewMarker(Baseline);

        tracker.CancelPending(marker);

        Assert.False(tracker.IsPendingTimedOut(Baseline + Timeout + TimeSpan.FromDays(1), Timeout));
    }

    [Fact]
    public void CancelPending_NonMatchingMarker_DoesNotClearPending()
    {
        var tracker = new SpoolSelfTestTracker();
        tracker.BeginNewMarker(Baseline);

        tracker.CancelPending("some-unrelated-marker");

        Assert.True(tracker.IsPendingTimedOut(Baseline + Timeout, Timeout));
    }
}
