using Bunit;
using Microsoft.AspNetCore.Components;
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
/// ログ検索の「さらに読み込む」（database.md DB-11。カーソル（キーセット）ページングの
/// 追記型 UI。Issue #144 残課題）の対話的検証。
/// </summary>
/// <remarks>
/// <see cref="LogSearchQueryBindingTests"/> と同じ理由（<c>[SupplyParameterFromQuery]</c> の
/// 実供給・再供給を検証するため）で bUnit の <see cref="BunitContext"/> を使う。
/// 検証内容: (1) 初回検索が上限ぴったりの件数を返した場合に「さらに読み込む」ボタンが現れること、
/// (2) クリックすると直前ページの最終行 <c>(ReceivedAt, Id)</c> をカーソルとした
/// <see cref="LogQuery"/> が発行され、結果が末尾に追記されること、(3) 追記後のバッチが上限未満
/// なら「さらに読み込む」ボタンが消えること（続きが尽きたことの表現）、(4) 検索条件
/// （送信元等）はカーソル付きクエリにも引き継がれること、(5) カーソルは URL に載らないこと
/// （ui.md §4「カーソルは URL に載せない」の判断の実装確認）。
/// </remarks>
public sealed class LogSearchLoadMoreTests : IAsyncLifetime
{
    private readonly BunitContext _ctx = new();
    private readonly SequencedLogStore _store;

    public LogSearchLoadMoreTests()
    {
        // 総件数 15,000 件: 先頭ページ（上限 SearchLimit=10,000）がちょうど埋まり、
        // 2 ページ目は残り 5,000 件（上限未満）で「続きが尽きる」形にする。
        _store = new SequencedLogStore(totalRecords: LogSearch.SearchLimit + 5_000);

        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();

        // 検索ボタン（YaguraButton）が例外の受け皿（Issue #372）として注入する通知経路。
        _ctx.Services.AddScoped<Yagura.Web.Components.Common.IYaguraNotifier,
            Yagura.Web.Components.Common.YaguraSnackbarNotifier>();
        _ctx.Services.AddSingleton<ILogStore>(_store);
        _ctx.Services.AddSingleton<IYaguraSystemStatusReader>(new FakeStatusReader());

        // LogSearch の結果テーブル（YaguraSourceAddress）が要求する逆引き表示の依存一式
        // （LogSearchQueryBindingTests と同じ構成——描画テストは常に無効構成で行う）。
        _ctx.Services.AddSingleton(new ReverseDnsDisplayOptions(Enabled: false));
        _ctx.Services.AddSingleton<IReverseDnsLookup, SystemDnsReverseLookup>();
        _ctx.Services.AddSingleton<ReverseDnsMetrics>();
        _ctx.Services.AddSingleton<IReverseDnsResolver, ReverseDnsResolver>();
        _ctx.Services.AddSingleton(TimeProvider.System);
    }

