using System.Net;
using Microsoft.Extensions.Time.Testing;
using Yagura.Host.Configuration;
using Yagura.Host.Observability.ActiveNotification.SourceSilence;

namespace Yagura.Host.Tests.Observability.ActiveNotification.SourceSilence;

/// <summary>
/// <see cref="SourceSilenceDetector"/>（ADR-0018 決定 3）の単体テスト。
/// ADR の受け入れ基準のうち、判定・抑制・集約に関わるものをここで固定する。
/// </summary>
public sealed class SourceSilenceDetectorTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);

    private static SourceSilenceWatchEntry Entry(string address, int thresholdMinutes = 60) =>
        new(IPAddress.Parse(address), $"装置-{address}", TimeSpan.FromMinutes(thresholdMinutes), false);

    private static (SourceSilenceDetector Detector, SourceActivityTracker Tracker, FakeTimeProvider Time)
        Create(params SourceSilenceWatchEntry[] entries)
    {
        var time = new FakeTimeProvider(Origin);
        var tracker = new SourceActivityTracker(time);
        var detector = new SourceSilenceDetector(tracker, time);

        tracker.ApplyWatchlist(entries);
        detector.ApplyWatchlist(entries);

        return (detector, tracker, time);
    }

    // ------------------------------------------------------------------
    // 受け入れ基準: 途絶 → 警告(1回) → 復帰 → 再途絶 → 再警告
    // ------------------------------------------------------------------

    [Fact]
    public void Evaluate_SilenceRecoveryAndSilenceAgain_FollowsTheStateMachine()
    {
        var (detector, tracker, time) = Create(Entry("192.0.2.10", thresholdMinutes: 60));
        var address = IPAddress.Parse("192.0.2.10");

        // 閾値内は何も起きない。
        time.Advance(TimeSpan.FromMinutes(59));
        Assert.False(detector.Evaluate().HasAnything);

        // 閾値超過で 1 回だけ発火する。
        time.Advance(TimeSpan.FromMinutes(2));
        var first = detector.Evaluate();
        Assert.Single(first.Silences);
        Assert.Equal(address, first.Silences[0].Address);
        Assert.Equal("装置-192.0.2.10", first.Silences[0].Label);

        // 途絶が継続しても再発火しない（状態遷移のラッチ）。
        time.Advance(TimeSpan.FromMinutes(30));
        Assert.Empty(detector.Evaluate().Silences);

        // 受信再開で復帰の情報記録が 1 件出る（能動通知はしない）。
        tracker.RecordActivity(address);
        var recovery = detector.Evaluate();
        Assert.Empty(recovery.Silences);
        Assert.Single(recovery.Recoveries);

        // 再び途絶すれば再発火する。
        time.Advance(TimeSpan.FromMinutes(61));
        Assert.Single(detector.Evaluate().Silences);
    }

    // ------------------------------------------------------------------
    // 受け入れ基準: フラッピングが抑制窓で律速される
    // ------------------------------------------------------------------

    [Fact]
    public void Evaluate_Flapping_IsRateLimitedByThePerEntrySuppressionWindow()
    {
        // 短い個別閾値 + 不安定な装置で発火が反復する事態を防ぐ（決定 3）。
        var (detector, tracker, time) = Create(Entry("192.0.2.10", thresholdMinutes: 10));
        var address = IPAddress.Parse("192.0.2.10");

        time.Advance(TimeSpan.FromMinutes(11));
        Assert.Single(detector.Evaluate().Silences); // 1 回目

        // 復帰 → 再途絶を抑制窓（15 分）の内側で繰り返す。
        tracker.RecordActivity(address);
        detector.Evaluate();
        time.Advance(TimeSpan.FromMinutes(11));

        // 2 回目は窓内なので抑制される。
        Assert.Empty(detector.Evaluate().Silences);
    }

    [Fact]
    public void Evaluate_SuppressedSilenceThatPersists_FiresLateInsteadOfBeingLostForever()
    {
        // 「途絶が継続しているのに警告が一度も出ていない」状態を作らない（決定 3）。
        var (detector, tracker, time) = Create(Entry("192.0.2.10", thresholdMinutes: 10));
        var address = IPAddress.Parse("192.0.2.10");

        time.Advance(TimeSpan.FromMinutes(11));
        Assert.Single(detector.Evaluate().Silences);

        tracker.RecordActivity(address);
        detector.Evaluate();
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.Empty(detector.Evaluate().Silences); // 窓内で抑制（保留になる）

        // 窓が明けた時点でも途絶が継続している → 遅延して 1 回発火する。
        time.Advance(SourceSilenceConstants.EntrySuppressionWindow);
        Assert.Single(detector.Evaluate().Silences);
    }

    [Fact]
    public void Evaluate_SuppressionWindowIsPerEntry_SoOneDeviceDoesNotSwallowAnother()
    {
        // 既存のトリガ別抑制窓との粒度の違い（決定 3）——装置 A の発火が装置 B の初報を飲まない。
        var (detector, _, time) = Create(
            Entry("192.0.2.10", thresholdMinutes: 10),
            Entry("192.0.2.11", thresholdMinutes: 30));

        time.Advance(TimeSpan.FromMinutes(11));
        var first = detector.Evaluate();
        Assert.Single(first.Silences);
        Assert.Equal(IPAddress.Parse("192.0.2.10"), first.Silences[0].Address);

        // A の発火直後（A の窓内）でも、B の初報は出る。
        time.Advance(TimeSpan.FromMinutes(20));
        var second = detector.Evaluate();
        Assert.Single(second.Silences);
        Assert.Equal(IPAddress.Parse("192.0.2.11"), second.Silences[0].Address);
    }

    // ------------------------------------------------------------------
    // 受け入れ基準: 一斉集約
    // ------------------------------------------------------------------

    [Fact]
    public void Evaluate_ManySimultaneousSilences_ProducesOneAggregatedWarning()
    {
        var entries = Enumerable.Range(0, SourceSilenceConstants.BurstAggregationThreshold)
            .Select(i => Entry($"192.0.2.{10 + i}", thresholdMinutes: 60))
            .ToArray();
        var (detector, _, time) = Create(entries);

        time.Advance(TimeSpan.FromMinutes(61));
        var result = detector.Evaluate();

        Assert.True(result.IsBurst);
        Assert.Equal(SourceSilenceConstants.BurstAggregationThreshold, result.Silences.Count);
    }

    [Fact]
    public void Evaluate_BelowTheBurstThreshold_StaysIndividual()
    {
        var entries = Enumerable.Range(0, SourceSilenceConstants.BurstAggregationThreshold - 1)
            .Select(i => Entry($"192.0.2.{10 + i}", thresholdMinutes: 60))
            .ToArray();
        var (detector, _, time) = Create(entries);

        time.Advance(TimeSpan.FromMinutes(61));
        var result = detector.Evaluate();

        Assert.False(result.IsBurst);
        Assert.Equal(entries.Length, result.Silences.Count);
    }

    [Fact]
    public void Evaluate_BurstAlsoUpdatesPerEntryStateSoItDoesNotRefireNextCycle()
    {
        // 決定 3: 集約警告時も各エントリの途絶フラグ・抑制窓は個別警告時と同様に更新する。
        var entries = Enumerable.Range(0, SourceSilenceConstants.BurstAggregationThreshold)
            .Select(i => Entry($"192.0.2.{10 + i}", thresholdMinutes: 60))
            .ToArray();
        var (detector, _, time) = Create(entries);

        time.Advance(TimeSpan.FromMinutes(61));
        Assert.True(detector.Evaluate().IsBurst);

        time.Advance(TimeSpan.FromMinutes(30));
        Assert.Empty(detector.Evaluate().Silences);
    }

    // ------------------------------------------------------------------
    // 受け入れ基準: 起動時の再アーム（seed 時点で閾値超過でも即発火しない）
    // ------------------------------------------------------------------

    [Fact]
    public void NewlyRegisteredEntry_DoesNotFireImmediately_EvenIfItHasNeverBeenSeen()
    {
        // 決定 3: 登録時点を仮の最終受信時刻とし、閾値経過で通常どおり発火する。
        var (detector, _, time) = Create(Entry("192.0.2.10", thresholdMinutes: 60));

        Assert.False(detector.Evaluate().HasAnything);

        time.Advance(TimeSpan.FromMinutes(59));
        Assert.False(detector.Evaluate().HasAnything);

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Single(detector.Evaluate().Silences);
    }

    // ------------------------------------------------------------------
    // 受信断保留（決定 3。委任 6 の判定源は第 4 段）
    // ------------------------------------------------------------------

    [Fact]
    public void Evaluate_WhileReceptionIsSuspended_DoesNotTransitionToSilent()
    {
        // 真因がサーバ側なのに運用者を装置側の調査へ誘導しない。
        var (detector, _, time) = Create(Entry("192.0.2.10", thresholdMinutes: 60));

        time.Advance(TimeSpan.FromMinutes(120));
        Assert.False(detector.Evaluate(receptionSuspended: true).HasAnything);

        // 保留が解けた周期で判定される。
        Assert.Single(detector.Evaluate(receptionSuspended: false).Silences);
    }

    [Fact]
    public void RearmAfterReceptionRecovery_RestartsTheClockForEveryEntry()
    {
        // 回復時刻で再アームする（起動時の再アームと同一規則）。固定のグレース値は置かない
        // ——各エントリの再検知は当該エントリの閾値で律速する。
        var (detector, _, time) = Create(
            Entry("192.0.2.10", thresholdMinutes: 60),
            Entry("192.0.2.11", thresholdMinutes: 600));

        time.Advance(TimeSpan.FromMinutes(700));
        detector.RearmAfterReceptionRecovery();

        Assert.False(detector.Evaluate().HasAnything);

        // 短い閾値のエントリは 60 分後に、長い方はまだ発火しない。
        time.Advance(TimeSpan.FromMinutes(61));
        var result = detector.Evaluate();
        Assert.Single(result.Silences);
        Assert.Equal(IPAddress.Parse("192.0.2.10"), result.Silences[0].Address);
    }

    // ------------------------------------------------------------------
    // 設定の差し替え（決定 6）
    // ------------------------------------------------------------------

    [Fact]
    public void ApplyWatchlist_RemovedEntry_StopsBeingEvaluated()
    {
        var (detector, tracker, time) = Create(Entry("192.0.2.10"), Entry("192.0.2.11"));

        tracker.ApplyWatchlist([Entry("192.0.2.10")]);
        detector.ApplyWatchlist([Entry("192.0.2.10")]);

        time.Advance(TimeSpan.FromMinutes(61));
        var result = detector.Evaluate();

        Assert.Single(result.Silences);
        Assert.Equal(IPAddress.Parse("192.0.2.10"), result.Silences[0].Address);
    }

    [Fact]
    public void ApplyWatchlist_RaisingTheThresholdAboveTheElapsedTime_ClearsTheSilentFlag()
    {
        // 決定 3: 閾値の変更で途絶条件を満たさなくなったエントリはフラグを解除する。
        var (detector, tracker, time) = Create(Entry("192.0.2.10", thresholdMinutes: 60));

        time.Advance(TimeSpan.FromMinutes(61));
        Assert.Single(detector.Evaluate().Silences);

        var relaxed = new[] { Entry("192.0.2.10", thresholdMinutes: 600) };
        tracker.ApplyWatchlist(relaxed);
        detector.ApplyWatchlist(relaxed);

        // 閾値内に戻ったため復帰として扱われる（次に 600 分超過すれば再発火する）。
        var result = detector.Evaluate();
        Assert.Empty(result.Silences);
        Assert.Single(result.Recoveries);
    }
}
