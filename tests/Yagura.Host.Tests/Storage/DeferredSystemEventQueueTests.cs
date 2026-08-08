using Microsoft.Extensions.Time.Testing;
using Yagura.Host.Storage;
using Yagura.Storage;
using Yagura.Storage.Administration;
using Yagura.TestSupport.Fakes;
using Yagura.Web.Administration;

namespace Yagura.Host.Tests.Storage;

/// <summary>
/// <see cref="DeferredSystemEventQueue"/> と、<see cref="StorageInitializationCoordinator"/> による
/// フラッシュの検証（Issue #501）。
/// </summary>
/// <remarks>
/// <para>
/// 回帰の核心: SQLite から SQL Server へ昇格した直後の初回起動では、受信断区間の記録
/// （<c>IngestionHostedService</c> が受信開始直後に行う）が**スキーマ作成より前**にあるため
/// 確定的に失敗する。しかもその 1 回は「昇格に伴う受信断」——運用者が最も記録を期待する
/// 受信断である。初期化の完了後に書き直せることを機械検証する。
/// </para>
/// </remarks>
public sealed class DeferredSystemEventQueueTests
{
    private static readonly DateTimeOffset Baseline = DateTimeOffset.Parse("2026-08-02T10:02:39Z");

    private static SystemEvent DowntimeEvent(int minutes = 0) =>
        new(SystemEventKinds.DowntimeNormalStop,
            Baseline.AddMinutes(minutes),
            Baseline.AddMinutes(minutes).AddSeconds(2),
            Approximate: false);

    [Fact]
    public async Task FlushAsync_WritesDeferredEvents()
    {
        var queue = new DeferredSystemEventQueue();
        var store = new RecordingLogStore();

        Assert.True(queue.TryDefer(DowntimeEvent()));
        Assert.Equal(1, queue.PendingCount);

        var written = await queue.FlushAsync(store);

        Assert.Equal(1, written);
        Assert.Equal(0, queue.PendingCount);
        Assert.Single(store.Written);
        Assert.Equal(SystemEventKinds.DowntimeNormalStop, store.Written[0].Kind);
    }

