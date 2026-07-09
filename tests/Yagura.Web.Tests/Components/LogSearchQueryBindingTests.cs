using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Yagura.Abstractions.Observability;
using Yagura.Storage;
using Yagura.Web.Components.Common;
using Yagura.Web.Components.Pages;
using Yagura.Web.Diagnostics;
using Yagura.Web.ReverseDns;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// ログ検索の検索条件 ⇔ URL クエリの双方向反映（Issue #148）の対話的検証。
/// </summary>
/// <remarks>
/// <see cref="CommonComponentRenderHarness"/>（<c>HtmlRenderer</c> による直描画）は
/// <c>Router</c> を介さないため、本 PR で <c>LogSearch</c> に追加した
/// <c>[SupplyParameterFromQuery]</c> + <c>OnParametersSetAsync</c> の「URL ナビゲーションで
/// パラメータが再供給され、検索が再実行される」という対話的な状態遷移までは検証できない
/// （同ハーネスの doc コメントに明記された、bUnit 採用の再検討トリガーそのもの）。
/// このテストでは bUnit の <see cref="BunitContext"/> を使う。bUnit 2.x は
/// <c>[SupplyParameterFromQuery]</c> 対象パラメータへの直接の値注入を禁止し
/// （<c>NavigationManager.NavigateTo</c> 経由を強制する例外を送出する）、
/// <c>NavigationManager.NavigateTo</c> 呼び出しに応じて Router 相当の再供給を行う——
/// これにより実アプリの Router を介した挙動をそのまま模せる。
/// (1) URL クエリの供給値が <see cref="LogQuery"/> へ正しく反映されること、(2) 不正な値は
/// 「条件なし」に安全側で丸められること、(3) 検索ボタン押下が現在の条件を URL クエリへ
/// 反映すること、(4) 初回描画・ナビゲーションのたびに検索がちょうど 1 回だけ実行されること
/// （OnInitialized と OnParametersSetAsync の両方から実行してしまう二重実行の回帰を防ぐ）を
/// 検証する。
/// </remarks>
public sealed class LogSearchQueryBindingTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly RecordingLogStore _store = new();

    public LogSearchQueryBindingTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();
        _ctx.Services.AddSingleton<ILogStore>(_store);
        _ctx.Services.AddSingleton<IYaguraSystemStatusReader>(new FakeStatusReader());

        // LogSearch の結果テーブル（YaguraSourceAddress）が要求する逆引き表示の依存一式
        // （CommonComponentRenderHarness と同じ構成——描画テストは常に無効構成で行う）。
        _ctx.Services.AddSingleton(new ReverseDnsDisplayOptions(Enabled: false));
        _ctx.Services.AddSingleton<IReverseDnsLookup, SystemDnsReverseLookup>();
        _ctx.Services.AddSingleton<ReverseDnsMetrics>();
        _ctx.Services.AddSingleton<IReverseDnsResolver, ReverseDnsResolver>();
        _ctx.Services.AddSingleton(TimeProvider.System);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void NavigatingWithQueryParameters_ExecutesQueryWithMatchingTypedConditions()
    {
        var cut = _ctx.Render<LogSearch>();
        var navigation = _ctx.Services.GetRequiredService<NavigationManager>();

        navigation.NavigateTo(navigation.GetUriWithQueryParameters(new Dictionary<string, object?>
        {
            ["source"] = "10.0.0.5",
            ["severity"] = "3",
            ["facility"] = "4",
            ["parseStatus"] = nameof(ParseStatus.ParseFailed),
            ["q"] = "disk failure",
        }));

        Assert.NotNull(_store.LastQuery);
        Assert.Equal("10.0.0.5", _store.LastQuery!.SourceAddress);
        // 閾値方式（Issue #148）——URL の "severity" は SeverityAtMost にマップされる。
        Assert.Equal(3, _store.LastQuery.SeverityAtMost);
        Assert.Equal(4, _store.LastQuery.Facility);
        Assert.Equal(ParseStatus.ParseFailed, _store.LastQuery.ParseStatus);
        Assert.Equal("disk failure", _store.LastQuery.SearchText);

        // rendered が破綻しないことも合わせて確認（例外なく検索が完了した証跡）。
        Assert.Contains(UiText.SearchButton, cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void NavigatingWithReceivedAtRange_RoundTripsThroughServerTimeZone()
    {
        _ctx.Render<LogSearch>();
        var navigation = _ctx.Services.GetRequiredService<NavigationManager>();

        var instant = new DateTimeOffset(2026, 7, 9, 3, 30, 0, TimeSpan.Zero);
        var fromQuery = instant.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        navigation.NavigateTo(navigation.GetUriWithQueryParameter("from", fromQuery));

        Assert.NotNull(_store.LastQuery!.ReceivedAtFrom);
        Assert.True((_store.LastQuery.ReceivedAtFrom!.Value - instant).Duration() < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void NavigatingWithInvalidQueryValues_TreatedAsNoCondition()
    {
        var cut = _ctx.Render<LogSearch>();
        var navigation = _ctx.Services.GetRequiredService<NavigationManager>();

        // 改ざん・手編集された URL でも例外を出さず、「条件なし」として安全側に扱う。
        navigation.NavigateTo(navigation.GetUriWithQueryParameters(new Dictionary<string, object?>
        {
            ["severity"] = "not-a-number",
            ["facility"] = "also-not-a-number",
            ["parseStatus"] = "NoSuchStatus",
            ["from"] = "not-a-date",
        }));

        Assert.NotNull(_store.LastQuery);
        Assert.Null(_store.LastQuery!.SeverityAtMost);
        Assert.Null(_store.LastQuery.Facility);
        Assert.Null(_store.LastQuery.ParseStatus);
        Assert.Null(_store.LastQuery.ReceivedAtFrom);
        Assert.NotNull(cut.Markup);
    }

    [Fact]
    public void ClickingSearchButton_NavigatesToUrlReflectingCurrentConditions()
    {
        var cut = _ctx.Render<LogSearch>();
        var navigation = _ctx.Services.GetRequiredService<NavigationManager>();

        // 初期条件を URL クエリで与える——現在の検索フォームの状態はこの供給値から
        // OnParametersSetAsync が同期する（ui.md §4 の実装参照どおり）。
        navigation.NavigateTo(navigation.GetUriWithQueryParameters(new Dictionary<string, object?>
        {
            ["source"] = "10.0.0.5",
            ["severity"] = "3",
            ["facility"] = "4",
            ["parseStatus"] = nameof(ParseStatus.ParseFailed),
            ["q"] = "disk failure",
        }));

        cut.Find("button").Click();

        var query = QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query);

        Assert.Equal("10.0.0.5", query["source"].ToString());
        Assert.Equal("3", query["severity"].ToString());
        Assert.Equal("4", query["facility"].ToString());
        Assert.Equal(nameof(ParseStatus.ParseFailed), query["parseStatus"].ToString());
        Assert.Equal("disk failure", query["q"].ToString());
    }

    [Fact]
    public void SearchExecutesExactlyOnce_OnInitialRenderAndOnEachNavigation()
    {
        // 検索実行は OnParametersSetAsync に一本化している（OnInitialized はリスナ/保持地平の
        // 読み込みのみを担う）。両方から実行してしまう二重実行の回帰を防ぐ——初回描画・
        // その後の 1 回のナビゲーションのそれぞれで ILogStore.QueryAsync がちょうど 1 回だけ
        // 呼ばれること。
        _ctx.Render<LogSearch>();
        Assert.Equal(1, _store.QueryCount);

        var navigation = _ctx.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(navigation.GetUriWithQueryParameter("severity", "5"));

        Assert.Equal(2, _store.QueryCount);
        Assert.Equal(5, _store.LastQuery!.SeverityAtMost);
    }

    private sealed class RecordingLogStore : ILogStore
    {
        public LogQuery? LastQuery { get; private set; }

        public int QueryCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("閲覧画面は書き込みを行わない（L-5）。");

        public Task<IReadOnlyList<LogRecordSummary>> QueryLatestAsync(int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            QueryAsync(new LogQuery(Limit: limit, Timeout: timeout), cancellationToken);

        public Task<IReadOnlyList<LogRecordSummary>> QueryAsync(LogQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            QueryCount++;
            return Task.FromResult((IReadOnlyList<LogRecordSummary>)Array.Empty<LogRecordSummary>());
        }

        public Task WriteSystemEventAsync(SystemEvent systemEvent, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("閲覧画面は書き込みを行わない（L-5）。");

        public Task<DeleteOlderThanResult> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("閲覧画面は書き込みを行わない（L-5）。");

        public Task<LogStoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LogStoreStatistics(RecordCount: 0, DatabaseSizeBytes: 0));

        public Task<LogRecord?> FindByIdAsync(long id, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<LogRecord?>(null);

        public Task<IReadOnlyList<SystemEvent>> QuerySystemEventsAsync(DateTimeOffset? from, DateTimeOffset? to, int limit, TimeSpan timeout, string? kind = null, CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<SystemEvent>)Array.Empty<SystemEvent>());

        public Task<IReadOnlyList<SourceActivity>> QuerySourceActivityAsync(int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<SourceActivity>)Array.Empty<SourceActivity>());

        public Task<IReadOnlyList<SeverityCount>> QuerySeverityDistributionAsync(DateTimeOffset from, DateTimeOffset to, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<SeverityCount>)Array.Empty<SeverityCount>());

        public Task<IReadOnlyList<SourceActivity>> QueryTopTalkersAsync(DateTimeOffset from, DateTimeOffset to, int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<SourceActivity>)Array.Empty<SourceActivity>());
    }

    private sealed class FakeStatusReader : IYaguraSystemStatusReader
    {
        public YaguraSystemStatusSnapshot ReadCurrent() => new(
            TakenAt: DateTimeOffset.UtcNow,
            Counters: [],
            Spool: null,
            SpoolDegraded: false,
            Health: YaguraHealthReading.Ok,
            RetentionDays: 30,
            Listeners: []);
    }
}