    // MudPopoverProvider を実描画する本テストクラスは、その内部サービス
    // （PointerEventsNoneService）が IAsyncDisposable のみを実装するため、同期 Dispose では
    // 破棄に失敗する（実行して確認済み）。IAsyncLifetime（xUnit のテストごとの非同期初期化/破棄）
    // で非同期破棄経路を使う——LogSearchQueryBindingTests 等は MudPopoverProvider を描画しない
    // ため同期 Dispose のままで問題ない。
    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    [Fact]
    public void InitialSearch_FullBatch_ShowsLoadMoreButton()
    {
        var cut = RenderLogSearch();

        Assert.Single(_store.Queries);
        Assert.Null(_store.Queries[0].Cursor);
        Assert.Contains(UiText.SearchLoadMoreButton, cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ClickingLoadMore_QueriesWithCursorFromLastDisplayedRecord_AndAppendsResults()
    {
        var cut = RenderLogSearch();
        var firstPage = _store.LastReturnedPage;
        Assert.Equal(LogSearch.SearchLimit, firstPage.Count);
        var expectedCursorSource = firstPage[^1];

        FindLoadMoreButton(cut).Click();
        WaitForLoadMoreToSettle(cut);

        Assert.Equal(2, _store.Queries.Count);
        var secondQuery = _store.Queries[1];
        Assert.NotNull(secondQuery.Cursor);
        Assert.Equal(expectedCursorSource.ReceivedAt, secondQuery.Cursor!.ReceivedAt);
        Assert.Equal(expectedCursorSource.Id, secondQuery.Cursor.Id);

        // 2 ページ目は上限未満（5,000 件）——続きが尽きたため「さらに読み込む」は消える。
        Assert.DoesNotContain(UiText.SearchLoadMoreButton, cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ClickingLoadMore_PreservesCurrentFilterConditions()
    {
        var cut = RenderLogSearch();
        var navigation = _ctx.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(navigation.GetUriWithQueryParameter("source", "10.0.0.9"));

        Assert.Equal("10.0.0.9", _store.Queries[^1].SourceAddress);

        FindLoadMoreButton(cut).Click();
        WaitForLoadMoreToSettle(cut);

        var loadMoreQuery = _store.Queries[^1];
        Assert.Equal("10.0.0.9", loadMoreQuery.SourceAddress);
        Assert.NotNull(loadMoreQuery.Cursor);
    }

    [Fact]
    public void CursorIsNotReflectedInUrl_LoadMoreDoesNotNavigate()
    {
        // ui.md §4「カーソルは URL に載せない」の実装確認: 「さらに読み込む」はページ内の
        // 状態更新のみで、SubmitSearch のような NavigateTo は行わない。
        var cut = RenderLogSearch();
        var navigation = _ctx.Services.GetRequiredService<NavigationManager>();
        var uriBeforeLoadMore = navigation.Uri;

        FindLoadMoreButton(cut).Click();
        WaitForLoadMoreToSettle(cut);

        Assert.Equal(uriBeforeLoadMore, navigation.Uri);
        Assert.DoesNotContain("cursor", navigation.Uri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NewSearchAfterLoadMore_ResetsCursorState()
    {
        // 「さらに読み込む」でカーソル状態を進めた後、新しい検索（URL ナビゲーション）を行うと
        // 先頭ページから再開する——カーソルは画面ローカルの状態であり、新検索を跨いで残らない。
        var cut = RenderLogSearch();
        FindLoadMoreButton(cut).Click();
        // LoadMoreAsync は QueryAsync 完了後もチャート/受信断区間の再計算（LoadOutagesAndChartAsync）
        // を await し続ける——ここで完全な決着を待たずに次の操作へ進むと、その残り処理が
        // NavigateTo 後の新検索と非決定的に競合しうる（実測: CI で 1 度再現した）。
        WaitForLoadMoreToSettle(cut);
        Assert.Equal(2, _store.Queries.Count);

        var navigation = _ctx.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(navigation.GetUriWithQueryParameter("source", "10.0.0.1"));

        // 新検索のクエリが記録されるまで待つ（新検索の実行自体も非同期のため）。タイムアウトは
        // bUnit 既定（1 秒）ではなく明示的に延長する——CI の遅いランナーで既定値超過による
        // 偽陽性の失敗が反復したため（PR #296 の run 29538283065。ローカルでは常に 1 秒未満）。
        cut.WaitForAssertion(
            () => Assert.Contains(_store.Queries, q => q.SourceAddress == "10.0.0.1"),
            TimeSpan.FromSeconds(30));
        var newSearchQuery = _store.Queries.Last(q => q.SourceAddress == "10.0.0.1");
        Assert.Null(newSearchQuery.Cursor);
    }

    /// <summary>
    /// 「さらに読み込む」クリック後の非同期処理（<c>LoadMoreAsync</c>——結果の追記に加えて
    /// 時間軸チャート・受信断区間の再計算まで）が完全に決着するのを待つ。ボタンの表示が
    /// 「読み込み中…」から抜けたことをもって決着の合図とする——<c>_loadingMore</c> は
    /// <c>LoadMoreAsync</c> の <c>finally</c> でのみ <c>false</c> に戻るため。
    /// </summary>
    private static void WaitForLoadMoreToSettle(IRenderedComponent<ProviderHost> cut) =>
        // タイムアウトを明示的に延長する（bUnit 既定 1 秒は CI の遅いランナーで不足し、
        // 偽陽性の失敗が反復した——PR #296 の run 29538283065・2 回連続）。
        cut.WaitForAssertion(
            () => Assert.DoesNotContain(UiText.SearchLoadingMoreButton, cut.Markup, StringComparison.Ordinal),
            TimeSpan.FromSeconds(30));

    /// <summary>
    /// <see cref="LogSearch"/> を <c>MudPopoverProvider</c> と同居させて描画する。非空の検索結果を
    /// 描画すると <c>YaguraTable</c> の行末詳細ボタン（<c>MudTooltip</c> 内蔵）が
    /// <c>MudPopoverProvider</c> の存在を要求するため（<see cref="LogSearchQueryBindingTests"/> は
    /// 常に空配列を返すフェイクのため踏まない経路）——実アプリの <c>MainLayout</c> 相当の同居を
    /// <see cref="ProviderHost"/>（<c>CommonComponentRenderHarness.ProviderHost</c> と同じ発想）で
    /// 再現する。
    /// </summary>
    private IRenderedComponent<ProviderHost> RenderLogSearch() => _ctx.Render<ProviderHost>();

    private static AngleSharp.Dom.IElement FindLoadMoreButton(IRenderedComponent<ProviderHost> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Contains(UiText.SearchLoadMoreButton, StringComparison.Ordinal));

    /// <summary>
    /// 検証対象（<see cref="LogSearch"/>）を <c>MudPopoverProvider</c> と同居させて描画する
    /// 最小ホスト（実アプリの <c>MainLayout</c> 相当）。
    /// </summary>
    private sealed class ProviderHost : ComponentBase
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<LogSearch>(1);
            builder.CloseComponent();
        }
    }

    /// <summary>
    /// カーソル条件を実際に評価するフェイク <see cref="ILogStore"/>。<see cref="QueryAsync"/> のみ
    /// 実データで応答し、他の書き込み系メソッドは閲覧画面から呼ばれないため未サポートとする
    /// （<see cref="LogSearchQueryBindingTests.RecordingLogStore"/> と同じ方針）。
    /// </summary>
    private sealed class SequencedLogStore : ILogStore
    {
        private readonly List<LogRecordSummary> _all;

        public SequencedLogStore(int totalRecords)
        {
            var baseline = new DateTimeOffset(2026, 7, 9, 0, 0, 0, TimeSpan.Zero);
            // Id は挿入順（新しいほど大きい）——ReceivedAt 降順と Id 降順が一致する実 DB の性質を模す。
            _all = Enumerable.Range(0, totalRecords)
                .Select(i => new LogRecordSummary(
                    Id: totalRecords - i,
                    ReceivedAt: baseline.AddSeconds(-i),
                    SourceAddress: "10.0.0.1",
                    SourcePort: 514,
                    Protocol: Protocol.Udp,
                    ParseStatus: ParseStatus.Parsed,
                    DeviceTimestamp: null,
                    Facility: 1,
                    Severity: 5,
                    Hostname: "host",
                    AppName: "app",
                    ProcId: null,
                    MsgId: null,
                    StructuredData: null,
                    Message: $"record-{i}"))
                .ToList();
        }

        public List<LogQuery> Queries { get; } = [];

        public IReadOnlyList<LogRecordSummary> LastReturnedPage { get; private set; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("閲覧画面は書き込みを行わない（L-5）。");

        public Task<IReadOnlyList<LogRecordSummary>> QueryLatestAsync(int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            QueryAsync(new LogQuery(Limit: limit, Timeout: timeout), cancellationToken);

        public Task<IReadOnlyList<LogRecordSummary>> QueryAsync(LogQuery query, CancellationToken cancellationToken = default)
        {
            // 検索条件（SourceAddress 等）はここでは絞り込みに使わない——本フェイクの目的は
            // 「LogSearch が発行する LogQuery（特にカーソル）を記録して検証する」ことであり、
            // 実データの絞り込みは対象外（全レコードが SourceAddress="10.0.0.1" 固定のため、
            // 異なる値で絞り込むと空になり「さらに読み込む」ボタンの前提が崩れる）。
            // 検索条件が正しくカーソル付きクエリへ引き継がれることは Queries の記録内容で検証する
            // （ClickingLoadMore_PreservesCurrentFilterConditions 参照）。
            Queries.Add(query);

            IEnumerable<LogRecordSummary> source = _all;
            if (query.Cursor is { } cursor)
            {
                // 実 provider と同じシーク条件（database.md §1.2・DB-11）。
                source = source.Where(r =>
                    r.ReceivedAt < cursor.ReceivedAt ||
                    (r.ReceivedAt == cursor.ReceivedAt && r.Id < cursor.Id));
            }

            var page = source.Take(query.Limit).ToList();
            LastReturnedPage = page;
            return Task.FromResult((IReadOnlyList<LogRecordSummary>)page);
        }

        public Task WriteSystemEventAsync(SystemEvent systemEvent, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("閲覧画面は書き込みを行わない（L-5）。");

        public Task<DeleteOlderThanResult> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("閲覧画面は書き込みを行わない（L-5）。");

        public Task<LogStoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LogStoreStatistics(RecordCount: _all.Count, DatabaseSizeBytes: 0));

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

        public IReadOnlyList<YaguraFlowControlRejectionReading> ReadFlowControlRejections(int maxCount) => [];

        public IReadOnlyList<YaguraSourceSilenceReading> ReadSourceSilenceEntries() => [];
    }
}
