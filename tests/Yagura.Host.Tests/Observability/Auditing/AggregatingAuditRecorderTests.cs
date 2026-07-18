using Microsoft.Extensions.Time.Testing;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Observability.Auditing;

namespace Yagura.Host.Tests.Observability.Auditing;

/// <summary>
/// <see cref="AggregatingAuditRecorder"/> の単体テスト（SEC-4。security.md §4.4。Issue #268）。
/// </summary>
/// <remarks>
/// 検証観点: (1) 閾値までは個別記録し希釈しない、(2) 閾値到達で集約モードへ入り以降は個別記録しない、
/// (3) 静穏窓の経過で集約サマリ（3012）を 1 件出しキーを破棄して個別記録へ復帰、(4) 集約中も件数を
/// 失わない（サマリの count が全件）、(5) 事象種別が変われば別キーで畳まない、(6) 集約対象外
/// （3007 等）・送信元なしは素通り、(7) 認証失敗の集約で試行された利用者名の次元を保持する、
/// (8) 停止時に集約中の全キーをフラッシュする。時刻は <see cref="FakeTimeProvider"/> で決定的に制御する。
/// </remarks>
public sealed class AggregatingAuditRecorderTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 9, 0, 0, TimeSpan.Zero);

    /// <summary>内側の記録先を代替する捕捉用ダブル。渡された監査事象をそのまま溜める。</summary>
    private sealed class CapturingRecorder : IAuditRecorder
    {
        public List<AuditEvent> Events { get; } = new();

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private static AuditEvent Rejection(string address = "203.0.113.5", string? username = null,
        AuditEventKind kind = AuditEventKind.AppAuthenticationLoginFailed) => new(
            OccurredAt: Now,
            Kind: kind,
            RemoteAddress: address,
            RemotePort: 54321,
            AttemptedPath: "/admin",
            ReachedListenerPort: 8080,
            Detail: username is null ? "login failed" : $"login failed username={username}");

    private static async Task RecordManyAsync(AggregatingAuditRecorder recorder, int count,
        Func<int, AuditEvent> factory)
    {
        for (var i = 0; i < count; i++)
        {
            await recorder.RecordAsync(factory(i));
        }
    }

    [Fact]
    public async Task BelowThreshold_RecordsIndividually()
    {
        var inner = new CapturingRecorder();
        var time = new FakeTimeProvider(Now);
        await using var recorder = new AggregatingAuditRecorder(inner, time);

        await RecordManyAsync(recorder, AuditAggregationDefaults.AggregationThreshold - 1, _ => Rejection());

        Assert.Equal(AuditAggregationDefaults.AggregationThreshold - 1, inner.Events.Count);
        Assert.All(inner.Events, e => Assert.NotEqual(AuditEventKind.RejectionAggregated, e.Kind));
    }

    [Fact]
    public async Task AtThreshold_EntersAggregation_StopsIndividualRecording()
    {
        var inner = new CapturingRecorder();
        var time = new FakeTimeProvider(Now);
        await using var recorder = new AggregatingAuditRecorder(inner, time);

        // 閾値の 2 倍を投入。閾値-1 件までは個別記録、閾値到達以降は集約されて個別記録されない。
        await RecordManyAsync(recorder, AuditAggregationDefaults.AggregationThreshold * 2, _ => Rejection());

        Assert.Equal(AuditAggregationDefaults.AggregationThreshold - 1, inner.Events.Count);
    }

    [Fact]
    public async Task QuietWindowElapsed_FlushesSummary_ThenReturnsToIndividual()
    {
        var inner = new CapturingRecorder();
        var time = new FakeTimeProvider(Now);
        await using var recorder = new AggregatingAuditRecorder(inner, time);
        await recorder.StartAsync(CancellationToken.None);

        const int total = 25;
        await RecordManyAsync(recorder, total, _ => Rejection(username: "administrator"));
        inner.Events.Clear();

        // 静穏窓を経過させ、周期スキャンを走らせる。
        time.Advance(AuditAggregationDefaults.QuietWindow + TimeSpan.FromSeconds(1));
        await recorder.FlushStaleAsync();

        var summary = Assert.Single(inner.Events);
        Assert.Equal(AuditEventKind.RejectionAggregated, summary.Kind);
        Assert.Contains($"count={total}", summary.Detail);
        Assert.Contains("administrator", summary.Detail);

        // 復帰後は再び個別記録される。
        inner.Events.Clear();
        await recorder.RecordAsync(Rejection());
        var next = Assert.Single(inner.Events);
        Assert.NotEqual(AuditEventKind.RejectionAggregated, next.Kind);

        await recorder.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SubThresholdEntry_AfterWindowElapses_IsSweptFromState()
    {
        // #313: 閾値未満のまま集約窓が失効した非集約キーは、集約サマリを出さずに回収される。
        // 異なる送信元 IP から閾値未満の拒否が続いても _states が単調増加しないことを固定する。
        var inner = new CapturingRecorder();
        var time = new FakeTimeProvider(Now);
        await using var recorder = new AggregatingAuditRecorder(inner, time);

        await recorder.RecordAsync(Rejection(address: "203.0.113.9"));
        Assert.Equal(1, recorder.TrackedStateCount);

        time.Advance(AuditAggregationDefaults.AggregationWindow + TimeSpan.FromSeconds(1));
        await recorder.FlushStaleAsync();

        Assert.Equal(0, recorder.TrackedStateCount);
        Assert.DoesNotContain(inner.Events, e => e.Kind == AuditEventKind.RejectionAggregated);
    }

    [Fact]
    public async Task CountIsNeverLost_SummaryReflectsAllEvents()
    {
        var inner = new CapturingRecorder();
        var time = new FakeTimeProvider(Now);
        await using var recorder = new AggregatingAuditRecorder(inner, time);

        const int total = 100;
        await RecordManyAsync(recorder, total, _ => Rejection());

        // 個別記録数 + サマリの集約数 = 総数（一件も失われない）。
        var individual = inner.Events.Count;
        await recorder.StopAsync(CancellationToken.None);
        var summary = inner.Events.Single(e => e.Kind == AuditEventKind.RejectionAggregated);
        Assert.Contains($"count={total}", summary.Detail);
        Assert.Equal(AuditAggregationDefaults.AggregationThreshold - 1, individual);
    }

    [Fact]
    public async Task DifferentKind_SameAddress_NotFoldedTogether()
    {
        var inner = new CapturingRecorder();
        var time = new FakeTimeProvider(Now);
        await using var recorder = new AggregatingAuditRecorder(inner, time);

        // 別種別を交互に閾値未満ずつ投入。キーが別なのでどちらも閾値に達せず、全件個別記録される。
        for (var i = 0; i < AuditAggregationDefaults.AggregationThreshold - 1; i++)
        {
            await recorder.RecordAsync(Rejection(kind: AuditEventKind.AppAuthenticationLoginFailed));
            await recorder.RecordAsync(Rejection(kind: AuditEventKind.CircuitOriginRejected));
        }

        Assert.Equal((AuditAggregationDefaults.AggregationThreshold - 1) * 2, inner.Events.Count);
        Assert.DoesNotContain(inner.Events, e => e.Kind == AuditEventKind.RejectionAggregated);
    }

    [Fact]
    public async Task NonAggregatedKind_PassesThrough()
    {
        var inner = new CapturingRecorder();
        var time = new FakeTimeProvider(Now);
        await using var recorder = new AggregatingAuditRecorder(inner, time);

        // 3007（グローバルバケット）は集約対象外——大量に来ても全件個別記録される。
        await RecordManyAsync(recorder, AuditAggregationDefaults.AggregationThreshold * 3,
            _ => Rejection(kind: AuditEventKind.AdminAuthRateLimited));

        Assert.Equal(AuditAggregationDefaults.AggregationThreshold * 3, inner.Events.Count);
        Assert.All(inner.Events, e => Assert.Equal(AuditEventKind.AdminAuthRateLimited, e.Kind));
    }

    [Fact]
    public async Task NoRemoteAddress_PassesThrough()
    {
        var inner = new CapturingRecorder();
        var time = new FakeTimeProvider(Now);
        await using var recorder = new AggregatingAuditRecorder(inner, time);

        var noAddress = new AuditEvent(Now, AuditEventKind.AppAuthenticationLoginFailed,
            RemoteAddress: null, RemotePort: null);
        await RecordManyAsync(recorder, AuditAggregationDefaults.AggregationThreshold * 2, _ => noAddress);

        Assert.Equal(AuditAggregationDefaults.AggregationThreshold * 2, inner.Events.Count);
    }

    [Fact]
    public async Task DifferentAddress_SameKind_AggregatedSeparately()
    {
        var inner = new CapturingRecorder();
        var time = new FakeTimeProvider(Now);
        await using var recorder = new AggregatingAuditRecorder(inner, time);

        await RecordManyAsync(recorder, AuditAggregationDefaults.AggregationThreshold * 2,
            _ => Rejection(address: "198.51.100.1"));
        await RecordManyAsync(recorder, AuditAggregationDefaults.AggregationThreshold * 2,
            _ => Rejection(address: "198.51.100.2"));

        await recorder.StopAsync(CancellationToken.None);

        var summaries = inner.Events.Where(e => e.Kind == AuditEventKind.RejectionAggregated).ToList();
        Assert.Equal(2, summaries.Count);
        Assert.Contains(summaries, s => s.RemoteAddress == "198.51.100.1");
        Assert.Contains(summaries, s => s.RemoteAddress == "198.51.100.2");
    }

    [Fact]
    public async Task UsernameDimensionPreserved_InSummary()
    {
        var inner = new CapturingRecorder();
        var time = new FakeTimeProvider(Now);
        await using var recorder = new AggregatingAuditRecorder(inner, time);

        var usernames = new[] { "admin", "root", "guest", "operator" };
        await RecordManyAsync(recorder, AuditAggregationDefaults.AggregationThreshold * 3,
            i => Rejection(username: usernames[i % usernames.Length]));

        await recorder.StopAsync(CancellationToken.None);

        var summary = inner.Events.Single(e => e.Kind == AuditEventKind.RejectionAggregated);
        foreach (var u in usernames)
        {
            Assert.Contains(u, summary.Detail);
        }
    }

    [Fact]
    public async Task ManyDistinctUsernames_TruncatedWithOverflowNote()
    {
        var inner = new CapturingRecorder();
        var time = new FakeTimeProvider(Now);
        await using var recorder = new AggregatingAuditRecorder(inner, time);

        var distinct = AuditAggregationDefaults.MaxDistinctUsernamesInSummary + 5;
        await RecordManyAsync(recorder, distinct + AuditAggregationDefaults.AggregationThreshold,
            i => Rejection(username: $"user{i % distinct:D3}"));

        await recorder.StopAsync(CancellationToken.None);

        var summary = inner.Events.Single(e => e.Kind == AuditEventKind.RejectionAggregated);
        Assert.Contains("...(+", summary.Detail);
    }

    [Fact]
    public async Task WindowNotExceeded_ScatteredFailures_RestartWindow()
    {
        var inner = new CapturingRecorder();
        var time = new FakeTimeProvider(Now);
        await using var recorder = new AggregatingAuditRecorder(inner, time);

        // 閾値未満投入 → 窓を跨いで間隔を空ける → また閾値未満。集約に入らず全件個別記録される。
        await RecordManyAsync(recorder, AuditAggregationDefaults.AggregationThreshold - 1, _ => Rejection());
        time.Advance(AuditAggregationDefaults.AggregationWindow + TimeSpan.FromSeconds(1));
        await RecordManyAsync(recorder, AuditAggregationDefaults.AggregationThreshold - 1, _ => Rejection());

        Assert.Equal((AuditAggregationDefaults.AggregationThreshold - 1) * 2, inner.Events.Count);
        Assert.DoesNotContain(inner.Events, e => e.Kind == AuditEventKind.RejectionAggregated);
    }
}
