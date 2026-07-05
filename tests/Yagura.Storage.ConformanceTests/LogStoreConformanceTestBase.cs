using Yagura.Storage;

namespace Yagura.Storage.ConformanceTests;

/// <summary>
/// すべての <see cref="ILogStore"/> provider が合格すべき単一の適合テストスイート
/// （database.md §1.3「適合テストスイート — 外部貢献の入口」）。
/// </summary>
/// <remarks>
/// <para>
/// <b>新 provider を追加する手順</b>（外部貢献者向け）:
/// </para>
/// <para>
/// 1. 本クラスを継承した派生クラスを作成し、<see cref="CreateStoreAsync"/> と
///    <see cref="DisposeStoreAsync"/> を実装する。<see cref="CreateStoreAsync"/> は
///    「毎テストで独立した新規インスタンス」を返すこと（テスト間でデータを共有しない）。
///    <see cref="ILogStore.InitializeAsync"/> の呼び出しは <see cref="CreateStoreAsync"/> の
///    実装側の責務とする（本基底クラスは呼び出し済みの <see cref="ILogStore"/> を受け取る）。
/// </para>
/// <para>
/// 2. 派生クラスをそのままビルド・実行し、本基底クラスに定義された全テストが緑になることを
///    確認する（xUnit の継承テストパターン——派生すると基底の [Fact]/[Theory] がすべて
///    派生クラスに対して実行される）。
/// </para>
/// <para>
/// 3. 全緑になったら、database.md に provider 節（§4・§5 と同格）を追加する PR を提出する
///    （database.md §1.3「新 provider の追加提案は『適合テストスイートが通ること』を受け入れの
///    技術条件とする」）。provider 節には「読み書き分離の性質」（本スイートでは機械検証しない
///    文書化義務。下記参照）を明記すること。
/// </para>
/// <para>
/// <b>本スイートが機械検証する範囲</b>（database.md §1.2 契約 6 項目に対応）:
/// スキーマ管理の冪等性・バッチ挿入（全カラム往復・空バッチ・大バッチ・at-least-once の
/// 重複許容）・対話的検索（条件・組み合わせ・射影・上限・キャンセル伝播）・保持期間削除
/// （cutoff 境界・分割実行・削除件数・空 DB）・統計（件数・サイズの有無）・システムイベントの
/// 往復。「失敗の分類報告」（契約 3）は発火手段が provider 依存のため、本基底クラスでは
/// <see cref="LogStoreFailureKind"/> が 3 値のいずれかであることの型レベル検証のみを行い、
/// 実際の発火・分類の正しさは provider 固有のテスト（例: SqliteCapacityTests）に委ねる
/// （database.md §1.2「発火手段が provider 依存」を理由に本スイートではスキップし、
/// provider 固有テストでの検証を必須とする）。
/// </para>
/// <para>
/// <b>本スイートが機械検証しない範囲</b>: 「読み書き分離の性質」（database.md §1.2 契約表 末尾）は
/// 文書化義務であり、レビューで検証する（§1.3）。provider ごとの運用特性（WAL 肥大等）も
/// 同様に provider 固有のテスト・doc コメントに委ねる。
/// </para>
/// </remarks>
public abstract class LogStoreConformanceTestBase : IAsyncLifetime
{
    private ILogStore _store = null!;

    /// <summary>
    /// テストが使う <see cref="ILogStore"/> を返す。基準時刻は
    /// <see cref="DateTimeOffset.UtcNow"/> を 1 回だけ読み取ってから両端を構築すること
    /// （conventions.md「時間窓を扱うテストは 1 つの基準時刻から両端を構築する」）。
    /// </summary>
    protected ILogStore Store => _store;

    /// <summary>
    /// 新規の <see cref="ILogStore"/> インスタンスを生成し、<see cref="ILogStore.InitializeAsync"/>
    /// まで完了させて返す。呼び出しごとに独立したストレージ（別ファイル・別スキーマ等）を
    /// 使うこと——テスト間でデータが漏れてはならない。
    /// </summary>
    protected abstract Task<ILogStore> CreateStoreAsync();

    /// <summary>
    /// <see cref="CreateStoreAsync"/> が生成したストアの後片付け（ファイル削除等）を行う。
    /// </summary>
    protected abstract Task DisposeStoreAsync(ILogStore store);