    [Fact]
    public async Task FlushAsync_WhenWriteFails_KeepsEventForTheNextAttempt()
    {
        // 初期化は通ったが書き込みが落ちる状況（別の障害）。**捨てない**ことを固定する
        // ——ここで捨てると「初期化前だったから書けなかった」記録が黙って消える。
        var queue = new DeferredSystemEventQueue();
        var store = new RecordingLogStore { WritesFail = true };

        queue.TryDefer(DowntimeEvent());

        var written = await queue.FlushAsync(store);

        Assert.Equal(0, written);
        Assert.Equal(1, queue.PendingCount);

        // 復旧後の再試行で書ける。
        store.WritesFail = false;
        Assert.Equal(1, await queue.FlushAsync(store));
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void TryDefer_BeyondCapacity_CountsDroppedInsteadOfGrowing()
    {
        // 本クラスは「初期化前だったから書けなかった」だけを引き受ける小さな受け皿であり、
        // 保存先の長期障害に対する耐障害経路の代替ではない。青天井に積まず、
        // あふれた分は**黙って消さずに数える**。
        var queue = new DeferredSystemEventQueue();

        for (var i = 0; i < DeferredSystemEventQueue.Capacity; i++)
        {
            Assert.True(queue.TryDefer(DowntimeEvent(i)));
        }

        Assert.False(queue.TryDefer(DowntimeEvent(100)));
        Assert.Equal(DeferredSystemEventQueue.Capacity, queue.PendingCount);
        Assert.Equal(1, queue.DroppedCount);
    }

    [Fact]
    public async Task Coordinator_FlushesDeferredEvents_OnceLogStoreSchemaIsReady()
    {
        // 昇格直後の初回起動の再現: 1 回目はスキーマが無く初期化が失敗し、
        // 受信断イベントは預けられたまま。保存先が用意できた 2 回目で書き込まれる。
        var state = new StorageAvailabilityState();
        var time = new FakeTimeProvider(Baseline);
        var queue = new DeferredSystemEventQueue();
        var logStore = new RecordingLogStore { InitializeFails = true };
        var accountStore = new AlwaysOkAccountStore();

        var coordinator = new StorageInitializationCoordinator(
            logStore, accountStore, state, time, logger: null, deferredSystemEvents: queue);

        queue.TryDefer(DowntimeEvent());

        // 1 回目: スキーマ初期化に失敗する = 書き込み先が無い。預かったまま。
        await coordinator.TryInitializeAsync(force: true);
        Assert.False(coordinator.IsLogStoreInitialized);
        Assert.Equal(1, queue.PendingCount);
        Assert.Empty(logStore.Written);

        // 2 回目: 初期化が成功した直後にフラッシュされる。
        logStore.InitializeFails = false;
        await coordinator.TryInitializeAsync(force: true);

        Assert.True(coordinator.IsLogStoreInitialized);
        Assert.Equal(0, queue.PendingCount);
        Assert.Single(logStore.Written);
        Assert.Equal(SystemEventKinds.DowntimeNormalStop, logStore.Written[0].Kind);
    }

    [Fact]
    public async Task Coordinator_WithoutDeferredEvents_StillInitializesNormally()
    {
        // 預かりが無い経路（通常の再起動）で余計な副作用が起きないこと。
        var state = new StorageAvailabilityState();
        var time = new FakeTimeProvider(Baseline);
        var logStore = new RecordingLogStore();
        var coordinator = new StorageInitializationCoordinator(
            logStore, new AlwaysOkAccountStore(), state, time, logger: null, deferredSystemEvents: new DeferredSystemEventQueue());

        Assert.True(await coordinator.TryInitializeAsync(force: true));
        Assert.Empty(logStore.Written);
    }

    // ---- 初期化完了の待ち合わせ（Issue #510） ----

    [Fact]
    public async Task WaitForLogStoreInitialization_ReturnsFalse_WhenInitializationDoesNotSucceed()
    {
        // 保存先が落ちている間は初期化が成立しない。**無期限に待たない**ことを固定する
        // ——待ち続けると「復旧するまで保持期間削除が一切走らない」状態を作るため。
        var time = new FakeTimeProvider(Baseline);
        var coordinator = new StorageInitializationCoordinator(
            new RecordingLogStore { InitializeFails = true }, new AlwaysOkAccountStore(),
            new StorageAvailabilityState(), time);

        await coordinator.TryInitializeAsync(force: true);

        var waiting = coordinator.WaitForLogStoreInitializationAsync(TimeSpan.FromMinutes(2));
        time.Advance(TimeSpan.FromMinutes(2));

        Assert.False(await waiting);
    }

    [Fact]
    public async Task WaitForLogStoreInitialization_CompletesWhenSchemaBecomesReady()
    {
        // 昇格直後の初回起動の再現: 1 回目は失敗して待ち手はブロックされ、
        // 2 回目の成功で解ける（= 起動時キャッチアップがスキーマ作成後に走る）。
        var time = new FakeTimeProvider(Baseline);
        var logStore = new RecordingLogStore { InitializeFails = true };
        var coordinator = new StorageInitializationCoordinator(
            logStore, new AlwaysOkAccountStore(), new StorageAvailabilityState(), time);

        await coordinator.TryInitializeAsync(force: true);
        var waiting = coordinator.WaitForLogStoreInitializationAsync(TimeSpan.FromMinutes(2));
        Assert.False(waiting.IsCompleted);

        logStore.InitializeFails = false;
        await coordinator.TryInitializeAsync(force: true);

        Assert.True(await waiting);
    }

    [Fact]
    public async Task WaitForLogStoreInitialization_ReturnsImmediately_WhenAlreadyInitialized()
    {
        var time = new FakeTimeProvider(Baseline);
        var coordinator = new StorageInitializationCoordinator(
            new RecordingLogStore(), new AlwaysOkAccountStore(), new StorageAvailabilityState(), time);

        Assert.True(await coordinator.TryInitializeAsync(force: true));

        // 時間を進めずに完了する（Task.Delay の待ちに入っていない）。
        Assert.True(await coordinator.WaitForLogStoreInitializationAsync(TimeSpan.FromMinutes(2)));
    }

    private sealed class RecordingLogStore : LogStoreTestDouble
    {
        public List<SystemEvent> Written { get; } = [];

        public bool InitializeFails { get; set; }

        public bool WritesFail { get; set; }

        public override Task InitializeAsync(CancellationToken cancellationToken = default) =>
            InitializeFails
                ? throw new InvalidOperationException("テスト用の初期化失敗（スキーマ未作成の模擬）。")
                : Task.CompletedTask;

        public override Task WriteSystemEventAsync(SystemEvent systemEvent, CancellationToken cancellationToken = default)
        {
            if (WritesFail)
            {
                throw new InvalidOperationException("テスト用の書き込み失敗。");
            }

            Written.Add(systemEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysOkAccountStore : IAdminAccountStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

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
