using System.Text;
using Yagura.Storage;
using Yagura.Web.Tests.ArchitectureTests;

namespace Yagura.Web.Tests.Export;

/// <summary>
/// <c>/search/export.csv</c>（<c>YaguraWebViewerExtensions.MapLogSearchCsvExport</c>）の実 HTTP
/// 応答テスト（Issue #157）。UTF-8 BOM・RFC 4180・CSV インジェクション対策・件数上限は個々に
/// <see cref="CsvFieldTests"/> / <see cref="LogRecordCsvWriterTests"/> が単体で確認済みのため、
/// 本テストは「エンドポイントとしての結線」（クエリパラメータの引き渡し・応答ヘッダー・
/// 応答本文の先頭バイト列（BOM）」を確認する。
/// </summary>
public sealed class LogSearchCsvExportEndpointTests
{
    [Fact]
    public async Task Export_NoConditions_Returns200WithCsvContentTypeAndAttachment()
    {
        var logStore = new FakeLogStore([Sample(1)]);
        await using var harness = await ViewerHostHarness.StartAsync(logStore: logStore);
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        var response = await client.GetAsync("/search/export.csv");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("attachment", response.Content.Headers.ContentDisposition?.ToString() ?? string.Empty);
        Assert.Contains(".csv", response.Content.Headers.ContentDisposition?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task Export_ResponseBody_StartsWithUtf8Bom()
    {
        var logStore = new FakeLogStore([Sample(1)]);
        await using var harness = await ViewerHostHarness.StartAsync(logStore: logStore);
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        var response = await client.GetAsync("/search/export.csv");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        // UTF-8 BOM（EF BB BF。Issue #157 の受け入れ条件——Excel の日本語文字化け耐性）。
        Assert.True(bytes.Length >= 3);
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);
    }

    [Fact]
    public async Task Export_ResponseBody_ContainsHeaderAndDataRows()
    {
        var logStore = new FakeLogStore([Sample(1), Sample(2)]);
        await using var harness = await ViewerHostHarness.StartAsync(logStore: logStore);
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        var response = await client.GetAsync("/search/export.csv");
        var text = await ReadAsUtf8TextWithoutBomAsync(response);

        Assert.StartsWith("受信時刻,送信元アドレス", text, StringComparison.Ordinal);
        Assert.Contains("192.0.2.1", text, StringComparison.Ordinal);
        Assert.Contains("192.0.2.2", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_QueryParameters_ArePassedThroughToLogQuery()
    {
        // クエリキーは検索画面の URL 共有形式（Issue #148。LogSearch.razor の
        // BuildQueryParameters）と同一: from・to・source・severity・facility・parseStatus・q。
        var logStore = new FakeLogStore([]);
        await using var harness = await ViewerHostHarness.StartAsync(logStore: logStore);
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        var from = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 7, 9, 0, 0, 0, TimeSpan.Zero);
        var url = "/search/export.csv" +
            $"?from={Uri.EscapeDataString(from.UtcDateTime.ToString("O"))}" +
            $"&to={Uri.EscapeDataString(to.UtcDateTime.ToString("O"))}" +
            "&source=192.0.2.10" +
            "&severity=3" +
            "&facility=4" +
            "&parseStatus=ParseFailed" +
            "&q=auth";

        var response = await client.GetAsync(url);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(logStore.LastQuery);
        var query = logStore.LastQuery!;
        Assert.Equal(from, query.ReceivedAtFrom);
        Assert.Equal(to, query.ReceivedAtTo);
        Assert.Equal("192.0.2.10", query.SourceAddress);
        Assert.Equal(3, query.SeverityAtMost);
        Assert.Equal(4, query.Facility);
        Assert.Equal(ParseStatus.ParseFailed, query.ParseStatus);
        Assert.Equal("auth", query.SearchText);
    }

    [Fact]
    public async Task Export_NoConditions_QueriesWithoutFilters()
    {
        var logStore = new FakeLogStore([]);
        await using var harness = await ViewerHostHarness.StartAsync(logStore: logStore);
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        await client.GetAsync("/search/export.csv");

        Assert.NotNull(logStore.LastQuery);
        var query = logStore.LastQuery!;
        Assert.Null(query.ReceivedAtFrom);
        Assert.Null(query.ReceivedAtTo);
        Assert.Null(query.SourceAddress);
        Assert.Null(query.SeverityAtMost);
        Assert.Null(query.Facility);
        Assert.Null(query.ParseStatus);
        Assert.Null(query.SearchText);
    }

    [Fact]
    public async Task Export_InvalidQueryValues_AreTreatedAsNoCondition_Not400()
    {
        // 不正な値は検索画面と同じく例外を出さず「条件なし」に安全側で丸める
        // （LogSearch.razor の TryParseInt / TryParseParseStatus / ParseServerWallClock と
        // 同じ寛容規則。改変された URL で 400 にしない——検索画面との解釈一致）。
        var logStore = new FakeLogStore([]);
        await using var harness = await ViewerHostHarness.StartAsync(logStore: logStore);
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        var response = await client.GetAsync(
            "/search/export.csv?from=bogus&to=also-bogus&severity=high&facility=kern&parseStatus=Nope");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(logStore.LastQuery);
        var query = logStore.LastQuery!;
        Assert.Null(query.ReceivedAtFrom);
        Assert.Null(query.ReceivedAtTo);
        Assert.Null(query.SeverityAtMost);
        Assert.Null(query.Facility);
        Assert.Null(query.ParseStatus);
    }

    [Fact]
    public async Task Export_RequestsFullMessageProjection_NotTruncatedTo200Chars()
    {
        // 一覧射影の 200 文字切り詰め(M-10)を回避し、全文を出力する契約の確認
        // (LogQuery.MessageProjectionLength の呼び出し側オプション——LogQuery.cs:35 の既存契約)。
        var longMessage = new string('a', 500);
        var logStore = new FakeLogStore([Sample(1) with { Message = longMessage }]);
        await using var harness = await ViewerHostHarness.StartAsync(logStore: logStore);
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        var response = await client.GetAsync("/search/export.csv");
        var text = await ReadAsUtf8TextWithoutBomAsync(response);

        Assert.Contains(longMessage, text, StringComparison.Ordinal);
        Assert.True(logStore.LastQuery?.MessageProjectionLength > 200);
    }

    [Fact]
    public async Task Export_ResultCountBelowLimit_DoesNotSetTruncatedHeader()
    {
        var logStore = new FakeLogStore([Sample(1)]);
        await using var harness = await ViewerHostHarness.StartAsync(logStore: logStore);
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        var response = await client.GetAsync("/search/export.csv");

        Assert.False(response.Headers.Contains("X-Yagura-Csv-Truncated"));
    }

    [Fact]
    public async Task Export_ResultCountAtLimit_SetsTruncatedHeader()
    {
        // FakeLogStore はクエリの Limit と同じ件数を返すことで「上限に到達した」ことを模擬する
        // (実 provider の挙動——LIMIT 句で切り詰められた結果は要求件数と同数になる)。
        var logStore = new FakeLogStore(returnExactlyLimit: true);
        await using var harness = await ViewerHostHarness.StartAsync(logStore: logStore);
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        var response = await client.GetAsync("/search/export.csv");

        Assert.True(response.Headers.Contains("X-Yagura-Csv-Truncated"));
        Assert.Equal("true", response.Headers.GetValues("X-Yagura-Csv-Truncated").Single());
    }

    [Fact]
    public async Task Export_Get_IsTheOnlySupportedMethod()
    {
        var logStore = new FakeLogStore([]);
        await using var harness = await ViewerHostHarness.StartAsync(logStore: logStore);
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        var response = await client.PostAsync("/search/export.csv", content: null);

        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    private static async Task<string> ReadAsUtf8TextWithoutBomAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? utf8NoBom.GetString(bytes, 3, bytes.Length - 3)
            : utf8NoBom.GetString(bytes);
    }

    private static LogRecordSummary Sample(int seed) => new(
        Id: seed,
        ReceivedAt: new DateTimeOffset(2026, 7, 9, 3, 0, 0, TimeSpan.Zero).AddMinutes(seed),
        SourceAddress: $"192.0.2.{seed}",
        SourcePort: 514,
        Protocol: Protocol.Udp,
        ParseStatus: ParseStatus.Parsed,
        DeviceTimestamp: null,
        Facility: 3,
        Severity: 5,
        Hostname: null,
        AppName: null,
        ProcId: null,
        MsgId: null,
        StructuredData: null,
        Message: $"sample message {seed}");

    /// <summary>
    /// <see cref="ILogStore"/> のフェイク（Issue #157 のエンドポイントテスト専用）。
    /// <see cref="QueryAsync"/> だけを実体化し、渡された <see cref="LogQuery"/> を
    /// <see cref="LastQuery"/> として記録する。他メンバーは本テストの対象外
    /// （<c>ViewerHostHarness.NoopLogStore</c> と同じ流儀で <see cref="NotSupportedException"/>）。
    /// </summary>
    private sealed class FakeLogStore : ILogStore
    {
        private readonly IReadOnlyList<LogRecordSummary> _records;
        private readonly bool _returnExactlyLimit;

        public FakeLogStore(IReadOnlyList<LogRecordSummary> records)
        {
            _records = records;
            _returnExactlyLimit = false;
        }

        public FakeLogStore(bool returnExactlyLimit)
        {
            _records = [];
            _returnExactlyLimit = returnExactlyLimit;
        }

        public LogQuery? LastQuery { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("本フェイクは QueryAsync 専用。");

        public Task<IReadOnlyList<LogRecordSummary>> QueryLatestAsync(int limit, TimeSpan timeout, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("本フェイクは QueryAsync 専用。");

        public Task<IReadOnlyList<LogRecordSummary>> QueryAsync(LogQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;

            if (_returnExactlyLimit)
            {
                var records = Enumerable.Range(1, query.Limit).Select(Sample).ToList();
                return Task.FromResult<IReadOnlyList<LogRecordSummary>>(records);
            }

            return Task.FromResult(_records);
        }

        public Task WriteSystemEventAsync(SystemEvent systemEvent, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("本フェイクは QueryAsync 専用。");

        public Task<DeleteOlderThanResult> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("本フェイクは QueryAsync 専用。");

        public Task<LogStoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("本フェイクは QueryAsync 専用。");

        public Task<LogRecord?> FindByIdAsync(long id, TimeSpan timeout, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("本フェイクは QueryAsync 専用。");

        public Task<IReadOnlyList<SystemEvent>> QuerySystemEventsAsync(DateTimeOffset? from, DateTimeOffset? to, int limit, TimeSpan timeout, string? kind = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("本フェイクは QueryAsync 専用。");

        public Task<IReadOnlyList<SourceActivity>> QuerySourceActivityAsync(int limit, TimeSpan timeout, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("本フェイクは QueryAsync 専用。");

        public Task<IReadOnlyList<SeverityCount>> QuerySeverityDistributionAsync(DateTimeOffset from, DateTimeOffset to, TimeSpan timeout, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("本フェイクは QueryAsync 専用。");

        public Task<IReadOnlyList<SourceActivity>> QueryTopTalkersAsync(DateTimeOffset from, DateTimeOffset to, int limit, TimeSpan timeout, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("本フェイクは QueryAsync 専用。");
    }
}