    public async Task InitializeAsync()
    {
        _store = await CreateStoreAsync();
    }

    public async Task DisposeAsync()
    {
        await DisposeStoreAsync(_store);
    }

    private static LogRecord CreateParsedRecord(DateTimeOffset receivedAt, string sourceAddress, string message, int? severity = null) =>
        new(
            ReceivedAt: receivedAt,
            SourceAddress: sourceAddress,
            SourcePort: 514,
            Protocol: Protocol.Udp,
            ParseStatus: ParseStatus.Parsed,
            Facility: 1,
            Severity: severity ?? 5,
            Hostname: "host",
            AppName: "app",
            ProcId: "123",
            MsgId: "msg-1",
            StructuredData: "[exampleSDID@32473 iut=\"3\"]",
            Message: message);

    // ------------------------------------------------------------------
    // 契約 1: スキーマ管理（database.md §1.2「スキーマ管理」）
    // ------------------------------------------------------------------

    [Fact]
    public async Task InitializeAsync_CalledTwice_IsIdempotent()
    {
        var exception = await Record.ExceptionAsync(() => Store.InitializeAsync());

        Assert.Null(exception);
    }

    [Fact]
    public async Task InitializeAsync_CalledTwiceConcurrently_DoesNotThrow()
    {
        var first = Store.InitializeAsync();
        var second = Store.InitializeAsync();

        var exception = await Record.ExceptionAsync(() => Task.WhenAll(first, second));

        Assert.Null(exception);
    }

    [Fact]
    public async Task InitializeAsync_CalledAfterDataWritten_DoesNotDestroyExistingData()
    {
        var baseline = DateTimeOffset.UtcNow;
        await Store.WriteBatchAsync(new[] { CreateParsedRecord(baseline, "10.0.0.1", "pre-existing") });

        await Store.InitializeAsync();

        var results = await Store.QueryLatestAsync(limit: 10, timeout: TimeSpan.FromSeconds(5));
        Assert.Single(results);
        Assert.Equal("pre-existing", results[0].Message);
    }

    // ------------------------------------------------------------------
    // 契約 2: バッチ挿入（database.md §1.2「バッチ挿入」、architecture.md §7 要求①）
    // ------------------------------------------------------------------

    [Fact]
    public async Task WriteBatchAsync_AllColumns_RoundTripThroughRawColumn()
    {
        var receivedAt = DateTimeOffset.UtcNow;
        var raw = new byte[] { 0x00, 0xFF, 0x41, 0x42, 0xC0 };
        var record = new LogRecord(
            ReceivedAt: receivedAt,
            SourceAddress: "192.168.1.10",
            SourcePort: 514,
            Protocol: Protocol.Tcp,
            ParseStatus: ParseStatus.ParseFailed,
            DeviceTimestamp: receivedAt.AddSeconds(-1),
            Facility: 4,
            Severity: 3,
            Hostname: "host-a",
            AppName: "app-a",
            ProcId: "999",
            MsgId: "msg-x",
            StructuredData: "[sdid@1 key=\"value\"]",
            Message: "raw-backed message",
            Raw: raw);

        await Store.WriteBatchAsync(new[] { record });

        var results = await Store.QueryLatestAsync(limit: 1, timeout: TimeSpan.FromSeconds(5));

        Assert.Single(results);
        var summary = results[0];
        Assert.Equal("192.168.1.10", summary.SourceAddress);
        Assert.Equal(514, summary.SourcePort);
        Assert.Equal(Protocol.Tcp, summary.Protocol);
        Assert.Equal(ParseStatus.ParseFailed, summary.ParseStatus);
        Assert.Equal(4, summary.Facility);
        Assert.Equal(3, summary.Severity);
        Assert.Equal("host-a", summary.Hostname);
        Assert.Equal("app-a", summary.AppName);
        Assert.Equal("999", summary.ProcId);
        Assert.Equal("msg-x", summary.MsgId);
        Assert.Equal("raw-backed message", summary.Message);
    }

