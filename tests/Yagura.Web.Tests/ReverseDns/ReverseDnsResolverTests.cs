using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Yagura.Web.Diagnostics;
using Yagura.Web.ReverseDns;

namespace Yagura.Web.Tests.ReverseDns;

/// <summary>
/// <see cref="ReverseDnsResolver"/> の単体テスト（ADR-0007）。
/// </summary>
/// <remarks>
/// 本テストの中核は「<b>オフ時・対象帯域外の IP で下位の解決 API へ到達しない</b>」の固定
/// （ADR-0007 決定 4。security.md §1.1 の外向き通信台帳の実効性を担う——解決 API の呼び出しは
/// <see cref="IReverseDnsLookup"/> 実装 1 点に集約されており、偽実装が「呼ばれないこと」を検証する）。
/// </remarks>
public sealed class ReverseDnsResolverTests
{
    // ---- テスト用の偽実装 ----

    private sealed class FakeLookup : IReverseDnsLookup
    {
        private readonly Func<IPAddress, Task<string?>> _handler;

        public FakeLookup(Func<IPAddress, Task<string?>>? handler = null)
        {
            _handler = handler ?? (_ => Task.FromResult<string?>(null));
        }

        public ConcurrentQueue<IPAddress> Queries { get; } = new();

        public Task<string?> QueryPtrAsync(IPAddress address, CancellationToken cancellationToken)
        {
            Queries.Enqueue(address);
            return _handler(address);
        }
    }

