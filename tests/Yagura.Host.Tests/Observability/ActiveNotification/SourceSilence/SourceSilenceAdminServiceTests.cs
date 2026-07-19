using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Abstractions.Observability;
using Yagura.Host.Configuration;
using Yagura.Host.Observability.ActiveNotification.SourceSilence;
using Yagura.Storage;

namespace Yagura.Host.Tests.Observability.ActiveNotification.SourceSilence;

/// <summary>
/// <see cref="SourceSilenceAdminService"/>（ADR-0018 決定 4・5・6。Issue #351 第 5 段）の単体テスト。
/// </summary>
public sealed class SourceSilenceAdminServiceTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-source-silence-admin-{Guid.NewGuid():N}");
    private readonly RecordingAuditRecorder _audit = new();
    private readonly FakeLogStore _logStore = new();

    private IReadOnlyList<SourceSilenceWatchEntry>? _lastApplied;
    private int _applyCount;

    public SourceSilenceAdminServiceTests() => Directory.CreateDirectory(_dataRoot);

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    private SourceSilenceAdminService CreateService(
        Func<IReadOnlyList<YaguraSourceSilenceReading>>? runtimeStates = null) =>
        new(_dataRoot,
            _audit,
            _logStore,
            applyToRuntime: watchlist =>
            {
                _lastApplied = watchlist;
                _applyCount++;
            },
            runtimeStates: runtimeStates ?? (() => []),
            timeProvider: null);

    private static SourceSilenceWatchlistItem Item(
        string address, string? label = null, int? thresholdMinutes = 60) =>
        new(address, label, thresholdMinutes);

    // ------------------------------------------------------------------
    // 保存・監査 2023・即時反映（決定 5・6）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Configure_AddedEntries_SavesNormalizedValuesAuditsAndAppliesImmediately()
    {
        var service = CreateService();

        var result = await service.ConfigureAsync(new SourceSilenceSettings(
        [
            Item("192.0.2.10", "コアスイッチ", 60),
            Item("::ffff:192.0.2.11", "FW", 120), // IPv4-mapped は IPv4 表記へ正規化して保存する
        ]));

        Assert.Equal(2, result.AddedAddresses.Count);
        Assert.Contains("192.0.2.10(コアスイッチ)", result.AddedAddresses);
        Assert.Contains("192.0.2.11(FW)", result.AddedAddresses);

        var persisted = YaguraConfigurationWriter.Read(_dataRoot).Options.Notification?.SourceSilence?.Watchlist;
        Assert.NotNull(persisted);
        Assert.Equal(["192.0.2.10", "192.0.2.11"], persisted!.Select(e => e.Address));

        // 監査 2023: 追加エントリのアドレスと表示名が Detail に残る（決定 5）。
        var audit = Assert.Single(_audit.Events);
        Assert.Equal(AuditEventKind.SourceSilenceWatchlistConfigured, audit.Kind);
        Assert.Contains("192.0.2.10(コアスイッチ)", audit.Detail, StringComparison.Ordinal);

        // 即時反映（決定 6）: Loader で解決した実効ウォッチリストが渡る。
        Assert.Equal(1, _applyCount);
        Assert.NotNull(_lastApplied);
        Assert.Equal(2, _lastApplied!.Count);
        Assert.Equal(TimeSpan.FromMinutes(120), _lastApplied[1].Threshold);
    }

    [Fact]
    public async Task Configure_RemovedEntry_AppearsInTheAuditTrail()
    {
        var service = CreateService();
        await service.ConfigureAsync(new SourceSilenceSettings([Item("192.0.2.10", "撤去予定"), Item("192.0.2.11")]));
        _audit.Events.Clear();

        var result = await service.ConfigureAsync(new SourceSilenceSettings([Item("192.0.2.11")]));

        Assert.Equal(["192.0.2.10(撤去予定)"], result.RemovedAddresses);
        var audit = Assert.Single(_audit.Events);
        Assert.Contains("removed=[192.0.2.10(撤去予定)]", audit.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Configure_LabelOrThresholdChange_IsRecordedAsChanged()
    {
        var service = CreateService();
        await service.ConfigureAsync(new SourceSilenceSettings([Item("192.0.2.10", "旧名", 60)]));
        _audit.Events.Clear();

        var result = await service.ConfigureAsync(new SourceSilenceSettings([Item("192.0.2.10", "新名", 90)]));

        Assert.Equal(["192.0.2.10(新名)"], result.ChangedAddresses);
        Assert.Empty(result.AddedAddresses);
        Assert.Empty(result.RemovedAddresses);
        Assert.Single(_audit.Events);
    }

    [Fact]
    public async Task Configure_NoChanges_DoesNotSaveAuditOrReapply()
    {
        var service = CreateService();
        await service.ConfigureAsync(new SourceSilenceSettings([Item("192.0.2.10", "装置", 60)]));
        _audit.Events.Clear();
        var applyCountAfterFirst = _applyCount;

        var result = await service.ConfigureAsync(new SourceSilenceSettings([Item("192.0.2.10", "装置", 60)]));

        Assert.Empty(result.AddedAddresses);
        Assert.Empty(result.RemovedAddresses);
        Assert.Empty(result.ChangedAddresses);
        Assert.Empty(_audit.Events);
        Assert.Equal(applyCountAfterFirst, _applyCount);
    }

    // ------------------------------------------------------------------
    // 保存前検証（起動時のエントリ単位無効化を、利用者が目の前にいる場面では拒否に倒す）
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("192.0.2.999")]
    [InlineData("")]
    public async Task Configure_InvalidAddress_IsRejected(string address)
    {
        var service = CreateService();

        await Assert.ThrowsAsync<WizardValidationException>(() =>
            service.ConfigureAsync(new SourceSilenceSettings([Item(address)])));

        Assert.Empty(_audit.Events);
    }

    [Fact]
    public async Task Configure_DuplicateAfterNormalization_IsRejected()
    {
        // IPv4 と IPv4-mapped IPv6 は同一アドレス——表記違いの二重登録を許すと、
        // 片方だけ消した「つもり」の編集が成立してしまう。
        var service = CreateService();

        await Assert.ThrowsAsync<WizardValidationException>(() =>
            service.ConfigureAsync(new SourceSilenceSettings(
                [Item("192.0.2.10"), Item("::ffff:192.0.2.10")])));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(43201)]
    public async Task Configure_ThresholdOutOfRange_IsRejected(int thresholdMinutes)
    {
        var service = CreateService();

        await Assert.ThrowsAsync<WizardValidationException>(() =>
            service.ConfigureAsync(new SourceSilenceSettings(
                [Item("192.0.2.10", thresholdMinutes: thresholdMinutes)])));
    }

    [Fact]
    public async Task Configure_MoreThanTheEntryLimit_IsRejected()
    {
        var service = CreateService();
        var tooMany = Enumerable.Range(0, 1001)
            .Select(i => Item($"10.{i / 65536 % 256}.{i / 256 % 256}.{i % 256}"))
            .ToList();

        await Assert.ThrowsAsync<WizardValidationException>(() =>
            service.ConfigureAsync(new SourceSilenceSettings(tooMany)));
    }

    [Fact]
    public async Task Configure_NewEntryWithoutThreshold_IsRejected_ButExistingOmissionIsPreserved()
    {
        // 決定 4: UI 経由の登録は閾値の明示確定を必須とする。ただし手編集で閾値を省略した
        // 既存エントリは省略のまま保持できる（無関係な編集のたびに省略の解消を強要しない）。
        SeedRawWatchlist(RawEntry("192.0.2.10", label: "手編集"));
        var service = CreateService();

        // 新規の閾値未指定は拒否。
        await Assert.ThrowsAsync<WizardValidationException>(() =>
            service.ConfigureAsync(new SourceSilenceSettings(
                [Item("192.0.2.10", "手編集", null), Item("192.0.2.20", thresholdMinutes: null)])));

        // 既存の省略保持 + 新規の明示指定は受理。
        var result = await service.ConfigureAsync(new SourceSilenceSettings(
            [Item("192.0.2.10", "手編集", null), Item("192.0.2.20", thresholdMinutes: 60)]));

        Assert.Equal(["192.0.2.20"], result.AddedAddresses);
        var persisted = YaguraConfigurationWriter.Read(_dataRoot).Options.Notification?.SourceSilence?.Watchlist;
        Assert.Null(persisted![0].ThresholdMinutes); // 省略が保持されている
    }

    // ------------------------------------------------------------------
    // 候補選択（決定 4）と状態取得
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetCandidates_SortsNewestFirstAndMarksRegisteredOnes()
    {
        var service = CreateService();
        await service.ConfigureAsync(new SourceSilenceSettings([Item("192.0.2.10")]));

        _logStore.SourceActivities =
        [
            new SourceActivity("192.0.2.99", DateTimeOffset.Parse("2026-07-19T00:00:00Z"), 5),
            new SourceActivity("::ffff:192.0.2.10", DateTimeOffset.Parse("2026-07-19T03:00:00Z"), 100),
        ];

        var candidates = await service.GetCandidatesAsync(limit: 10);

        Assert.Equal(2, candidates.Count);
        Assert.Equal("::ffff:192.0.2.10", candidates[0].Address); // 新しい順
        Assert.True(candidates[0].AlreadyRegistered);             // 正規化して照合される
        Assert.False(candidates[1].AlreadyRegistered);
    }

    [Fact]
    public async Task GetStatus_ReturnsRawWatchlistLimitsAndRuntimeStates()
    {
        SeedRawWatchlist(RawEntry("192.0.2.10", label: "装置", thresholdMinutes: "60"));
        var service = CreateService(runtimeStates: () =>
            [new YaguraSourceSilenceReading("192.0.2.10", "装置", TimeSpan.FromMinutes(60), IsSilent: true)]);

        var status = await service.GetStatusAsync();

        Assert.Equal(1000, status.MaxWatchlistEntries);
        Assert.Equal(10, status.MinThresholdMinutes);
        Assert.Equal(43200, status.MaxThresholdMinutes);
        Assert.Equal(1440, status.DefaultThresholdMinutes);
        var item = Assert.Single(status.Watchlist);
        Assert.Equal("192.0.2.10", item.Address);
        Assert.Equal(60, item.ThresholdMinutes);
        Assert.True(Assert.Single(status.RuntimeStates).IsSilent);
    }

    // ------------------------------------------------------------------
    // ヘルパー
    // ------------------------------------------------------------------

    private static YaguraConfigurationOptions.NotificationOptions.SourceSilenceOptions.WatchlistEntryOptions RawEntry(
        string address, string? label = null, string? thresholdMinutes = null) =>
        new() { Address = address, Label = label, ThresholdMinutes = thresholdMinutes };

    private void SeedRawWatchlist(
        params YaguraConfigurationOptions.NotificationOptions.SourceSilenceOptions.WatchlistEntryOptions[] entries)
    {
        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        var options = snapshot.Options;
        options.Notification ??= new YaguraConfigurationOptions.NotificationOptions();
        options.Notification.SourceSilence = new YaguraConfigurationOptions.NotificationOptions.SourceSilenceOptions
        {
            Watchlist = [.. entries],
        };
        YaguraConfigurationWriter.Save(_dataRoot, options, snapshot.VersionToken);
    }

    private sealed class RecordingAuditRecorder : IAuditRecorder
    {
        internal List<AuditEvent> Events { get; } = [];

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    /// <summary>候補選択（QuerySourceActivityAsync）だけを差し替える最小の ILogStore。</summary>
    private sealed class FakeLogStore : ILogStore
    {
        internal IReadOnlyList<SourceActivity> SourceActivities { get; set; } = [];

        public Task<IReadOnlyList<SourceActivity>> QuerySourceActivityAsync(
            int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SourceActivity>>([.. SourceActivities.Take(limit)]);

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

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
    }
}