    [Theory]
    [InlineData(ParseStatus.Parsed)]
    [InlineData(ParseStatus.ParseFailed)]
    [InlineData(ParseStatus.Incomplete)]
    public async Task WriteBatchAsync_AllParseStatusValues_RoundTrip(ParseStatus parseStatus)
    {
        var record = new LogRecord(
            ReceivedAt: DateTimeOffset.UtcNow,
            SourceAddress: "10.0.0.1",
            SourcePort: 514,
            Protocol: Protocol.Udp,
            ParseStatus: parseStatus,
            Message: $"parse-status-{parseStatus}");

        await Store.WriteBatchAsync(new[] { record });

        var results = await Store.QueryLatestAsync(limit: 1, timeout: TimeSpan.FromSeconds(5));

        Assert.Single(results);
        Assert.Equal(parseStatus, results[0].ParseStatus);
    }

    [Fact]
    public async Task WriteBatchAsync_EmptyBatch_DoesNotThrowAndWritesNothing()
    {
        var statisticsBefore = await Store.GetStatisticsAsync();

        var exception = await Record.ExceptionAsync(() => Store.WriteBatchAsync(Array.Empty<LogRecord>()));

        Assert.Null(exception);

        var statisticsAfter = await Store.GetStatisticsAsync();
        Assert.Equal(statisticsBefore.RecordCount, statisticsAfter.RecordCount);
    }

    [Fact]
    public async Task WriteBatchAsync_LargeBatch_AllRecordsPersisted()
    {
        const int batchSize = 1000;
        var baseline = DateTimeOffset.UtcNow;
        var records = Enumerable.Range(0, batchSize)
            .Select(i => CreateParsedRecord(baseline.AddSeconds(-i), "10.0.0.1", $"large-batch-{i}"))
            .ToArray();

        await Store.WriteBatchAsync(records);

        var statistics = await Store.GetStatisticsAsync();
        Assert.Equal(batchSize, statistics.RecordCount);
    }

    [Fact]
    public async Task WriteBatchAsync_SameBatchTwice_DoesNotThrowAndDuplicatesAreStored()
    {
        // at-least-once の機械化（database.md §1.2「部分成功の扱い（全再試行で重複になっても
        // at-least-once の範囲内）」）: 同一バッチを 2 回投入してもエラーにならず、
        // 重複排除は行われない（2 倍保存される）。「重複排除は約束しない」ことの検証。
        var baseline = DateTimeOffset.UtcNow;
        var records = new[]
        {
            CreateParsedRecord(baseline.AddSeconds(-1), "10.0.0.1", "duplicate-candidate-1"),
            CreateParsedRecord(baseline, "10.0.0.2", "duplicate-candidate-2"),
        };

        await Store.WriteBatchAsync(records);
        var exception = await Record.ExceptionAsync(() => Store.WriteBatchAsync(records));

        Assert.Null(exception);

        var statistics = await Store.GetStatisticsAsync();
        Assert.Equal(4, statistics.RecordCount);
    }

    // ------------------------------------------------------------------
    // 契約 3: 失敗の分類報告（database.md §1.2「失敗の分類報告」）
    // ------------------------------------------------------------------
    //
    // 機械検証をスキップする理由: LogStoreFailureKind の実発火手段（ディスク満杯・権限不足・
    // ロック競合等）は provider ごとに前提条件が大きく異なり（例: SQLite はファイル ACL、
    // SQL Server はログイン権限）、provider 非依存の単一テストで確実に発火させる方法がない。
    // 「分類不能の例外を素通しにしない」ことの検証は、各 provider が LogStoreWriteException
    // 以外の例外を送出しないこと（またはコードレビューで確認すること）に委ね、本スイートでは
    // 型そのものが 3 値の enum であることの最小限の検証のみ行う（database.md §1.3
    // 「難しければ文書化義務としてスキップ理由を明記する」を適用）。
    // 発火可能な provider は SqliteCapacityTests のような provider 固有テストで
    // 実発火 → 分類の正しさを検証すること。

    [Fact]
    public void LogStoreFailureKind_HasExactlyThreeValues()
    {
        var values = Enum.GetValues<LogStoreFailureKind>();

        Assert.Equal(3, values.Length);
        Assert.Contains(LogStoreFailureKind.Transient, values);
        Assert.Contains(LogStoreFailureKind.Permanent, values);
        Assert.Contains(LogStoreFailureKind.CapacityExhausted, values);
    }

