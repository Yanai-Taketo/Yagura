using Microsoft.Extensions.Time.Testing;
using Yagura.Abstractions;
using Yagura.Host.Observability.ActiveNotification;
using Yagura.Host.Storage;
using Yagura.Storage;
using Yagura.Storage.Administration;
using Yagura.Web.Administration;

namespace Yagura.Host.Tests.Storage;

/// <summary>
/// <see cref="StorageInitializationCoordinator"/> の単体テスト（ADR-0023 決定 1。Issue #466）。
/// </summary>
/// <remarks>
/// 決定 1 が「委任へ落とさず決定の一部として実装する」と明記した 3 つの不変条件
/// （①単一実行 ②失敗の負のキャッシュ ③接続タイムアウトの明示上限）と、受け入れ基準②
/// （復旧後の自動回復・上限時間つき）・③（起動経路の接続試行の天井）を機械検証する。
/// </remarks>
public sealed class StorageInitializationCoordinatorTests
{
    private static StorageInitializationCoordinator CreateCoordinator(
        FailableStore logStore, FailableStore accountStore, StorageAvailabilityState state, FakeTimeProvider time) =>
        new(new FakeLogStore(logStore), new FakeAdminAccountStore(accountStore), state, time);

    [Fact]
    public async Task InitializationFailure_DoesNotThrow_AndMarksUnavailable()
    {
        // 回帰の核心（Issue #466）: 初期化失敗が例外として呼び出し側へ抜けると、起動シーケンスに
        // 置いた場合はプロセスが即死し、監視ループに置いた場合はループが止まる。
        var state = new StorageAvailabilityState();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-26T00:00:00Z"));
        var logStore = new FailableStore { ShouldFail = true };
        var accountStore = new FailableStore { ShouldFail = true };
        var coordinator = CreateCoordinator(logStore, accountStore, state, time);

        var initialized = await coordinator.TryInitializeAsync(force: true);

        Assert.False(initialized);
        Assert.False(coordinator.IsFullyInitialized);
        Assert.False(state.IsAdminAccountStoreAvailable);
        Assert.Equal(nameof(InvalidOperationException), state.Current.FailureKind);
    }

    [Fact]
    public async Task LogStoreFailureAlone_DoesNotMakeAppAuthUnavailable()
    {
        // StorageAvailabilityState が表すのは**アプリ独自認証の可用性**であり、ログストアの可否では
        // ない。ここを混ぜると、アカウント台帳は健全なのにログインが拒否される（同じ DB でも
        // 権限や既存スキーマの差で片方だけ落ちうる）。
        var state = new StorageAvailabilityState();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-26T00:00:00Z"));
        var logStore = new FailableStore { ShouldFail = true };
        var accountStore = new FailableStore { ShouldFail = false };
        var coordinator = CreateCoordinator(logStore, accountStore, state, time);

        var initialized = await coordinator.TryInitializeAsync(force: true);

        Assert.False(initialized);
        Assert.False(coordinator.IsLogStoreInitialized);
        Assert.True(coordinator.IsAdminAccountStoreInitialized);
        Assert.True(state.IsAdminAccountStoreAvailable);
    }

    [Fact]
    public async Task RepeatedAttemptsWithinRetryInterval_DoNotTouchTheStore()
    {
        // 不変条件②（失敗の負のキャッシュ）: 障害中の保存先へ毎周期接続を積まない。
        var state = new StorageAvailabilityState();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-26T00:00:00Z"));
        var logStore = new FailableStore { ShouldFail = true };
        var accountStore = new FailableStore { ShouldFail = true };
        var coordinator = CreateCoordinator(logStore, accountStore, state, time);

        await coordinator.TryInitializeAsync(force: true);
        var afterFirst = accountStore.AttemptCount;

        time.Advance(StorageInitializationCoordinator.RetryInterval - TimeSpan.FromSeconds(1));
        await coordinator.TryInitializeAsync();

        Assert.Equal(afterFirst, accountStore.AttemptCount);

        time.Advance(TimeSpan.FromSeconds(2));
        await coordinator.TryInitializeAsync();

        Assert.Equal(afterFirst + 1, accountStore.AttemptCount);
    }

    [Fact]
    public async Task StoreRecovers_AppAuthBecomesAvailableAgain_WithoutRestart()
    {
        // 受け入れ基準②: 復旧後にアプリ独自認証が自動回復すること。
        var state = new StorageAvailabilityState();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-26T00:00:00Z"));
        var logStore = new FailableStore { ShouldFail = true };
        var accountStore = new FailableStore { ShouldFail = true };
        var coordinator = CreateCoordinator(logStore, accountStore, state, time);

        await coordinator.TryInitializeAsync(force: true);
        Assert.False(state.IsAdminAccountStoreAvailable);

        logStore.ShouldFail = false;
        accountStore.ShouldFail = false;
        time.Advance(StorageInitializationCoordinator.RetryInterval);

        Assert.True(await coordinator.TryInitializeAsync());
        Assert.True(state.IsAdminAccountStoreAvailable);
        Assert.Null(state.Current.FailureKind);
    }