    /// <summary>手動で進める時刻源（TTL 検証用。時間窓は 1 つの基準時刻から構築する——conventions.md）。</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 7, 7, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private static ReverseDnsResolverLimits TestLimits(
        int maxCacheEntries = 100,
        int maxConcurrentLookups = 4,
        TimeSpan? lookupTimeout = null,
        TimeSpan? positiveTtl = null,
        TimeSpan? negativeTtl = null) =>
        new(
            PositiveTtl: positiveTtl ?? TimeSpan.FromMinutes(30),
            NegativeTtl: negativeTtl ?? TimeSpan.FromMinutes(5),
            MaxCacheEntries: maxCacheEntries,
            MaxConcurrentLookups: maxConcurrentLookups,
            LookupTimeout: lookupTimeout ?? TimeSpan.FromSeconds(10),
            // テストは束ねを待たない（0 = 即時通知）。束ねの実時間挙動は実利用で確認する（UI-10）。
            NotifyBatchInterval: TimeSpan.Zero);

    private static ReverseDnsResolver CreateResolver(
        FakeLookup lookup,
        ReverseDnsMetrics metrics,
        bool enabled = true,
        TimeProvider? timeProvider = null,
        ReverseDnsResolverLimits? limits = null) =>
        new(
            new ReverseDnsDisplayOptions(enabled),
            lookup,
            metrics,
            timeProvider ?? TimeProvider.System,
            limits ?? TestLimits());

    // ---- オフ時・対象帯域外はクエリ 0（ADR-0007 決定 4 の受け入れ条件） ----

    [Fact]
    public async Task Disabled_NeverInvokesLookupAndShowsNothing()
    {
        var lookup = new FakeLookup(_ => Task.FromResult<string?>("host.example"));
        using var metrics = new ReverseDnsMetrics();
        using var resolver = CreateResolver(lookup, metrics, enabled: false);

        Assert.Null(resolver.TryGetDisplayName("10.0.0.1"));
        await resolver.WaitForPendingLookupsAsync();

        Assert.Empty(lookup.Queries);
    }

    [Theory]
    // 対象帯域外（グローバル等）——クエリ自体を発しない（ADR-0007 決定 2 の帯域限定）。
    [InlineData("8.8.8.8")]
    [InlineData("203.0.113.7")]
    [InlineData("100.128.0.1")] // 100.64/10 のすぐ外（100.128/9 はグローバル側）
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:8.8.8.8")] // IPv4-mapped でもグローバルはグローバル（正規化して判定）
    public async Task OutOfRangeAddress_NeverInvokesLookup(string address)
    {
        var lookup = new FakeLookup();
        using var metrics = new ReverseDnsMetrics();
        using var resolver = CreateResolver(lookup, metrics);

        Assert.Null(resolver.TryGetDisplayName(address));
        await resolver.WaitForPendingLookupsAsync();

        Assert.Empty(lookup.Queries);
    }

    [Fact]
    public async Task NonIpValue_NeverInvokesLookup()
    {
        var lookup = new FakeLookup();
        using var metrics = new ReverseDnsMetrics();
        using var resolver = CreateResolver(lookup, metrics);

        Assert.Null(resolver.TryGetDisplayName("not-an-ip"));
        Assert.Null(resolver.TryGetDisplayName(""));
        await resolver.WaitForPendingLookupsAsync();

        Assert.Empty(lookup.Queries);
    }

    // ---- 帯域判定（ADR-0007 決定 2——プライベート/サイトローカル系のみ） ----

    [Theory]
    [InlineData("127.0.0.1")]      // ループバック
    [InlineData("::1")]            // IPv6 ループバック
    [InlineData("10.1.2.3")]       // RFC 1918
    [InlineData("172.16.0.1")]     // RFC 1918（下端）
    [InlineData("172.31.255.254")] // RFC 1918（上端）
    [InlineData("192.168.1.10")]   // RFC 1918
    [InlineData("100.64.0.1")]     // RFC 6598（下端）
    [InlineData("100.127.255.254")] // RFC 6598（上端）
    [InlineData("169.254.10.20")]  // リンクローカル
    [InlineData("fe80::1")]        // IPv6 リンクローカル
    [InlineData("fd12:3456::1")]   // ULA
    [InlineData("fc00::1")]        // ULA（下端）
    public void ResolvableRange_IsAccepted(string address)
    {
        Assert.True(ReverseDnsResolver.IsResolvableAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("172.32.0.1")]     // RFC 1918 のすぐ外
    [InlineData("172.15.255.255")] // RFC 1918 のすぐ外（下側）
    [InlineData("100.63.255.255")] // RFC 6598 のすぐ外（下側）
    [InlineData("11.0.0.1")]
    [InlineData("fe00::1")]        // ULA でもリンクローカルでもない
    public void OutOfRange_IsRejected(string address)
    {
        Assert.False(ReverseDnsResolver.IsResolvableAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task Ipv4MappedIpv6_IsNormalizedBeforeRangeCheckAndLookup()
    {
        var lookup = new FakeLookup(_ => Task.FromResult<string?>("sv-file01.corp.example"));
        using var metrics = new ReverseDnsMetrics();
        using var resolver = CreateResolver(lookup, metrics);

        Assert.Null(resolver.TryGetDisplayName("::ffff:10.0.0.1"));
        await resolver.WaitForPendingLookupsAsync();

        var query = Assert.Single(lookup.Queries);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), query);
        // 正規化後のキーで IPv4 表記・IPv4-mapped 表記のどちらからも同じキャッシュに命中する。
        Assert.Equal("sv-file01.corp.example", resolver.TryGetDisplayName("10.0.0.1"));
        Assert.Equal("sv-file01.corp.example", resolver.TryGetDisplayName("::ffff:10.0.0.1"));
    }

    // ---- キャッシュ充填型の基本動作 ----

    [Fact]
    public async Task ResolvedName_IsCachedAndCounted()
    {
        var lookup = new FakeLookup(_ => Task.FromResult<string?>("printer-01.corp.example"));
        using var metrics = new ReverseDnsMetrics();
        using var resolver = CreateResolver(lookup, metrics);

        // 1 回目はキャッシュミス（null を返しつつ背景解決を予約——描画は解決を待たない）。
        Assert.Null(resolver.TryGetDisplayName("192.168.1.10"));
        await resolver.WaitForPendingLookupsAsync();

        Assert.Equal("printer-01.corp.example", resolver.TryGetDisplayName("192.168.1.10"));
        Assert.Equal(1, metrics.ResolvedTotal);
        Assert.Single(lookup.Queries);

        // TTL 内の再取得は下位 API を呼ばない。
        Assert.Equal("printer-01.corp.example", resolver.TryGetDisplayName("192.168.1.10"));
        await resolver.WaitForPendingLookupsAsync();
        Assert.Single(lookup.Queries);
    }

    [Fact]
    public async Task NotFound_IsNegativelyCachedAndCounted()
    {
        var lookup = new FakeLookup(_ => Task.FromResult<string?>(null));
        using var metrics = new ReverseDnsMetrics();
        using var resolver = CreateResolver(lookup, metrics);

        Assert.Null(resolver.TryGetDisplayName("10.9.9.9"));
        await resolver.WaitForPendingLookupsAsync();

        Assert.Equal(1, metrics.NotFoundTotal);
        Assert.Single(lookup.Queries);

        // 負のキャッシュ: PTR 未登録の解決は数秒級を要し得るため、TTL 内は再解決しない
        // （ADR-0007 検証記録の前提条件）。
        Assert.Null(resolver.TryGetDisplayName("10.9.9.9"));
        await resolver.WaitForPendingLookupsAsync();
        Assert.Single(lookup.Queries);
    }

    [Fact]
    public async Task SocketFailure_IsCountedAsFailedAndNegativelyCached()
    {
        var lookup = new FakeLookup(_ => Task.FromException<string?>(new SocketException((int)SocketError.TryAgain)));
        using var metrics = new ReverseDnsMetrics();
        using var resolver = CreateResolver(lookup, metrics);

        Assert.Null(resolver.TryGetDisplayName("10.0.0.2"));
        await resolver.WaitForPendingLookupsAsync();

        Assert.Equal(1, metrics.FailedTotal);
        Assert.Null(resolver.TryGetDisplayName("10.0.0.2"));
        await resolver.WaitForPendingLookupsAsync();
        Assert.Single(lookup.Queries);
    }

    [Fact]
    public async Task ExpiredEntry_IsResolvedAgain()
    {
        var time = new ManualTimeProvider();
        var lookup = new FakeLookup(_ => Task.FromResult<string?>("host-a.corp.example"));
        using var metrics = new ReverseDnsMetrics();
        using var resolver = CreateResolver(
            lookup, metrics, timeProvider: time, limits: TestLimits(positiveTtl: TimeSpan.FromMinutes(30)));

        Assert.Null(resolver.TryGetDisplayName("10.0.0.3"));
        await resolver.WaitForPendingLookupsAsync();
        Assert.Equal("host-a.corp.example", resolver.TryGetDisplayName("10.0.0.3"));
        Assert.Single(lookup.Queries);

        // TTL 経過後はキャッシュ切れ扱いで再解決が走る（両端は 1 つの基準時刻からの構築）。
        time.Advance(TimeSpan.FromMinutes(31));
        Assert.Null(resolver.TryGetDisplayName("10.0.0.3"));
        await resolver.WaitForPendingLookupsAsync();
        Assert.Equal(2, lookup.Queries.Count);
    }

    // ---- 資源保護（ADR-0007 決定 3） ----

    [Fact]
    public async Task CacheLimitReached_SkipsNewLookupAndCounts()
    {
        var lookup = new FakeLookup(_ => Task.FromResult<string?>(null));
        using var metrics = new ReverseDnsMetrics();
        using var resolver = CreateResolver(lookup, metrics, limits: TestLimits(maxCacheEntries: 2));

        Assert.Null(resolver.TryGetDisplayName("10.0.0.10"));
        Assert.Null(resolver.TryGetDisplayName("10.0.0.11"));
        await resolver.WaitForPendingLookupsAsync();
        Assert.Equal(2, lookup.Queries.Count);

        // 上限到達後の新規 IP は見送り（クエリを発しない = IP のみ表示）+ skipped 計上。
        Assert.Null(resolver.TryGetDisplayName("10.0.0.12"));
        await resolver.WaitForPendingLookupsAsync();
        Assert.Equal(2, lookup.Queries.Count);
        Assert.Equal(1, metrics.SkippedTotal);
    }

    [Fact]
    public async Task ConcurrencyLimitReached_DefersWithoutCounting()
    {
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookup = new FakeLookup(_ => gate.Task);
        using var metrics = new ReverseDnsMetrics();
        using var resolver = CreateResolver(lookup, metrics, limits: TestLimits(maxConcurrentLookups: 1));

        Assert.Null(resolver.TryGetDisplayName("10.0.1.1")); // スロットを占有（上限計数は同期）
        Assert.Null(resolver.TryGetDisplayName("10.0.1.2")); // 延期（キューに積まない・計上しない）

        // 照会の発行は Task.Yield 越しの非同期のため、並列テスト実行でスレッドプールが
        // 逼迫していると即時には現れない——発行を待ってから件数を検証する（flake 対策。
        // 2 件目の延期判定自体は同期カウンタによるため、1 件に収束することは保証される）。
        await WaitUntilAsync(() => !lookup.Queries.IsEmpty);
        Assert.Single(lookup.Queries);
        Assert.Equal(0, metrics.SkippedTotal);

        gate.SetResult(null);
        await resolver.WaitForPendingLookupsAsync();

        // 延期分は次の表示更新（TryGetDisplayName）で自然に再試行される。
        Assert.Null(resolver.TryGetDisplayName("10.0.1.2"));
        await resolver.WaitForPendingLookupsAsync();
        Assert.Equal(2, lookup.Queries.Count);
    }

    [Fact]
    public async Task LookupTimeout_CountsFailedAndAppliesLateSuccessAfterCompletion()
    {
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookup = new FakeLookup(_ => gate.Task);
        using var metrics = new ReverseDnsMetrics();
        using var resolver = CreateResolver(
            lookup, metrics, limits: TestLimits(lookupTimeout: TimeSpan.FromMilliseconds(50)));

        Assert.Null(resolver.TryGetDisplayName("10.0.2.1"));
        await resolver.WaitForPendingLookupsAsync();

        // 打ち切り = failed 計上 + 負のキャッシュ（「待つのをやめる」——OS の解決は中断できない）。
        Assert.Equal(1, metrics.FailedTotal);
        Assert.Null(resolver.TryGetDisplayName("10.0.2.1"));

        // 完走した成功結果は事後にキャッシュへ反映される（追加の計上はない）。
        gate.SetResult("late-host.corp.example");
        await WaitUntilAsync(() => resolver.TryGetDisplayName("10.0.2.1") == "late-host.corp.example");
        Assert.Equal(1, metrics.FailedTotal);
        Assert.Equal(0, metrics.ResolvedTotal);
    }

    // ---- 無害化（ADR-0007 決定 5） ----

    [Theory]
    [InlineData("sv-file01.corp.example", "sv-file01.corp.example")]
    [InlineData("sv-file01.corp.example.", "sv-file01.corp.example")] // FQDN 末尾ドットのみ除去
    [InlineData("xn--28j2af.example", "xn--28j2af.example")]          // Punycode は復号せず原文のまま
    [InlineData("HOST-01", "HOST-01")]
    public void Sanitize_AcceptsLdhNames(string input, string expected)
    {
        Assert.Equal(expected, ReverseDnsResolver.Sanitize(input));
    }

    [Theory]
    [InlineData("host_01.example")]        // LDH 外（アンダースコア）
    [InlineData("ホスト.example")]          // 非 ASCII（Windows DNS の UTF-8 名前検査で載り得る生値）
    [InlineData("host<script>.example")]   // マークアップ気配のある値
    [InlineData("-leading.example")]       // ラベル先頭ハイフン
    [InlineData("trailing-.example")]      // ラベル末尾ハイフン
    [InlineData("a..b")]                   // 空ラベル
    [InlineData("")]
    public void Sanitize_RejectsNonLdhNames(string input)
    {
        Assert.Null(ReverseDnsResolver.Sanitize(input));
    }

    [Fact]
    public void Sanitize_RejectsNamesOver253Chars()
    {
        var longName = string.Join('.', Enumerable.Repeat(new string('a', 60), 5)); // 304 文字
        Assert.Null(ReverseDnsResolver.Sanitize(longName));
    }

    [Fact]
    public async Task UnsafeResolvedName_IsNotShownButCountedAsResolved()
    {
        var lookup = new FakeLookup(_ => Task.FromResult<string?>("bad_host.example"));
        using var metrics = new ReverseDnsMetrics();
        using var resolver = CreateResolver(lookup, metrics);

        Assert.Null(resolver.TryGetDisplayName("10.0.3.1"));
        await resolver.WaitForPendingLookupsAsync();

        // 解決は成功（resolved に計上）だが、無害化により表示しない（切り詰め・整形もしない）。
        Assert.Equal(1, metrics.ResolvedTotal);
        Assert.Null(resolver.TryGetDisplayName("10.0.3.1"));
        Assert.Single(lookup.Queries); // 正の TTL でキャッシュ済み——再解決の嵐にしない
    }

    // ---- 反映通知 ----

    [Fact]
    public async Task NamesUpdated_IsRaisedAfterResolution()
    {
        var lookup = new FakeLookup(_ => Task.FromResult<string?>("host-b.corp.example"));
        using var metrics = new ReverseDnsMetrics();
        using var resolver = CreateResolver(lookup, metrics);

        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        resolver.NamesUpdated += () => notified.TrySetResult();

        Assert.Null(resolver.TryGetDisplayName("10.0.4.1"));
        await resolver.WaitForPendingLookupsAsync();

        await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        // 遅延完走の反映は ContinueWith 経由の非同期のため、条件成立まで短い間隔で確認する
        // （上限つき——固定 sleep の 1 発勝負にしない）。
        for (var i = 0; i < 200; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("条件が時間内に成立しなかった");
    }
}