    // ------------------------------------------------------------------
    // 契約 4: 対話的検索（database.md §1.2「対話的検索」）
    // ------------------------------------------------------------------

    [Fact]
    public async Task QueryAsync_ReceivedAtRange_FiltersToRange()
    {
        var baseline = DateTimeOffset.UtcNow;
        await Store.WriteBatchAsync(new[]
        {
            CreateParsedRecord(baseline.AddMinutes(-10), "10.0.0.1", "too-old"),
            CreateParsedRecord(baseline.AddMinutes(-5), "10.0.0.2", "in-range"),
            CreateParsedRecord(baseline, "10.0.0.3", "too-new"),
        });

        var results = await Store.QueryAsync(new LogQuery(
            Limit: 10,
            Timeout: TimeSpan.FromSeconds(5),
            ReceivedAtFrom: baseline.AddMinutes(-6),
            ReceivedAtTo: baseline.AddMinutes(-1)));

        Assert.Single(results);
        Assert.Equal("in-range", results[0].Message);
    }

    [Fact]
    public async Task QueryAsync_SourceAddress_FiltersToExactMatch()
    {
        var baseline = DateTimeOffset.UtcNow;
        await Store.WriteBatchAsync(new[]
        {
            CreateParsedRecord(baseline.AddSeconds(-1), "10.0.0.1", "from-1"),
            CreateParsedRecord(baseline, "10.0.0.2", "from-2"),
        });

        var results = await Store.QueryAsync(new LogQuery(
            Limit: 10,
            Timeout: TimeSpan.FromSeconds(5),
            SourceAddress: "10.0.0.2"));

        Assert.Single(results);
        Assert.Equal("from-2", results[0].Message);
    }

    [Fact]
    public async Task QueryAsync_Severity_FiltersToExactMatch()
    {
        var baseline = DateTimeOffset.UtcNow;
        await Store.WriteBatchAsync(new[]
        {
            CreateParsedRecord(baseline.AddSeconds(-1), "10.0.0.1", "low-severity", severity: 3),
            CreateParsedRecord(baseline, "10.0.0.1", "high-severity", severity: 7),
        });

        var results = await Store.QueryAsync(new LogQuery(Limit: 10, Timeout: TimeSpan.FromSeconds(5), Severity: 7));

        Assert.Single(results);
        Assert.Equal("high-severity", results[0].Message);
    }

    [Fact]
    public async Task QueryAsync_SearchText_MatchesSubstringCaseInsensitive()
    {
        var baseline = DateTimeOffset.UtcNow;
        await Store.WriteBatchAsync(new[]
        {
            CreateParsedRecord(baseline.AddSeconds(-1), "10.0.0.1", "Connection RESET by peer"),
            CreateParsedRecord(baseline, "10.0.0.1", "normal heartbeat"),
        });

        var results = await Store.QueryAsync(new LogQuery(Limit: 10, Timeout: TimeSpan.FromSeconds(5), SearchText: "reset"));

        Assert.Single(results);
        Assert.Contains("RESET", results[0].Message);
    }

    [Fact]
    public async Task QueryAsync_CombinedConditions_AllMustMatch()
    {
        var baseline = DateTimeOffset.UtcNow;
        var matching = CreateParsedRecord(baseline, "10.0.0.5", "disk failure detected", severity: 2);
        var wrongSource = CreateParsedRecord(baseline, "10.0.0.6", "disk failure detected", severity: 2);
        var wrongSeverity = CreateParsedRecord(baseline, "10.0.0.5", "disk failure detected", severity: 6);
        await Store.WriteBatchAsync(new[] { matching, wrongSource, wrongSeverity });

        var results = await Store.QueryAsync(new LogQuery(
            Limit: 10,
            Timeout: TimeSpan.FromSeconds(5),
            SourceAddress: "10.0.0.5",
            Severity: 2,
            SearchText: "disk failure"));

        Assert.Single(results);
    }

