using System.Net;
using Microsoft.Extensions.Time.Testing;
using Yagura.Host.Administration;
using Yagura.Host.Administration.AdminAuthentication;

namespace Yagura.Host.Tests.Administration.AdminAuthentication;

/// <summary>
/// <see cref="AdminAuthFailureDefense"/>（ADR-0011 決定 2〜6 の三層防御の状態保持・判定）の単体テスト。
/// </summary>
/// <remarks>
/// 検証観点: ①IP レート制限（窓・上限・loopback 除外・Retry-After）、②グローバルトークンバケット
/// （バースト・補充・loopback 除外・Retry-After 上限）、③アカウント単位バックオフ（猶予閾値・
/// 指数遅延・cap・アイドル減衰・複合キー分離・成功時リセット）、原子的更新（並行試行での
/// 取りこぼしなし。委任事項 1）、能動通知への昇格スナップショット（決定 6）。
/// </remarks>
public sealed class AdminAuthFailureDefenseTests
{
    private static readonly IPAddress RemoteA = IPAddress.Parse("203.0.113.10");
    private static readonly IPAddress RemoteB = IPAddress.Parse("203.0.113.20");

    // ==== ①IP レート制限 ====

    [Fact]
    public void CheckIpRateLimit_WithinLimit_Allows()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        var defense = new AdminAuthFailureDefense(timeProvider);