    [Fact]
    public async Task AlreadyInitialized_DoesNotReinitialize()
    {
        var state = new StorageAvailabilityState();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-26T00:00:00Z"));
        var logStore = new FailableStore();
        var accountStore = new FailableStore();
        var coordinator = CreateCoordinator(logStore, accountStore, state, time);

        Assert.True(await coordinator.TryInitializeAsync(force: true));
        var attempts = accountStore.AttemptCount;

        time.Advance(TimeSpan.FromHours(1));
        Assert.True(await coordinator.TryInitializeAsync(force: true));

        Assert.Equal(attempts, accountStore.AttemptCount);
    }

    [Fact]
    public async Task ConcurrentAttempts_RunOnlyOneInitialization()
    {
        // 不変条件①（単一実行）: SqlServerAdminAccountStore は LogStore と違い sp_getapplock を
        // 持たないため、DB 側では直列化されない——プロセス側で保証する必要がある。
        var state = new StorageAvailabilityState();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-26T00:00:00Z"));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logStore = new FailableStore { Gate = gate.Task };
        var accountStore = new FailableStore();
        var coordinator = CreateCoordinator(logStore, accountStore, state, time);

        var first = coordinator.TryInitializeAsync(force: true);
        // 先行の試行がゲート内で止まっていることを確かめてから 2 本目を投げる。
        while (logStore.AttemptCount == 0)
        {
            await Task.Yield();
        }

        var second = await coordinator.TryInitializeAsync(force: true);
        Assert.False(second);

        gate.SetResult();
        Assert.True(await first);
        Assert.Equal(1, logStore.AttemptCount);
    }

    [Fact]
    public void StartupProbeCeiling_StaysWithinTheDecidedTenSeconds()
    {
        // 受け入れ基準③: 起動シーケンス中の保存先接続試行の合計上限（決定 1 で 10 秒に固定済み）。
        // SCM の起動待ち 30 秒に対する余裕であり、値を緩める変更はこのテストで止まる。
        Assert.True(
            StorageInitializationCoordinator.StartupProbeCeiling <= TimeSpan.FromSeconds(10),
            $"起動経路の接続試行の天井が 10 秒を超えている: {StorageInitializationCoordinator.StartupProbeCeiling}");
    }

    [Fact]
    public void RecoveryUpperBound_StaysWithinTwoMinutes()
    {
        // 受け入れ基準②の「上限時間」: 回復契機は周期監視のみのため、復旧が観測されるまでの
        // 最悪時間は「周期 + 負のキャッシュ」で決まる。値を緩めると「復旧したのに使えないまま」の
        // 窓が伸びる（ADR-0023 が受け入れるリスクとして挙げた失敗様式）。
        var upperBound = ActiveNotificationConstants.PollInterval + StorageInitializationCoordinator.RetryInterval;

        Assert.True(
            upperBound <= TimeSpan.FromMinutes(2),
            $"保存先復旧からアプリ独自認証が回復するまでの上限が 2 分を超えている: {upperBound}");
    }

    /// <summary>初期化の成否と回数を制御できる差し替え可能な初期化実装。</summary>
    private sealed class FailableStore
    {
        public bool ShouldFail { get; set; }

        /// <summary>初期化を任意のタイミングまで止めるためのゲート（単一実行の検証用）。</summary>
        public Task? Gate { get; set; }

        public int AttemptCount { get; private set; }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            AttemptCount++;

            if (Gate is not null)
            {
                await Gate.ConfigureAwait(false);
            }

            if (ShouldFail)
            {
                throw new InvalidOperationException("テスト用の初期化失敗（保存先到達不能の模擬）。");
            }
        }
    }

    private sealed class FakeLogStore(FailableStore behavior) : ILogStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            behavior.InitializeAsync(cancellationToken);

        public Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<LogRecordSummary>> QueryLatestAsync(
            int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<LogRecordSummary>> QueryAsync(
            LogQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task WriteSystemEventAsync(SystemEvent systemEvent, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DeleteOlderThanResult> DeleteOlderThanAsync(
            DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LogStoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LogRecord?> FindByIdAsync(long id, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SystemEvent>> QuerySystemEventsAsync(
            DateTimeOffset? from, DateTimeOffset? to, int limit, TimeSpan timeout, string? kind = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SeverityCount>> QuerySeverityDistributionAsync(
            DateTimeOffset from, DateTimeOffset to, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SourceActivity>> QueryTopTalkersAsync(
            DateTimeOffset from, DateTimeOffset to, int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SourceActivity>> QuerySourceActivityAsync(
            int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeAdminAccountStore(FailableStore behavior) : IAdminAccountStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            behavior.InitializeAsync(cancellationToken);

        public Task<bool> HasAnyAccountAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AdminAccountRecord?> GetSoleAccountAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AdminAccountRecord?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpsertAsync(
            string username, string passwordHash, DateTimeOffset atUtc, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RecordSuccessfulLoginAsync(
            string username, DateTimeOffset atUtc, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