    [Fact]
    public async Task QueryAsync_MessageProjectionLength_TruncatesToFirstNCharacters()
    {
        var longMessage = new string('a', 500);
        await Store.WriteBatchAsync(new[] { CreateParsedRecord(DateTimeOffset.UtcNow, "10.0.0.1", longMessage) });

        var results = await Store.QueryAsync(new LogQuery(Limit: 10, Timeout: TimeSpan.FromSeconds(5), MessageProjectionLength: 200));

        Assert.Single(results);
        Assert.Equal(200, results[0].Message!.Length);
        Assert.Equal(longMessage[..200], results[0].Message);
    }

    [Fact]
    public async Task QueryAsync_MessageShorterThanProjectionLength_NotPadded()
    {
        await Store.WriteBatchAsync(new[] { CreateParsedRecord(DateTimeOffset.UtcNow, "10.0.0.1", "short") });

        var results = await Store.QueryAsync(new LogQuery(Limit: 10, Timeout: TimeSpan.FromSeconds(5)));

        Assert.Single(results);
        Assert.Equal("short", results[0].Message);
    }

    [Fact]
    public async Task QueryAsync_Limit_RespectsUpperBound()
    {
        var baseline = DateTimeOffset.UtcNow;
        var records = Enumerable.Range(0, 5)
            .Select(i => CreateParsedRecord(baseline.AddSeconds(-i), $"10.0.0.{i}", $"message-{i}"))
            .ToArray();
        await Store.WriteBatchAsync(records);

        var results = await Store.QueryAsync(new LogQuery(Limit: 2, Timeout: TimeSpan.FromSeconds(5)));

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task QueryAsync_ExternalCancellation_PropagatesAsOperationCanceled()
    {
        await Store.WriteBatchAsync(new[] { CreateParsedRecord(DateTimeOffset.UtcNow, "10.0.0.1", "message") });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Store.QueryAsync(new LogQuery(Limit: 10, Timeout: TimeSpan.FromSeconds(5)), cts.Token));
    }

    [Fact]
    public async Task QueryLatestAsync_ReturnsRecordsInDescendingOrder()
    {
        var baseline = DateTimeOffset.UtcNow;
        await Store.WriteBatchAsync(new[]
        {
            CreateParsedRecord(baseline.AddSeconds(-2), "10.0.0.1", "first"),
            CreateParsedRecord(baseline.AddSeconds(-1), "10.0.0.2", "second"),
            CreateParsedRecord(baseline, "10.0.0.3", "third"),
        });

        var results = await Store.QueryLatestAsync(limit: 10, timeout: TimeSpan.FromSeconds(5));

        Assert.Equal(3, results.Count);
        Assert.Equal("third", results[0].Message);
        Assert.Equal("second", results[1].Message);
        Assert.Equal("first", results[2].Message);
    }

    // ------------------------------------------------------------------
    // 契約 5: 保持期間の削除（database.md §1.2「保持期間の削除」・§3）
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeleteOlderThanAsync_CutoffBoundary_DeletesOnlyStrictlyOlderRecords()
    {
        var cutoff = DateTimeOffset.UtcNow;
        await Store.WriteBatchAsync(new[]
        {
            CreateParsedRecord(cutoff.AddSeconds(-1), "10.0.0.1", "older-than-cutoff"),
            CreateParsedRecord(cutoff, "10.0.0.2", "exactly-at-cutoff"),
            CreateParsedRecord(cutoff.AddSeconds(1), "10.0.0.3", "newer-than-cutoff"),
        });

        var result = await Store.DeleteOlderThanAsync(cutoff);

        Assert.Equal(1, result.DeletedCount);

        var remaining = await Store.QueryLatestAsync(limit: 10, timeout: TimeSpan.FromSeconds(5));
        Assert.Equal(2, remaining.Count);
        Assert.DoesNotContain(remaining, r => r.Message == "older-than-cutoff");
        Assert.Contains(remaining, r => r.Message == "exactly-at-cutoff");
        Assert.Contains(remaining, r => r.Message == "newer-than-cutoff");
    }