        for (var i = 0; i < AdminAuthenticationDefaults.IpRateLimitMaxAttempts; i++)
        {
            var decision = defense.CheckIpRateLimit(RemoteA, isLoopback: false);
            Assert.True(decision.Allowed);
        }
    }

    [Fact]
    public void CheckIpRateLimit_ExceedsLimit_DeniesWithFiniteRetryAfter()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        var defense = new AdminAuthFailureDefense(timeProvider);

        for (var i = 0; i < AdminAuthenticationDefaults.IpRateLimitMaxAttempts; i++)
        {
            Assert.True(defense.CheckIpRateLimit(RemoteA, isLoopback: false).Allowed);
        }

        var denied = defense.CheckIpRateLimit(RemoteA, isLoopback: false);

        Assert.False(denied.Allowed);
        Assert.True(denied.RetryAfter > TimeSpan.Zero);
        Assert.True(denied.RetryAfter <= AdminAuthenticationDefaults.RateLimitRetryAfterCap);
    }

    [Fact]
    public void CheckIpRateLimit_DifferentSourceIps_AreIndependent()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        var defense = new AdminAuthFailureDefense(timeProvider);

        for (var i = 0; i < AdminAuthenticationDefaults.IpRateLimitMaxAttempts; i++)
        {
            defense.CheckIpRateLimit(RemoteA, isLoopback: false);
        }

        Assert.False(defense.CheckIpRateLimit(RemoteA, isLoopback: false).Allowed);
        // 別の送信元 IP は無関係に許可される。
        Assert.True(defense.CheckIpRateLimit(RemoteB, isLoopback: false).Allowed);
    }

    [Fact]
    public void CheckIpRateLimit_WindowElapses_ResetsCount()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        var defense = new AdminAuthFailureDefense(timeProvider);

        for (var i = 0; i < AdminAuthenticationDefaults.IpRateLimitMaxAttempts; i++)
        {
            defense.CheckIpRateLimit(RemoteA, isLoopback: false);
        }

        Assert.False(defense.CheckIpRateLimit(RemoteA, isLoopback: false).Allowed);

        timeProvider.Advance(AdminAuthenticationDefaults.IpRateLimitWindow + TimeSpan.FromSeconds(1));

        Assert.True(defense.CheckIpRateLimit(RemoteA, isLoopback: false).Allowed);
    }

    [Fact]
    public void CheckIpRateLimit_Loopback_AlwaysAllowed_RegardlessOfVolume()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        var defense = new AdminAuthFailureDefense(timeProvider);

        for (var i = 0; i < AdminAuthenticationDefaults.IpRateLimitMaxAttempts * 3; i++)
        {
            Assert.True(defense.CheckIpRateLimit(IPAddress.Loopback, isLoopback: true).Allowed);
        }
    }

    // ==== ②グローバルトークンバケット ====

    [Fact]
    public void CheckGlobalBucket_UpToBurst_Allows_ThenDeniesWithFiniteRetryAfter()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        var defense = new AdminAuthFailureDefense(timeProvider);

        for (var i = 0; i < AdminAuthenticationDefaults.GlobalBucketBurst; i++)
        {
            var context = IPAddress.Parse($"198.51.100.{i + 1}");
            Assert.True(defense.CheckGlobalBucket(isLoopback: false, context).Allowed);
        }

        var denied = defense.CheckGlobalBucket(isLoopback: false, IPAddress.Parse("198.51.100.201"));

        Assert.False(denied.Allowed);
        Assert.True(denied.RetryAfter > TimeSpan.Zero);
        Assert.True(denied.RetryAfter <= AdminAuthenticationDefaults.RateLimitRetryAfterCap);
    }

    [Fact]
    public void CheckGlobalBucket_RefillsOverTime()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        var defense = new AdminAuthFailureDefense(timeProvider);

        for (var i = 0; i < AdminAuthenticationDefaults.GlobalBucketBurst; i++)
        {
            defense.CheckGlobalBucket(isLoopback: false, IPAddress.Parse($"198.51.100.{i + 1}"));
        }

        Assert.False(defense.CheckGlobalBucket(isLoopback: false, RemoteA).Allowed);

        // 定常補充 1 トークン/秒——2 秒経過で 2 トークン再充填される。
        timeProvider.Advance(TimeSpan.FromSeconds(2));

        Assert.True(defense.CheckGlobalBucket(isLoopback: false, RemoteA).Allowed);
        Assert.True(defense.CheckGlobalBucket(isLoopback: false, RemoteA).Allowed);
        Assert.False(defense.CheckGlobalBucket(isLoopback: false, RemoteA).Allowed);
    }

    [Fact]
    public void CheckGlobalBucket_Loopback_AlwaysAllowed_EvenWhenBucketExhausted()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        var defense = new AdminAuthFailureDefense(timeProvider);

        for (var i = 0; i < AdminAuthenticationDefaults.GlobalBucketBurst; i++)
        {
            defense.CheckGlobalBucket(isLoopback: false, IPAddress.Parse($"198.51.100.{i + 1}"));
        }

        Assert.False(defense.CheckGlobalBucket(isLoopback: false, RemoteA).Allowed);
        // loopback はバケットの涸渇状態に関わらず常に許可される（決定 4）。
        Assert.True(defense.CheckGlobalBucket(isLoopback: true, IPAddress.Loopback).Allowed);
    }

    // ==== ③アカウント単位バックオフ ====

    [Fact]
    public void GetBackoffDelay_BelowThreshold_ReturnsZero()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        var defense = new AdminAuthFailureDefense(timeProvider);

        for (var i = 0; i < AdminAuthenticationDefaults.BackoffThreshold - 1; i++)
        {
            defense.RecordFailure("admin1", isLoopback: false, null);
        }

        Assert.Equal(TimeSpan.Zero, defense.GetBackoffDelay("admin1", isLoopback: false));
    }

    [Fact]
    public void RecordFailure_ExponentialGrowth_CappedAtBackoffCap()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        var defense = new AdminAuthFailureDefense(timeProvider);

        // k 回までは delay=0。k+1 回目以降は base * 2^(n-k) で増加し、cap で頭打ちになる。
        TimeSpan? lastDelay = null;
        var capReachedAtLeastOnce = false;

        for (var i = 0; i < AdminAuthenticationDefaults.BackoffThreshold + 10; i++)
        {
            var outcome = defense.RecordFailure("admin1", isLoopback: false, null);

            if (i >= AdminAuthenticationDefaults.BackoffThreshold)
            {
                Assert.True(outcome.NextDelay <= AdminAuthenticationDefaults.BackoffCap);

                if (lastDelay is { } previous && previous < AdminAuthenticationDefaults.BackoffCap)
                {
                    Assert.True(outcome.NextDelay >= previous, "遅延は cap に達するまで単調に増加するはず。");
                }

                if (outcome.NextDelay >= AdminAuthenticationDefaults.BackoffCap)
                {
                    capReachedAtLeastOnce = true;
                }
            }

            lastDelay = outcome.NextDelay;
        }

        Assert.True(capReachedAtLeastOnce, "十分な連続失敗回数の後は cap に到達するはず。");
    }

    [Fact]
    public void RecordFailure_CapReachedThisAttempt_TrueOnlyOnceCapIsInEffect()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        var defense = new AdminAuthFailureDefense(timeProvider);

        // k 回目までは cap に達しない。
        for (var i = 0; i < AdminAuthenticationDefaults.BackoffThreshold; i++)
        {
            var outcome = defense.RecordFailure("admin1", isLoopback: false, null);
            Assert.False(outcome.CapReachedThisAttempt);
        }

        // 十分な回数を重ねれば cap 到達フラグが立つ試行が現れる。
        var sawCapReached = false;
        for (var i = 0; i < 15; i++)
        {
            var outcome = defense.RecordFailure("admin1", isLoopback: false, null);
            if (outcome.CapReachedThisAttempt)
            {
                sawCapReached = true;
            }
        }

        Assert.True(sawCapReached);
    }

    [Fact]
    public void RecordSuccess_ResetsBackoffState()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        var defense = new AdminAuthFailureDefense(timeProvider);

        for (var i = 0; i < AdminAuthenticationDefaults.BackoffThreshold + 2; i++)
        {
            defense.RecordFailure("admin1", isLoopback: false, null);
        }

        Assert.True(defense.GetBackoffDelay("admin1", isLoopback: false) > TimeSpan.Zero);

        defense.RecordSuccess("admin1", isLoopback: false);

        Assert.Equal(TimeSpan.Zero, defense.GetBackoffDelay("admin1", isLoopback: false));
    }

    [Fact]
    public void GetBackoffDelay_IdleDecay_ResetsAfterInactivityWindow()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        var defense = new AdminAuthFailureDefense(timeProvider);

        for (var i = 0; i < AdminAuthenticationDefaults.BackoffThreshold + 2; i++)
        {
            defense.RecordFailure("admin1", isLoopback: false, null);
        }

        Assert.True(defense.GetBackoffDelay("admin1", isLoopback: false) > TimeSpan.Zero);

        timeProvider.Advance(AdminAuthenticationDefaults.BackoffIdleDecay + TimeSpan.FromSeconds(1));

        // アイドル減衰（決定 3）: 無失敗が窓を超えて継続したら n は 0 相当に戻る。
        Assert.Equal(TimeSpan.Zero, defense.GetBackoffDelay("admin1", isLoopback: false));
    }

    [Fact]
    public void BackoffState_LoopbackAndRemote_AreIndependentCompositeKeys()
    {
        // 決定 3: キーは (アカウント × loopback/remote の別) の複合キー。リモート経由の失敗は
        // loopback 側の待ち時間に一切影響しない（ADR-0010 決定 1 の loopback 復旧経路の維持）。
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        var defense = new AdminAuthFailureDefense(timeProvider);

        for (var i = 0; i < AdminAuthenticationDefaults.BackoffThreshold + 5; i++)
        {
            defense.RecordFailure("admin1", isLoopback: false, RemoteA);
        }

        Assert.True(defense.GetBackoffDelay("admin1", isLoopback: false) > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, defense.GetBackoffDelay("admin1", isLoopback: true));
    }

    [Fact]
    public void BackoffState_UsernameNormalization_IsCaseAndWhitespaceInsensitive()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        var defense = new AdminAuthFailureDefense(timeProvider);

        for (var i = 0; i < AdminAuthenticationDefaults.BackoffThreshold + 1; i++)
        {
            defense.RecordFailure("Admin1", isLoopback: false, null);
        }

        // 異なる大小文字・前後空白でも同一アカウントとして扱われる（IAdminAccountStore と同じ正規化）。
        Assert.True(defense.GetBackoffDelay(" admin1 ", isLoopback: false) > TimeSpan.Zero);
    }

    // ==== 原子的更新（委任事項 1） ====

    [Fact]
    public async Task RecordFailure_ConcurrentCallsOnSameKey_NoLostUpdates()
    {
        var defense = new AdminAuthFailureDefense();
        const int concurrentAttempts = 200;

        var tasks = Enumerable.Range(0, concurrentAttempts)
            .Select(i => Task.Run(() => defense.RecordFailure("admin1", isLoopback: false, IPAddress.Parse($"10.0.{i / 255}.{i % 255}"))))
            .ToArray();

        var outcomes = await Task.WhenAll(tasks);

        // 全試行が取りこぼしなく反映されていること——最大到達 N が試行数と一致する
        // （分散送信元からの同時失敗による lost update がないことの直接証拠）。
        Assert.Equal(concurrentAttempts, outcomes.Max(o => o.N));

        // N の集合が 1..concurrentAttempts の連番であること（重複・欠番なし = 完全な原子性）。
        var distinctNs = outcomes.Select(o => o.N).OrderBy(n => n).ToArray();
        Assert.Equal(Enumerable.Range(1, concurrentAttempts), distinctNs);
    }

    // ==== 能動通知への昇格（決定 6） ====

    [Fact]
    public void GetBackoffEscalations_CapReachedContinuouslyBeyondThreshold_ReportsEscalation()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        var defense = new AdminAuthFailureDefense(timeProvider);

        // cap に到達するまで十分な回数失敗させる。
        for (var i = 0; i < AdminAuthenticationDefaults.BackoffThreshold + 10; i++)
        {
            defense.RecordFailure("admin1", isLoopback: false, RemoteA);
        }

        Assert.Empty(defense.GetBackoffEscalations());

        // cap 到達状態のまま昇格閾値を超えて時間が経過——ただしアイドル減衰窓は超えない間隔で
        // 失敗を継続する（cap に張り付いたまま経過時間だけを進める）。
        timeProvider.Advance(AdminAuthenticationDefaults.EscalationThreshold + TimeSpan.FromMinutes(1));
        defense.RecordFailure("admin1", isLoopback: false, RemoteA);

        var escalations = defense.GetBackoffEscalations();
        var escalation = Assert.Single(escalations);
        Assert.Equal("admin1", escalation.UsernameNormalized);
        Assert.False(escalation.IsLoopback);
        Assert.Contains(RemoteA.ToString(), escalation.RecentSourceAddresses);
    }

    [Fact]
    public void GetIpRateLimitEscalations_ContinuousDenialAcrossManyWindowsBeyondThreshold_ReportsEscalation()
    {
        // 固定窓方式は「窓の境界で必ず 1 件は通す」性質を持つため、単純に「最後に許可された時刻」を
        // ストリークの解除条件にすると持続的な攻撃下でも昇格が絶対に成立しない
        // （AdminAuthFailureDefense.CheckIpRateLimit の窓ロールオーバー時のコメント参照）。
        // 本テストは、各窓を毎回上限まで飽和させ続ける持続的な攻撃を昇格閾値を超える期間
        // シミュレートし、ストリークが窓をまたいで引き継がれることを確認する。
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        var defense = new AdminAuthFailureDefense(timeProvider);

        var elapsed = TimeSpan.Zero;
        var targetElapsed = AdminAuthenticationDefaults.EscalationThreshold + TimeSpan.FromMinutes(1);
        var windowStep = AdminAuthenticationDefaults.IpRateLimitWindow + TimeSpan.FromSeconds(1);

        while (elapsed < targetElapsed)
        {
            for (var i = 0; i < AdminAuthenticationDefaults.IpRateLimitMaxAttempts; i++)
            {
                defense.CheckIpRateLimit(RemoteA, isLoopback: false);
            }

            var decision = defense.CheckIpRateLimit(RemoteA, isLoopback: false);
            Assert.False(decision.Allowed);

            timeProvider.Advance(windowStep);
            elapsed += windowStep;
        }

        var escalation = Assert.Single(defense.GetIpRateLimitEscalations());
        Assert.Equal(RemoteA.ToString(), escalation.RemoteAddress);
    }

    [Fact]
    public void GetIpRateLimitEscalations_GenuineRecoveryWindow_ClearsStreak()
    {
        // 対比テスト: 上限に達しない（実需要が閾値未満だった）窓を一度でも挟めば、
        // ストリークは真の鎮静化として解除される。
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        var defense = new AdminAuthFailureDefense(timeProvider);

        for (var i = 0; i < AdminAuthenticationDefaults.IpRateLimitMaxAttempts; i++)
        {
            defense.CheckIpRateLimit(RemoteA, isLoopback: false);
        }

        Assert.False(defense.CheckIpRateLimit(RemoteA, isLoopback: false).Allowed);

        timeProvider.Advance(AdminAuthenticationDefaults.IpRateLimitWindow + TimeSpan.FromSeconds(1));

        // 新しい窓で 1 回だけ試行し（上限未満のまま）、窓を完了させる。
        Assert.True(defense.CheckIpRateLimit(RemoteA, isLoopback: false).Allowed);

        timeProvider.Advance(AdminAuthenticationDefaults.IpRateLimitWindow + TimeSpan.FromSeconds(1));
        defense.CheckIpRateLimit(RemoteA, isLoopback: false);

        Assert.Empty(defense.GetIpRateLimitEscalations());
    }

    [Fact]
    public void GetGlobalBucketEscalation_SustainedExhaustionBeyondThreshold_ReportsEscalation()
    {
        // トークンバケットは補充速度分のトリクル許可を必ず伴うため（AdminAuthFailureDefense.
        // CheckGlobalBucket のコメント参照）、持続的な攻撃（補充速度を上回るペースでの試行）を
        // 昇格閾値を超える期間シミュレートし、ストリークが「バーストまで完全回復しない限り」
        // 維持されることを確認する。
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        var defense = new AdminAuthFailureDefense(timeProvider);

        for (var i = 0; i < AdminAuthenticationDefaults.GlobalBucketBurst; i++)
        {
            defense.CheckGlobalBucket(isLoopback: false, IPAddress.Parse($"198.51.100.{i + 1}"));
        }

        Assert.False(defense.CheckGlobalBucket(isLoopback: false, RemoteA).Allowed);
        Assert.Null(defense.GetGlobalBucketEscalation());

        var elapsed = TimeSpan.Zero;
        var targetElapsed = AdminAuthenticationDefaults.EscalationThreshold + TimeSpan.FromMinutes(1);
        var step = TimeSpan.FromSeconds(1);

        while (elapsed < targetElapsed)
        {
            timeProvider.Advance(step);
            elapsed += step;

            // 補充される 1 トークン/秒を即座に消費しつつ、なお 1 回は拒否される
            // （バーストまでの完全回復を許さないペース）。
            defense.CheckGlobalBucket(isLoopback: false, RemoteA);
            var decision = defense.CheckGlobalBucket(isLoopback: false, RemoteA);
            Assert.False(decision.Allowed);
        }

        var escalation = defense.GetGlobalBucketEscalation();
        Assert.NotNull(escalation);
        Assert.Contains(RemoteA.ToString(), escalation!.RecentSourceAddresses);
    }

    [Fact]
    public void GetGlobalBucketEscalation_FullyRefilledBucket_ClearsStreak()
    {
        // 対比テスト: 需要が途絶えバーストまで完全に再充填されれば、真の鎮静化としてストリークが
        // 解除される。
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        var defense = new AdminAuthFailureDefense(timeProvider);

        for (var i = 0; i < AdminAuthenticationDefaults.GlobalBucketBurst; i++)
        {
            defense.CheckGlobalBucket(isLoopback: false, IPAddress.Parse($"198.51.100.{i + 1}"));
        }

        Assert.False(defense.CheckGlobalBucket(isLoopback: false, RemoteA).Allowed);

        // バースト全量が再充填されるのに十分な時間だけ経過させ、その後に 1 回だけ試行する。
        timeProvider.Advance(TimeSpan.FromSeconds(AdminAuthenticationDefaults.GlobalBucketBurst + 5));
        Assert.True(defense.CheckGlobalBucket(isLoopback: false, RemoteA).Allowed);

        Assert.Null(defense.GetGlobalBucketEscalation());
    }
}
