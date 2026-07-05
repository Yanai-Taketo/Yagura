using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Yagura.Storage;
using Yagura.Storage.Auditing;
using Yagura.Web.Diagnostics;

namespace Yagura.Web.Tests.ArchitectureTests;

/// <summary>
/// L-5 アーキテクチャテスト用の最小 <see cref="WebApplication"/> ホスト（M6-4。Issue #54）。
/// </summary>
/// <remarks>
/// <para>
/// <c>src/Yagura.Host/Program.cs</c> がリスナ分離（M6-1）で行う結線のうち、エンドポイント列挙の
/// 検証に必要な最小部分（<c>AddYaguraWebViewer</c> → <c>MapYaguraWebViewer</c> /
/// <c>MapYaguraAdmin</c>）だけを再現する。<c>ILogStore</c> / <c>IAuditRecorder</c> は実体を
/// 起動しないダミー実装で満たす——本ハーネスの目的はルーティング表の機械列挙であり、実際の
/// ログ永続化・監査書き込みは対象外。
/// </para>
/// <para>
/// <b>実サーバは listen しない</b>: <c>ConfigureKestrel(o =&gt; o.Listen(IPAddress.Loopback, 0))</c>
/// で OS 採番の loopback ポートに bind する。<c>EndpointDataSource</c> からのルーティング表取得は
/// <c>WebApplication.Build()</c> 後、実際に <c>StartAsync</c> を経ないと
/// <c>CompositeEndpointDataSource.Endpoints</c> が空集合のままであることを実機確認済み
/// （<c>MapRazorComponents</c> 側の遅延初期化——本コメントは推測ではなく実装時のスパイクテストで
/// 確認した事実）。そのため実際に Kestrel を起動する必要があるが、外部から到達可能な
/// アドレス・固定ポートには bind しない（loopback + OS 採番ポート = 0）。
/// </para>
/// </remarks>
internal sealed class ViewerHostHarness : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ViewerHostHarness(WebApplication app)
    {
        _app = app;
    }

    public static async Task<ViewerHostHarness> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();

        // Program.cs と同じ設定(M8-1): MapStaticAssets の開発時フォールバック
        // (catch-all {**path:file}。build マニフェスト使用時のみ登録される)を無効化し、
        // 静的アセットの配信面を「マニフェスト記載のアセットのみ」に固定する。
        // これにより本ハーネスの列挙面は publish 構成の実運用面と一致する(Program.cs の
        // 同キーのコメント参照)。
        builder.Configuration["DisableStaticAssetNotFoundRuntimeFallback"] = "true";

        builder.Services.AddYaguraWebViewer();
        builder.Services.AddSingleton<ILogStore>(_ => new NoopLogStore());
        builder.Services.AddSingleton<WebGuardMetrics>();
        builder.Services.AddSingleton<IAuditRecorder, NoopAuditRecorder>();
        builder.Services.AddSingleton<Yagura.Abstractions.Observability.IYaguraSystemStatusReader, NoopStatusReader>();

        // 閲覧・管理の両方を同一ホストにマップする(Program.cs と同じ構造。ポートによる
        // 到達可否の分離は実行時の ListenerPortGuardMiddleware が担うため、エンドポイント表
        // レベルでは両者は同居する——ViewerEndpointAllowlistTests のコメント参照)。
        builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 0));

        var app = builder.Build();

        // 静的アセットのマニフェストは明示指定する(M8-1): 既定解決は
        // {ApplicationName}.staticwebassets.endpoints.json だが、テスト実行時の
        // ApplicationName はエントリアセンブリ由来の "testhost" になり解決できない。
        // テスト出力には Yagura.Web(RCL)単位のマニフェストが生成されることを実機確認済みの
        // ため、それを指定する(MudBlazor 等の参照パッケージのアセットを含む——
        // YaguraWebViewerExtensions.MapYaguraWebViewer の引数コメント参照)。
        app.MapYaguraWebViewer("Yagura.Web.staticwebassets.endpoints.json");
        app.MapYaguraAdmin();

        await app.StartAsync();

        return new ViewerHostHarness(app);
    }

    /// <summary>登録済みの全エンドポイント(閲覧・管理の両方)。</summary>
    public IReadOnlyList<Endpoint> GetAllEndpoints() =>
        _app.Services.GetServices<EndpointDataSource>()
            .SelectMany(ds => ds.Endpoints)
            .ToList();

    /// <summary>
    /// 閲覧許可リストの対象となるエンドポイント(<see cref="ListenerPortGuardEndpointMetadata.Admin"/>
    /// を持たないもの)。
    /// </summary>
    public IReadOnlyList<RouteEndpoint> GetViewerEndpoints() =>
        GetAllEndpoints()
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ListenerPortGuardEndpointMetadata>() is not { Kind: ListenerKind.Admin })
            .ToList();

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private sealed class NoopLogStore : ILogStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<LogRecordSummary>> QueryLatestAsync(int limit, TimeSpan timeout, CancellationToken cancellationToken = default)
            => Task.FromResult((IReadOnlyList<LogRecordSummary>)new List<LogRecordSummary>());

        public Task<IReadOnlyList<LogRecordSummary>> QueryAsync(LogQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult((IReadOnlyList<LogRecordSummary>)new List<LogRecordSummary>());

        public Task WriteSystemEventAsync(SystemEvent systemEvent, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<DeleteOlderThanResult> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("本ハーネスはルーティング表の列挙専用であり、実データ操作は対象外。");

        public Task<LogStoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("本ハーネスはルーティング表の列挙専用であり、実データ操作は対象外。");

        public Task<LogRecord?> FindByIdAsync(long id, TimeSpan timeout, CancellationToken cancellationToken = default)
            => Task.FromResult<LogRecord?>(null);

        public Task<IReadOnlyList<SystemEvent>> QuerySystemEventsAsync(DateTimeOffset? from, DateTimeOffset? to, int limit, TimeSpan timeout, CancellationToken cancellationToken = default)
            => Task.FromResult((IReadOnlyList<SystemEvent>)new List<SystemEvent>());

        public Task<IReadOnlyList<SourceActivity>> QuerySourceActivityAsync(int limit, TimeSpan timeout, CancellationToken cancellationToken = default)
            => Task.FromResult((IReadOnlyList<SourceActivity>)new List<SourceActivity>());
    }

    private sealed class NoopStatusReader : Yagura.Abstractions.Observability.IYaguraSystemStatusReader
    {
        public Yagura.Abstractions.Observability.YaguraSystemStatusSnapshot ReadCurrent() => new(
            TakenAt: DateTimeOffset.UtcNow,
            Counters: [],
            Spool: null,
            SpoolDegraded: false,
            Health: Yagura.Abstractions.Observability.YaguraHealthReading.Ok,
            RetentionDays: 30,
            Listeners: []);
    }

    private sealed class NoopAuditRecorder : IAuditRecorder
    {
        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
