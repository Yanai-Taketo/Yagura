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
    public void RearmAfterReceptionRecovery_RearmsOnlyEntriesThatCrossedTheThresholdWhileSuspended()
    {
        // 決定 3(Issue #381): 再アームの対象は「保留中に閾値超過となったエントリ」のみ。
        // 回復時刻で再アームし、固定のグレース値は置かない——各エントリの再検知は当該
        // エントリの閾値で律速する。
        var (detector, _, time) = Create(
            Entry("192.0.2.10", thresholdMinutes: 60),
            Entry("192.0.2.11", thresholdMinutes: 600));

        time.Advance(TimeSpan.FromMinutes(700));
        Assert.Equal(2, detector.RearmAfterReceptionRecovery());

        Assert.False(detector.Evaluate().HasAnything);

        // 短い閾値のエントリは 60 分後に、長い方はまだ発火しない。
        time.Advance(TimeSpan.FromMinutes(61));
        var result = detector.Evaluate();
        Assert.Single(result.Silences);
        Assert.Equal(IPAddress.Parse("192.0.2.10"), result.Silences[0].Address);
    }

    [Fact]
    public void RearmAfterReceptionRecovery_LeavesEntriesUnderTheThresholdUntouched()
    {
        // 決定 3(Issue #381 の欠陥 2): 閾値未満で沈黙中のエントリ(例: 閾値 60 分で 50 分沈黙)の
        // 時計を前進させない。旧実装(全エントリ再アーム)では、短い受信断が観測されるたびに
        // 検知が閾値ぶん先送りされ、反復すると恒久先送りになっていた。
        var (detector, _, time) = Create(Entry("192.0.2.10", thresholdMinutes: 60));

        time.Advance(TimeSpan.FromMinutes(50));
        Assert.Equal(0, detector.RearmAfterReceptionRecovery());

        // 本来の「最終受信 + 閾値」で発火する(回復 + 閾値まで先送りされない)。
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.Single(detector.Evaluate().Silences);
    }

    [Fact]
    public void RearmAfterReceptionRecovery_DoesNotTouchEntriesAlreadySilentBeforeTheOutage()
    {
        // 受信断より前から途絶中(警告済み)のエントリの途絶は受信断とは独立に始まっている——
        // 再アームせず途絶状態を維持し、復帰の証跡(1029)は実受信でのみ出す(Issue #381)。
        var (detector, tracker, time) = Create(Entry("192.0.2.10", thresholdMinutes: 60));
        var address = IPAddress.Parse("192.0.2.10");

        time.Advance(TimeSpan.FromMinutes(61));
        Assert.Single(detector.Evaluate().Silences); // 受信断の前に警告済み

        time.Advance(TimeSpan.FromMinutes(5)); // 短い受信断があったとして回復
        Assert.Equal(0, detector.RearmAfterReceptionRecovery());

        // 途絶状態は維持され(再警告なし)、復帰も出ない。
        var afterRecovery = detector.Evaluate();
        Assert.False(afterRecovery.HasAnything);
        Assert.True(Assert.Single(detector.SnapshotEntryStatuses()).IsSilent);

        // 実受信で初めて復帰(1029 の入力)が出る。
        tracker.RecordActivity(address);
        Assert.Single(detector.Evaluate().Recoveries);
    }

    // ------------------------------------------------------------------
    // 起動時 seed（決定 3。Issue #381）
    // ------------------------------------------------------------------

    private static Yagura.Storage.SourceActivity Activity(string address, DateTimeOffset lastSeenAt) =>
        new(address, lastSeenAt, RecordCount: 1);

    [Fact]
    public void SeedFromStore_EntryUnderTheThreshold_FiresAtLastSeenPlusThreshold()
    {
        // 決定 3 の設計どおり: 閾値 60 分の装置がサーバ再起動の 30 分前まで送っていた場合、
        // 起動 +60 分ではなく最終受信 +60 分(= 起動 +30 分)で発火する(Issue #381 の欠陥 1)。
        var (detector, _, time) = Create(Entry("192.0.2.10", thresholdMinutes: 60));

        detector.SeedFromStore([Activity("192.0.2.10", Origin - TimeSpan.FromMinutes(30))]);

        time.Advance(TimeSpan.FromMinutes(29));
        Assert.False(detector.Evaluate().HasAnything); // 最終受信から 59 分——まだ

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Single(detector.Evaluate().Silences); // 最終受信から 61 分——発火
    }

    [Fact]
    public void SeedFromStore_EntryAlreadyOverTheThreshold_RearmsAtStartupInsteadOfFiringImmediately()
    {
        // 決定 3: seed 時点で既に閾値超過のエントリは起動時刻仮基準へ再アーム(即発火しない)。
        // サーバ自身が閾値超の期間停止していた場合(週末メンテ等)の一斉偽陽性を防ぐ。
        var (detector, _, time) = Create(Entry("192.0.2.10", thresholdMinutes: 60));

        detector.SeedFromStore([Activity("192.0.2.10", Origin - TimeSpan.FromMinutes(120))]);

        Assert.False(detector.Evaluate().HasAnything); // 即発火しない

        time.Advance(TimeSpan.FromMinutes(61));
        Assert.Single(detector.Evaluate().Silences); // 起動から閾値経過で発火
    }

    [Fact]
    public void SeedFromStore_EntryAbsentFromTheResults_KeepsTheStartupBaseline()
    {
        // 決定 3: 結果に現れないエントリは起動時刻を仮基準とする。
        var (detector, _, time) = Create(Entry("192.0.2.10", thresholdMinutes: 60));

        detector.SeedFromStore([Activity("192.0.2.99", Origin - TimeSpan.FromMinutes(30))]);

        time.Advance(TimeSpan.FromMinutes(59));
        Assert.False(detector.Evaluate().HasAnything);

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Single(detector.Evaluate().Silences);
    }

    [Fact]
    public void SeedFromStore_DoesNotPullBackAnEntryThatAlreadyObservedRealActivity()
    {
        // 実受信が先に届いたスロットへ過去の DB 値を適用しない(実績の引き戻し防止)。
        var (detector, tracker, time) = Create(Entry("192.0.2.10", thresholdMinutes: 60));
        var address = IPAddress.Parse("192.0.2.10");

        time.Advance(TimeSpan.FromMinutes(5));
        tracker.RecordActivity(address); // seed より先に実受信

        detector.SeedFromStore([Activity("192.0.2.10", Origin - TimeSpan.FromMinutes(30))]);

        // 基準は実受信(起動 +5 分)のまま——+65 分までは発火しない。
        time.Advance(TimeSpan.FromMinutes(59));
        Assert.False(detector.Evaluate().HasAnything);

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Single(detector.Evaluate().Silences);
    }

    [Fact]
    public void Evaluate_EventCarriesLastSeenWallClockAndBaselineOrigin()
    {
        // Issue #382: 1027/1028 の Detail 入力——壁時計の最終受信時刻(単調経過からの換算値)と
        // 基準の由来(再アーム起点か)をイベントに載せる。
        var (detector, tracker, time) = Create(
            Entry("192.0.2.10", thresholdMinutes: 60),
            Entry("192.0.2.11", thresholdMinutes: 60));

        tracker.RecordActivity(IPAddress.Parse("192.0.2.11")); // B のみ実受信あり
        time.Advance(TimeSpan.FromMinutes(61));
        var result = detector.Evaluate();

        var a = result.Silences.Single(s => s.Address.Equals(IPAddress.Parse("192.0.2.10")));
        var b = result.Silences.Single(s => s.Address.Equals(IPAddress.Parse("192.0.2.11")));

        Assert.True(a.BaselineIsRearmed); // 登録時点基準(実受信の記録なし)
        Assert.False(b.BaselineIsRearmed); // 実受信基準
        Assert.Equal(Origin, a.LastSeenAt); // 換算値 = 登録時点
        Assert.Equal(Origin, b.LastSeenAt); // 実受信の時刻
    }

    [Fact]
    public void SeedFromStore_SeededEntry_ReportsTheDbTimeAsRealLastSeen()
    {
        // DB 実績で seed した基準は「実際に受信した時刻」——再アーム起点として表示しない(Issue #382)。
        var (detector, _, time) = Create(Entry("192.0.2.10", thresholdMinutes: 60));
        var lastSeen = Origin - TimeSpan.FromMinutes(30);

        detector.SeedFromStore([Activity("192.0.2.10", lastSeen)]);

        time.Advance(TimeSpan.FromMinutes(31));
        var silence = Assert.Single(detector.Evaluate().Silences);
        Assert.False(silence.BaselineIsRearmed);
        Assert.Equal(lastSeen, silence.LastSeenAt);
    }

    [Fact]
    public void SeedFromStore_NormalizesIPv4MappedStoredAddresses()
    {
        // 保存側のアドレスが IPv4-mapped IPv6 表記でも照合される(既存の正規化規約)。
        var (detector, _, time) = Create(Entry("192.0.2.10", thresholdMinutes: 60));

        detector.SeedFromStore([Activity("::ffff:192.0.2.10", Origin - TimeSpan.FromMinutes(30))]);

        time.Advance(TimeSpan.FromMinutes(31));
        Assert.Single(detector.Evaluate().Silences); // 最終受信から 61 分
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
    public void ApplyWatchlist_RaisingTheThresholdAboveTheElapsedTime_ClearsTheFlagWithoutARecoveryRecord()
    {
        // 決定 3(Issue #381 の欠陥 3): 閾値変更はフラグの解除のみ。実際には 1 件も受信して
        // いないのに「受信が再開した」(1029)を記録しない——復帰の証跡は実受信でのみ出す。
        var (detector, tracker, time) = Create(Entry("192.0.2.10", thresholdMinutes: 60));

        time.Advance(TimeSpan.FromMinutes(61));
        Assert.Single(detector.Evaluate().Silences);

        var relaxed = new[] { Entry("192.0.2.10", thresholdMinutes: 600) };
        tracker.ApplyWatchlist(relaxed);
        detector.ApplyWatchlist(relaxed);

        // フラグは適用時点で解除済み(UI の途絶中強調も同時に消える)。
        Assert.False(Assert.Single(detector.SnapshotEntryStatuses()).IsSilent);

        // 次の評価でも復帰は記録されない(次に 600 分超過すれば再発火する)。
        var result = detector.Evaluate();
        Assert.False(result.HasAnything);
    }

    // ------------------------------------------------------------------
    // エントリ状態のスナップショット（決定 4。UI-4 の登録済みマーク・途絶中強調）
    // ------------------------------------------------------------------

    [Fact]
    public void SnapshotEntryStatuses_TracksTheSilenceLifecycle()
    {
        var (detector, tracker, time) = Create(Entry("192.0.2.10", thresholdMinutes: 60));
        var address = IPAddress.Parse("192.0.2.10");

        // 登録直後: 登録済みだが途絶ではない。
        var initial = Assert.Single(detector.SnapshotEntryStatuses());
        Assert.Equal("192.0.2.10", initial.Address);
        Assert.Equal("装置-192.0.2.10", initial.Label);
        Assert.Equal(TimeSpan.FromMinutes(60), initial.Threshold);
        Assert.False(initial.IsSilent);

        // 途絶判定後は IsSilent が立つ。
        time.Advance(TimeSpan.FromMinutes(61));
        detector.Evaluate();
        Assert.True(Assert.Single(detector.SnapshotEntryStatuses()).IsSilent);

        // 受信再開の評価で解除される。
        tracker.RecordActivity(address);
        detector.Evaluate();
        Assert.False(Assert.Single(detector.SnapshotEntryStatuses()).IsSilent);
    }

    [Fact]
    public void SnapshotEntryStatuses_NormalizesIPv4MappedAddresses()
    {
        // 閲覧側（SourceActivity の文字列アドレス）との照合キーを揃える——IPv4-mapped IPv6 は
        // IPv4 表記へ畳む（流量制御・Top talkers と同じ既存規約）。
        var entry = new SourceSilenceWatchEntry(
            IPAddress.Parse("::ffff:192.0.2.99"), null, TimeSpan.FromMinutes(60), false);
        var (detector, _, _) = Create(entry);

        Assert.Equal("192.0.2.99", Assert.Single(detector.SnapshotEntryStatuses()).Address);
    }
}
