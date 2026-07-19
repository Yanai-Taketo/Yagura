using Microsoft.Extensions.Logging.Testing;
using Yagura.Host.Configuration;
using Yagura.Host.Retention;
using Yagura.Host.Tests.Configuration;
using Yagura.Storage;

namespace Yagura.Host.Tests.Retention;

/// <summary>
/// 保持期間まわり(M5-1)のテスト: 設定キーの読み込み・宣言表の網羅・スケジューラの契機。
/// </summary>
[Collection(ConfigurationEnvironmentVariableTestCollection.Name)]
public sealed class RetentionTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-retention-test-{Guid.NewGuid():N}");

    public RetentionTests()
    {
        Directory.CreateDirectory(_dataRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    private void WriteConfigurationFile(string json) =>
        File.WriteAllText(Path.Combine(_dataRoot, YaguraConfigurationLoader.ConfigurationFileName), json);

    // ------------------------------------------------------------------
    // 設定キーの宣言表の網羅(再発防止: M4-1 で TCP キーの登録漏れが実際に起きた)
    // ------------------------------------------------------------------

    [Fact]
    public void EveryKnownKey_HasDeclaredReloadEffect()
    {
        foreach (var key in YaguraConfigurationLoader.KnownKeys)
        {
            // 未登録なら KeyNotFoundException で落ちる(= 新キー追加時の宣言忘れを CI が検出する)。
            _ = ConfigurationKeyMetadata.GetReloadEffect(key);
        }
    }

    [Fact]
    public void EveryDeclaredKey_IsKnownToLoader()
    {
        foreach (var key in ConfigurationKeyMetadata.RegisteredKeys)
        {
            Assert.Contains(key, YaguraConfigurationLoader.KnownKeys);
        }
    }

    // 配列キーはスカラーとは別の表（KnownArrayKeys ⇔ RegisteredArrayKeys）で管理する
    // ——.NET 構成システム上インデックス付きリーフへ展開されるため KnownKeys には現れない。
    // 上の 2 つと同じ双方向一致を、配列側にも掛ける（ADR-0017 委任 9。2026-07-19）。

    [Fact]
    public void EveryKnownArrayKey_HasDeclaredReloadEffect()
    {
        foreach (var key in YaguraConfigurationLoader.KnownArrayKeys)
        {
            _ = ConfigurationKeyMetadata.GetReloadEffect(key);
        }
    }

    [Fact]
    public void EveryDeclaredArrayKey_IsKnownToLoader()
    {
        // 配列キーには 2 つの形がある——スカラーの配列（KnownArrayKeys）と、オブジェクトの
        // 配列（KnownObjectArrayKeys。ADR-0018）。平坦化の形が違うので未知キー判定は別系統だが、
        // **反映方式の宣言単位はどちらも「論理キー 1 つ」で同じ**ため反映方式表は 1 つに保つ。
        var known = YaguraConfigurationLoader.KnownArrayKeys
            .Concat(YaguraConfigurationLoader.KnownObjectArrayKeys.Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var key in ConfigurationKeyMetadata.RegisteredArrayKeys)
        {
            Assert.Contains(key, known);
        }
    }

    [Fact]
    public void EveryKnownObjectArrayKey_HasDeclaredReloadEffect()
    {
        foreach (var key in YaguraConfigurationLoader.KnownObjectArrayKeys.Keys)
        {
            _ = ConfigurationKeyMetadata.GetReloadEffect(key);
        }
    }

    [Fact]
    public void ScalarAndObjectArrayKeySets_DoNotOverlap()
    {
        // 同じキーが両系統にあると未知キー判定の経路が二重になり、片方だけ更新した際に
        // 挙動が食い違う。
        Assert.Empty(YaguraConfigurationLoader.KnownArrayKeys
            .Intersect(YaguraConfigurationLoader.KnownObjectArrayKeys.Keys, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScalarAndArrayKeyTables_DoNotOverlap()
    {
        // 同じキーが両表にあると GetReloadEffect の解決順に依存した挙動になる。
        Assert.Empty(ConfigurationKeyMetadata.RegisteredKeys
            .Intersect(ConfigurationKeyMetadata.RegisteredArrayKeys, StringComparer.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------
    // Retention:Days / Retention:ExecutionTimeOfDay の読み込み(§1「既定値で継続」分類)
    // ------------------------------------------------------------------

    [Fact]
    public void Load_RetentionDaysValid_Resolved()
    {
        WriteConfigurationFile("""{ "Retention": { "Days": "30", "ExecutionTimeOfDay": "02:15" } }""");

        var result = YaguraConfigurationLoader.Load(_dataRoot, new FakeLogger());

        Assert.Equal(30, result.Configuration.RetentionDays);
        Assert.Equal(new TimeOnly(2, 15), result.Configuration.RetentionExecutionTimeOfDay);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.UnknownKeys);
    }

    [Fact]
    public void Load_RetentionDaysUnset_DefaultsTo30Days()
    {
        var result = YaguraConfigurationLoader.Load(_dataRoot, new FakeLogger());

        // DB-1(既定値): 2026-07-05 オーナー決定により 30 日(PR #64 決定記録)。
        Assert.Equal(30, result.Configuration.RetentionDays);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("0")]
    public void Load_RetentionDaysInvalid_WarnsAndFallsBackToNoDeletion(string invalid)
    {
        WriteConfigurationFile($$"""{ "Retention": { "Days": "{{invalid}}" } }""");

        var result = YaguraConfigurationLoader.Load(_dataRoot, new FakeLogger());

        // 不正値は既定 30 日へは自動フォールバックしない——意図しない自動削除の開始を
        // 避ける安全側の判断(本 Issue の設計判断。YaguraConfigurationLoader のコメント参照)。
        Assert.Null(result.Configuration.RetentionDays);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Retention:Days", warning.Key);
        Assert.Equal(invalid, warning.InvalidValue);
    }

    [Fact]
    public void Load_RetentionTimeInvalid_WarnsAndFallsBackToDefault()
    {
        WriteConfigurationFile("""{ "Retention": { "ExecutionTimeOfDay": "25:99" } }""");

        var result = YaguraConfigurationLoader.Load(_dataRoot, new FakeLogger());

        Assert.Equal(RetentionSchedulerOptions.DefaultExecutionTimeOfDay, result.Configuration.RetentionExecutionTimeOfDay);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Retention:ExecutionTimeOfDay", warning.Key);
    }

    // ------------------------------------------------------------------
    // スケジューラ: 次回実行までの待機計算(基準時刻 1 回から両端を構築)
    // ------------------------------------------------------------------

    [Fact]
    public void ComputeDelay_BeforeAndAfterExecutionTime_YieldsSameDayOrNextDay()
    {
        // 基準時刻を 1 回だけ構築し、実行時刻の前後を派生させる(タイムゾーンは本メソッドが
        // ToLocalTime() を使うため、期待値も同じ変換で導出して環境非依存にする)。
        var baseline = new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);
        var baselineLocal = baseline.ToLocalTime();
        var executionTime = new TimeOnly(baselineLocal.Hour, baselineLocal.Minute).AddMinutes(10);

        var delayBefore = RetentionScheduler.ComputeDelayUntilNextExecution(baseline, executionTime);
        var delayAfter = RetentionScheduler.ComputeDelayUntilNextExecution(baseline.AddMinutes(20), executionTime);

        // 実行時刻の 10 分前 → 当日実行(10 分後)。実行時刻の 10 分後 → 翌日実行(約 23 時間 50 分後)。
        Assert.Equal(TimeSpan.FromMinutes(10), delayBefore);
        Assert.Equal(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(10), delayAfter);
    }

    // ------------------------------------------------------------------
    // スケジューラ: 容量枯渇契機の前倒し削除(database.md §4 の自走復旧)
    // ------------------------------------------------------------------

    [Fact]
    public async Task OnCapacityExhausted_WithRetentionConfigured_TriggersImmediateDelete()
    {
        var store = new RecordingLogStore();
        await using var scheduler = new RetentionScheduler(
            store,
            new RetentionSchedulerOptions(RetentionDays: 7, RetentionSchedulerOptions.DefaultExecutionTimeOfDay));

        scheduler.OnCapacityExhausted();

        // fire-and-forget のため条件ポーリングで完了を待つ(固定 sleep は使わない)。
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (store.DeleteCallCount == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(1, store.DeleteCallCount);
        Assert.NotNull(store.LastCutoff);
    }

    [Fact]
    public async Task OnCapacityExhausted_WithoutRetentionDays_WarnsInsteadOfDeleting()
    {
        var store = new RecordingLogStore();
        var collector = new FakeLogCollector();
        await using var scheduler = new RetentionScheduler(
            store,
            new RetentionSchedulerOptions(RetentionDays: null, RetentionSchedulerOptions.DefaultExecutionTimeOfDay),
            logger: new FakeLogger<RetentionScheduler>(collector));

        scheduler.OnCapacityExhausted();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!collector.GetSnapshot().Any(r => r.Message.Contains("[retention-not-configured]")) &&
               DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(0, store.DeleteCallCount);
        Assert.Contains(collector.GetSnapshot(), r => r.Message.Contains("[retention-not-configured]"));
    }

    // ------------------------------------------------------------------
    // スケジューラ: 起動時キャッチアップ(Issue #150)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Start_WithoutPriorDeleteRecord_ExecutesCatchUpImmediately()
    {
        var store = new RecordingLogStore();
        await using var scheduler = new RetentionScheduler(
            store,
            new RetentionSchedulerOptions(RetentionDays: 7, RetentionSchedulerOptions.DefaultExecutionTimeOfDay));

        scheduler.Start();

        await WaitUntilAsync(() => store.DeleteCallCount >= 1);

        Assert.Equal(1, store.DeleteCallCount);
        // キャッチアップ実行も通常の実行と同じくシステムイベントとして記録される。
        Assert.Contains(store.WrittenSystemEvents, e => e.Kind == RetentionConstants.SystemEventKindRetentionDelete);
    }

    [Fact]
    public async Task Start_WithRecentDeleteRecord_SkipsCatchUp()
    {
        var baseline = new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(baseline);
        var store = new RecordingLogStore();
        // 前回実行が閾値(1 日)未満: 2 時間前。
        store.SystemEventsToReturn.Add(new SystemEvent(
            RetentionConstants.SystemEventKindRetentionDelete,
            StartAt: baseline.AddDays(-7).AddHours(-2),
            EndAt: baseline.AddHours(-2),
            Approximate: false));

        var collector = new FakeLogCollector();
        await using var scheduler = new RetentionScheduler(
            store,
            new RetentionSchedulerOptions(RetentionDays: 7, RetentionSchedulerOptions.DefaultExecutionTimeOfDay),
            timeProvider,
            new FakeLogger<RetentionScheduler>(collector));

        scheduler.Start();

        // 「スキップした」ことはログで確定を待つ(何も起きないことの検証を固定 sleep にしない)。
        await WaitUntilAsync(() => collector.GetSnapshot().Any(r => r.Message.Contains("[retention-catchup-skip]")));

        Assert.Equal(0, store.DeleteCallCount);
    }

    [Fact]
    public async Task Start_ManyDowntimeEventsAccumulated_StillFindsBackdatedDeleteRecordAndSkips()
    {
        // PR #198 レビュー指摘の押し出し問題の回帰テスト: retention.delete の StartAt は
        // 意図的な過去日付(cutoff)である一方、受信断イベントの StartAt は実時刻のため、
        // 種別を問わない直近 N 件の走査では受信断が CatchUpEventQueryLimit 件を超えて
        // 蓄積すると削除記録が恒常的に押し出され「記録なし」と誤認していた。
        // Kind フィルタ(ILogStore.QuerySystemEventsAsync の契約拡張)によりこの状況でも
        // 削除記録を発見し、キャッチアップをスキップできることを検証する。
        var baseline = new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(baseline);
        var store = new RecordingLogStore();

        // 受信断イベントを上限超まで蓄積(StartAt は実時刻——新しい側に並ぶ)。
        for (var i = 0; i < RetentionScheduler.CatchUpEventQueryLimit + 50; i++)
        {
            store.SystemEventsToReturn.Add(new SystemEvent(
                "downtime.normal-stop",
                StartAt: baseline.AddMinutes(-i - 1),
                EndAt: baseline.AddMinutes(-i),
                Approximate: false));
        }

        // 直近の削除実行: 2 時間前に実行、StartAt は cutoff(7 日前)へバックデート済み。
        store.SystemEventsToReturn.Add(new SystemEvent(
            RetentionConstants.SystemEventKindRetentionDelete,
            StartAt: baseline.AddDays(-7).AddHours(-2),
            EndAt: baseline.AddHours(-2),
            Approximate: false));

        var collector = new FakeLogCollector();
        await using var scheduler = new RetentionScheduler(
            store,
            new RetentionSchedulerOptions(RetentionDays: 7, RetentionSchedulerOptions.DefaultExecutionTimeOfDay),
            timeProvider,
            new FakeLogger<RetentionScheduler>(collector));

        scheduler.Start();

        await WaitUntilAsync(() => collector.GetSnapshot().Any(r => r.Message.Contains("[retention-catchup-skip]")));

        Assert.Equal(0, store.DeleteCallCount);
    }

    [Theory]
    [InlineData(0, true)]   // ちょうど閾値(1 日)経過 → 実行する(境界は実行側)
    [InlineData(-1, false)] // 閾値まで 1 分残っている → 実行しない
    [InlineData(1, true)]   // 閾値を 1 分超過 → 実行する
    public async Task Start_CatchUpThresholdBoundary_FiresOnlyAtOrBeyondThreshold(int minutesBeyondThreshold, bool expectCatchUp)
    {
        var baseline = new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(baseline);
        var store = new RecordingLogStore();
        var lastDeleteAt = baseline - RetentionScheduler.CatchUpThreshold - TimeSpan.FromMinutes(minutesBeyondThreshold);
        store.SystemEventsToReturn.Add(new SystemEvent(
            RetentionConstants.SystemEventKindRetentionDelete,
            StartAt: lastDeleteAt.AddDays(-7),
            EndAt: lastDeleteAt,
            Approximate: false));

        var collector = new FakeLogCollector();
        await using var scheduler = new RetentionScheduler(
            store,
            new RetentionSchedulerOptions(RetentionDays: 7, RetentionSchedulerOptions.DefaultExecutionTimeOfDay),
            timeProvider,
            new FakeLogger<RetentionScheduler>(collector));

        scheduler.Start();

        if (expectCatchUp)
        {
            await WaitUntilAsync(() => store.DeleteCallCount >= 1);
            Assert.Equal(1, store.DeleteCallCount);
        }
        else
        {
            await WaitUntilAsync(() => collector.GetSnapshot().Any(r => r.Message.Contains("[retention-catchup-skip]")));
            Assert.Equal(0, store.DeleteCallCount);
        }
    }

    [Fact]
    public async Task Start_QuerySystemEventsFails_SkipsCatchUpAndKeepsSchedulerAlive()
    {
        var store = new RecordingLogStore { ThrowOnQuerySystemEvents = true };
        var collector = new FakeLogCollector();
        await using var scheduler = new RetentionScheduler(
            store,
            new RetentionSchedulerOptions(RetentionDays: 7, RetentionSchedulerOptions.DefaultExecutionTimeOfDay),
            logger: new FakeLogger<RetentionScheduler>(collector));

        scheduler.Start();

        await WaitUntilAsync(() => collector.GetSnapshot().Any(r => r.Message.Contains("[retention-catchup-query-failed]")));

        // 判定の失敗はキャッチアップの見送りに留まり、削除は実行されない。
        Assert.Equal(0, store.DeleteCallCount);

        // スケジューラ自体は生きている(容量枯渇契機は引き続き機能する)。
        scheduler.OnCapacityExhausted();
        await WaitUntilAsync(() => store.DeleteCallCount >= 1);
        Assert.Equal(1, store.DeleteCallCount);
    }

    [Fact]
    public async Task Start_WithoutRetentionDays_DoesNotQueryOrCatchUp()
    {
        // RetentionDays 未設定(削除しない)ならキャッチアップ判定の読み取り自体を行わない。
        var store = new RecordingLogStore { ThrowOnQuerySystemEvents = true };
        var collector = new FakeLogCollector();
        await using var scheduler = new RetentionScheduler(
            store,
            new RetentionSchedulerOptions(RetentionDays: null, RetentionSchedulerOptions.DefaultExecutionTimeOfDay),
            logger: new FakeLogger<RetentionScheduler>(collector));

        scheduler.Start();

        // 読み取りが呼ばれていれば ThrowOnQuerySystemEvents により
        // [retention-catchup-query-failed] が出る——出ないことを短い猶予の後に確認する。
        await Task.Delay(200);
        Assert.DoesNotContain(collector.GetSnapshot(), r => r.Message.Contains("[retention-catchup-query-failed]"));
        Assert.Equal(0, store.DeleteCallCount);
    }

    [Fact]
    public async Task Start_CatchUpThenScheduledTickSoonAfter_SkipsSecondExecution()
    {
        // キャッチアップ実行の直後に当日の定刻が到来しても、二重実行しないこと(Issue #150)。
        // 定刻を「現在時刻の 1 時間後」に設定し、キャッチアップ実行後に手動タイマーで
        // 1 時間進めて定刻の契機を発火させる。
        var baseline = new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(baseline);
        var baselineLocal = baseline.ToLocalTime();
        var executionTime = new TimeOnly(baselineLocal.Hour, baselineLocal.Minute).AddHours(1);

        var store = new RecordingLogStore(); // 実行記録なし → キャッチアップが発火する。
        var collector = new FakeLogCollector();
        await using var scheduler = new RetentionScheduler(
            store,
            new RetentionSchedulerOptions(RetentionDays: 7, executionTime),
            timeProvider,
            new FakeLogger<RetentionScheduler>(collector));

        scheduler.Start();

        // キャッチアップの実行と、定期実行ループのタイマー登録を待つ。
        await WaitUntilAsync(() => store.DeleteCallCount >= 1 && timeProvider.TimersCreated >= 1);

        // 定刻を超えて時刻を進める → 定期実行の契機が発火するが、直前のキャッチアップ実行から
        // MinimumReexecutionInterval 未満のためスキップされる。進める量は実装と同じ計算で
        // 導出する(ローカル時刻の日またぎを含め約 1 時間になる——タイムゾーン非依存)。
        var scheduledDelay = RetentionScheduler.ComputeDelayUntilNextExecution(baseline, executionTime);
        Assert.True(
            scheduledDelay + TimeSpan.FromMinutes(2) < RetentionScheduler.MinimumReexecutionInterval,
            $"定刻までの待機 {scheduledDelay} が二重実行抑止の窓を超えており、テストの前提が崩れている。");
        timeProvider.Advance(scheduledDelay + TimeSpan.FromMinutes(2));
        await WaitUntilAsync(() => collector.GetSnapshot().Any(r => r.Message.Contains("[retention-delete-recent-skip]")));

        Assert.Equal(1, store.DeleteCallCount);
    }

    // ------------------------------------------------------------------
    // スケジューラ: 書き込みゲートとの直列化(Issue #151)
    // ------------------------------------------------------------------

    [Fact]
    public async Task OnCapacityExhausted_WithWriteGateHeld_WaitsUntilGateReleased()
    {
        using var gate = new LogStoreWriteGate();
        var store = new RecordingLogStore();
        await using var scheduler = new RetentionScheduler(
            store,
            new RetentionSchedulerOptions(RetentionDays: 7, RetentionSchedulerOptions.DefaultExecutionTimeOfDay),
            writeGate: gate);

        // 他経路(ライブ書き込みの体)がゲートを保持している間、削除は開始されない。
        var lease = await gate.AcquireAsync(CancellationToken.None);
        scheduler.OnCapacityExhausted();

        await Task.Delay(200);
        Assert.Equal(0, store.DeleteCallCount);

        // 解放されると削除が進む。
        lease.Dispose();
        await WaitUntilAsync(() => store.DeleteCallCount >= 1);
        Assert.Equal(1, store.DeleteCallCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "条件が制限時間内に成立しなかった。");
    }

    /// <summary>
    /// DeleteOlderThanAsync / WriteSystemEventAsync / QuerySystemEventsAsync の呼び出しを
    /// 記録する最小フェイク（起動時キャッチアップ——Issue #150——の判定入力も供給する）。
    /// </summary>
    private sealed class RecordingLogStore : ILogStore
    {
        private int _deleteCallCount;

        public int DeleteCallCount => Volatile.Read(ref _deleteCallCount);
        public DateTimeOffset? LastCutoff { get; private set; }

        /// <summary>QuerySystemEventsAsync が返すイベント（キャッチアップ判定の入力）。</summary>
        public List<SystemEvent> SystemEventsToReturn { get; } = [];

        /// <summary>QuerySystemEventsAsync を失敗させる（キャッチアップ判定の失敗経路の検証用）。</summary>
        public bool ThrowOnQuerySystemEvents { get; set; }

        /// <summary>WriteSystemEventAsync で書き込まれたイベントの記録。</summary>
        public List<SystemEvent> WrittenSystemEvents { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<LogRecordSummary>> QueryLatestAsync(int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LogRecordSummary>>(Array.Empty<LogRecordSummary>());

        public Task<IReadOnlyList<LogRecordSummary>> QueryAsync(LogQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LogRecordSummary>>(Array.Empty<LogRecordSummary>());

        public Task WriteSystemEventAsync(SystemEvent systemEvent, CancellationToken cancellationToken = default)
        {
            lock (WrittenSystemEvents)
            {
                WrittenSystemEvents.Add(systemEvent);
            }

            return Task.CompletedTask;
        }

        public Task<DeleteOlderThanResult> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        {
            LastCutoff = cutoff;
            Interlocked.Increment(ref _deleteCallCount);
            return Task.FromResult(new DeleteOlderThanResult(DeletedCount: 0, Cutoff: cutoff));
        }

        public Task<LogStoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LogStoreStatistics(RecordCount: 0, DatabaseSizeBytes: null, WalSizeBytes: null));

        // M8-3 で追加された読み取り専用 3 操作のうち、QuerySystemEventsAsync は起動時
        // キャッチアップ（Issue #150）の判定入力として本テストの検証対象になった。
        // 残る 2 操作は引き続き検証対象外のため未対応で明示する。
        public Task<LogRecord?> FindByIdAsync(long id, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("このテストダブルは閲覧用の読み取り操作を対象としない。");

        public Task<IReadOnlyList<SystemEvent>> QuerySystemEventsAsync(DateTimeOffset? from, DateTimeOffset? to, int limit, TimeSpan timeout, string? kind = null, CancellationToken cancellationToken = default)
        {
            if (ThrowOnQuerySystemEvents)
            {
                throw new InvalidOperationException("テスト用の読み取り失敗。");
            }

            // 実 provider と同じ意味論で kind フィルタを適用する（Issue #150 の
            // キャッチアップ判定が種別で絞って検索していることを、このフェイク越しにも
            // 通過させるため——受信断イベントを混ぜたテストで押し出しが起きないことの検証に使う）。
            IReadOnlyList<SystemEvent> events = SystemEventsToReturn
                .Where(e => kind is null || e.Kind == kind)
                .OrderByDescending(e => e.StartAt)
                .Take(limit)
                .ToList();
            return Task.FromResult(events);
        }

        public Task<IReadOnlyList<SourceActivity>> QuerySourceActivityAsync(int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("このテストダブルは閲覧用の読み取り操作を対象としない。");

        public Task<IReadOnlyList<SeverityCount>> QuerySeverityDistributionAsync(DateTimeOffset from, DateTimeOffset to, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("このテストダブルは閲覧用の読み取り操作を対象としない。");

        public Task<IReadOnlyList<SourceActivity>> QueryTopTalkersAsync(DateTimeOffset from, DateTimeOffset to, int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("このテストダブルは閲覧用の読み取り操作を対象としない。");
    }

    /// <summary>
    /// テストから時刻を明示的に進められる <see cref="TimeProvider"/> 実装。
    /// <see cref="Advance"/> で期限を過ぎたタイマーを発火させる（Task.Delay(delay, timeProvider)
    /// の待機を実時間なしで決定的に進めるため——キャッチアップと定刻の近接をテストで再現する）。
    /// </summary>
    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _lock = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = utcNow;
        private int _timersCreated;

        public int TimersCreated => Volatile.Read(ref _timersCreated);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock)
            {
                return _utcNow;
            }
        }

        public void Advance(TimeSpan delta)
        {
            List<ManualTimer> due;
            lock (_lock)
            {
                _utcNow += delta;
                due = _timers.Where(t => !t.Fired && t.DueAt <= _utcNow).ToList();
                foreach (var timer in due)
                {
                    timer.Fired = true;
                }
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _timersCreated);
            var timer = new ManualTimer(this, callback, state);
            lock (_lock)
            {
                timer.DueAt = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : _utcNow + dueTime;
                _timers.Add(timer);
            }

            return timer;
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset DueAt { get; set; } = DateTimeOffset.MaxValue;
            public bool Fired { get; set; }

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._lock)
                {
                    DueAt = dueTime == Timeout.InfiniteTimeSpan
                        ? DateTimeOffset.MaxValue
                        : owner._utcNow + dueTime;
                    Fired = false;
                }

                return true;
            }

            public void Dispose()
            {
                lock (owner._lock)
                {
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
