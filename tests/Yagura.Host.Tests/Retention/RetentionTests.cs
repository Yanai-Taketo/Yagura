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

    /// <summary>DeleteOlderThanAsync の呼び出しを記録する最小フェイク。</summary>
    private sealed class RecordingLogStore : ILogStore
    {
        private int _deleteCallCount;

        public int DeleteCallCount => Volatile.Read(ref _deleteCallCount);
        public DateTimeOffset? LastCutoff { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<LogRecordSummary>> QueryLatestAsync(int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LogRecordSummary>>(Array.Empty<LogRecordSummary>());

        public Task<IReadOnlyList<LogRecordSummary>> QueryAsync(LogQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LogRecordSummary>>(Array.Empty<LogRecordSummary>());

        public Task WriteSystemEventAsync(SystemEvent systemEvent, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<DeleteOlderThanResult> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        {
            LastCutoff = cutoff;
            Interlocked.Increment(ref _deleteCallCount);
            return Task.FromResult(new DeleteOlderThanResult(DeletedCount: 0, Cutoff: cutoff));
        }

        public Task<LogStoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LogStoreStatistics(RecordCount: 0, DatabaseSizeBytes: null, WalSizeBytes: null));

        // M8-3 で追加された読み取り専用 3 操作（閲覧画面用の読み取り口）。本テストダブルの
        // 検証対象では使用しないため未対応で明示する。
        public Task<LogRecord?> FindByIdAsync(long id, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("このテストダブルは閲覧用の読み取り操作を対象としない。");

        public Task<IReadOnlyList<SystemEvent>> QuerySystemEventsAsync(DateTimeOffset? from, DateTimeOffset? to, int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("このテストダブルは閲覧用の読み取り操作を対象としない。");

        public Task<IReadOnlyList<SourceActivity>> QuerySourceActivityAsync(int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("このテストダブルは閲覧用の読み取り操作を対象としない。");
    }
}