    [Fact]
    public async Task DeleteOlderThanAsync_MoreRecordsThanBatchSize_DeletesAllInMultipleBatches()
    {
        var cutoff = DateTimeOffset.UtcNow;
        var totalRecords = RetentionConstants.DeleteBatchMaxSize + 250;
        var records = Enumerable.Range(0, totalRecords)
            .Select(i => CreateParsedRecord(cutoff.AddSeconds(-1 - i), "10.0.0.1", $"old-{i}"))
            .ToArray();
        await Store.WriteBatchAsync(records);

        var result = await Store.DeleteOlderThanAsync(cutoff);

        Assert.Equal(totalRecords, result.DeletedCount);

        var statistics = await Store.GetStatisticsAsync();
        Assert.Equal(0, statistics.RecordCount);
    }

    [Fact]
    public async Task DeleteOlderThanAsync_EmptyDatabase_ReturnsZero()
    {
        var result = await Store.DeleteOlderThanAsync(DateTimeOffset.UtcNow);

        Assert.Equal(0, result.DeletedCount);
    }

    [Fact]
    public async Task DeleteOlderThanAsync_NoMatchingRecords_ReturnsZero()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        await Store.WriteBatchAsync(new[] { CreateParsedRecord(DateTimeOffset.UtcNow, "10.0.0.1", "recent") });

        var result = await Store.DeleteOlderThanAsync(cutoff);

        Assert.Equal(0, result.DeletedCount);
    }

    // ------------------------------------------------------------------
    // 契約 6: 統計（database.md §1.2「統計」）
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetStatisticsAsync_EmptyStore_RecordCountIsZero()
    {
        var statistics = await Store.GetStatisticsAsync();

        Assert.Equal(0, statistics.RecordCount);
    }

    [Fact]
    public async Task GetStatisticsAsync_AfterWrite_RecordCountIncreases()
    {
        var before = await Store.GetStatisticsAsync();

        await Store.WriteBatchAsync(new[]
        {
            CreateParsedRecord(DateTimeOffset.UtcNow, "10.0.0.1", "first"),
            CreateParsedRecord(DateTimeOffset.UtcNow, "10.0.0.2", "second"),
        });

        var after = await Store.GetStatisticsAsync();

        Assert.Equal(before.RecordCount + 2, after.RecordCount);
    }

    [Fact]
    public async Task GetStatisticsAsync_DatabaseSize_HasValueOrExplicitUnavailableReason()
    {
        // database.md §1.2「統計」: サイズは「値があるか、取得不能の明示か」のどちらかであること。
        var statistics = await Store.GetStatisticsAsync();

        if (statistics.DatabaseSizeBytes is null)
        {
            Assert.False(string.IsNullOrWhiteSpace(statistics.DatabaseSizeUnavailableReason));
        }
        else
        {
            Assert.Null(statistics.DatabaseSizeUnavailableReason);
        }
    }

    // ------------------------------------------------------------------
    // システムイベント（database.md §2.3）の往復
    // ------------------------------------------------------------------

    [Fact]
    public async Task WriteSystemEventAsync_AllColumns_RoundTrip()
    {
        var baseline = DateTimeOffset.UtcNow;
        var systemEvent = new SystemEvent(
            Kind: "downtime.normal-stop",
            StartAt: baseline.AddMinutes(-5),
            EndAt: baseline,
            Approximate: false,
            Details: "conformance detail");

        var exception = await Record.ExceptionAsync(() => Store.WriteSystemEventAsync(systemEvent));

        Assert.Null(exception);
    }

    [Fact]
    public async Task WriteSystemEventAsync_ApproximateTrue_DoesNotThrow()
    {
        var baseline = DateTimeOffset.UtcNow;
        var systemEvent = new SystemEvent(
            Kind: "downtime.crash-approximate",
            StartAt: baseline.AddHours(-1),
            EndAt: baseline,
            Approximate: true);

        var exception = await Record.ExceptionAsync(() => Store.WriteSystemEventAsync(systemEvent));

        Assert.Null(exception);
    }

    [Fact]
    public async Task WriteSystemEventAsync_DetailsNull_DoesNotThrow()
    {
        var baseline = DateTimeOffset.UtcNow;
        var systemEvent = new SystemEvent(
            Kind: "retention.delete",
            StartAt: baseline.AddMinutes(-1),
            EndAt: baseline,
            Approximate: false,
            Details: null);

        var exception = await Record.ExceptionAsync(() => Store.WriteSystemEventAsync(systemEvent));

        Assert.Null(exception);
    }
}
