using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Yagura.Storage;
using Yagura.Abstractions.Auditing;
using Yagura.Web.Administration;
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

    /// <param name="forwarderMsiSource">
    /// <see cref="Yagura.Web.ForwarderKit.IForwarderMsiSource"/> の差し替え（ADR-0008 設計条件 9。
    /// 既定 <see langword="null"/> は常に未検出を返す <see cref="StubForwarderMsiSource"/>）。
    /// <c>ForwarderKitDownloadEndpointTests</c> の includeMsi 系ケースが使う。
    /// </param>
    /// <param name="logStore">
    /// <see cref="ILogStore"/> の差し替え（既定 <see langword="null"/> は空集合のみを返す
    /// <see cref="NoopLogStore"/>）。<c>LogSearchCsvExportEndpointTests</c>（Issue #157）が
    /// 実データを積んだフェイクへ差し替えて使う。
    /// </param>
    /// <param name="appAuthEnabled">
    /// アプリ独自認証を有効にした構成でホストを起動する（ADR-0010 Phase 1。
    /// <c>AdminAuthLoginEndpointTests</c> の実 HTTP ログインフロー検証が使う。既定 false =
    /// 従来どおり全認証無効）。
    /// </param>
    /// <param name="appAuthenticator">
    /// <see cref="Yagura.Abstractions.Administration.IAppAdminAuthenticator"/> の差し替え
    /// （既定 <see langword="null"/> は常に例外のスタブ）。
    /// </param>
    /// <param name="auditRecorder">
    /// <see cref="IAuditRecorder"/> の差し替え（既定 <see langword="null"/> は何もしないスタブ。
    /// 監査発火のアサーションを行うテストが記録型フェイクへ差し替える）。
    /// </param>
    /// <param name="windowsAuthEnabled">
    /// Windows 統合認証を有効にした構成でホストを起動する（既定 false = 従来どおり無効。
    /// <c>/admin/login/windows</c> エンドポイントの登録可否を検証するテストが差し替える——
    /// <see cref="Yagura.Web.Administration.AdminAuthEndpoints.MapAdminAuthEndpoints"/> 参照）。
    /// </param>
    public static async Task<ViewerHostHarness> StartAsync(
        Yagura.Web.ForwarderKit.IForwarderMsiSource? forwarderMsiSource = null,
        ILogStore? logStore = null,
        bool appAuthEnabled = false,
        Yagura.Abstractions.Administration.IAppAdminAuthenticator? appAuthenticator = null,
        IAuditRecorder? auditRecorder = null,
        bool windowsAuthEnabled = false)
    {
        var builder = WebApplication.CreateBuilder();

        // Program.cs と同じ設定(M8-1): MapStaticAssets の開発時フォールバック
        // (catch-all {**path:file}。build マニフェスト使用時のみ登録される)を無効化し、
        // 静的アセットの配信面を「マニフェスト記載のアセットのみ」に固定する。
        // これにより本ハーネスの列挙面は publish 構成の実運用面と一致する(Program.cs の
        // 同キーのコメント参照)。
        builder.Configuration["DisableStaticAssetNotFoundRuntimeFallback"] = "true";

        builder.Services.AddYaguraWebViewer();
        builder.Services.AddSingleton<ILogStore>(_ => logStore ?? new NoopLogStore());
        builder.Services.AddSingleton<WebGuardMetrics>();
        builder.Services.AddSingleton<IAuditRecorder>(_ => auditRecorder ?? new NoopAuditRecorder());
        builder.Services.AddSingleton<Yagura.Abstractions.Observability.IYaguraSystemStatusReader, NoopStatusReader>();

        // M8-4: circuit 統治・管理画面が要求するサービス（Program.cs と同じ結線の最小形。
        // ポート値はルーティング列挙では到達判定に使われないため既定 8515 でよいが、
        // 管理画面（AdminScreenLayout 経由）を実 HTTP で取得するテスト（appAuthEnabled: true）
        // では prerender 段のリスナ帰属検査（AdminScreenAccessPolicy——実ローカルポートと
        // YaguraAdminListenerPort の一致判定）を通す必要があるため、実際に bind するポートを
        // 事前に確保して一致させる。
        var adminPort = 8515;
        if (appAuthEnabled)
        {
            using var probe = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);
            probe.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            adminPort = ((System.Net.IPEndPoint)probe.LocalEndPoint!).Port;
        }

        builder.Services.AddSingleton(new Yagura.Web.Administration.YaguraAdminListenerPort(adminPort));
        builder.Services.AddYaguraAdmin();
        builder.Services.AddSingleton<Yagura.Abstractions.Administration.ISetupWizardService, StubSetupWizardService>();
        builder.Services.AddSingleton<Yagura.Abstractions.Administration.IPromotionWizardService, StubPromotionWizardService>();

        // ADR-0010 Phase 1: 管理 UI 認証(既定は無効——ルーティング表の機械列挙用。
        // appAuthEnabled: true はアプリ独自認証の実 HTTP フロー検証用の構成。
        // windowsAuthEnabled: true は /admin/login/windows の条件付き登録を検証する構成)。
        builder.Services.AddYaguraAdminAuthentication(
            windowsAuthEnabled: windowsAuthEnabled, kerberosOnly: false, appAuthEnabled: appAuthEnabled);
        builder.Services.AddSingleton(new Yagura.Web.Administration.AdminAuthenticationRuntimeOptions(
            RequireAuthentication: false, WindowsAuthEnabled: windowsAuthEnabled, AppAuthEnabled: appAuthEnabled));
        builder.Services.AddSingleton<Yagura.Abstractions.Administration.IAdminAuthenticationAdminService, StubAdminAuthenticationAdminService>();
        builder.Services.AddSingleton<Yagura.Abstractions.Administration.IAppAdminAuthenticator>(
            _ => appAuthenticator ?? new StubAppAdminAuthenticator());

        // ADR-0008 設計条件 9: フォワーダキット生成画面・ダウンロードエンドポイントが要求する
        // IForwarderMsiSource（Program.cs と同じくデータルート配下 forwarder を注入する結線だが、
        // 本ハーネスはルーティング表の機械列挙・実 HTTP 応答検証が目的のため既定は未検出扱いの
        // スタブとする——実処理は ForwarderMsiFilterTests / ForwarderKitBuilderTests が検証する）。
        builder.Services.AddSingleton(forwarderMsiSource ?? new StubForwarderMsiSource());

        // 閲覧・管理の両方を同一ホストにマップする(Program.cs と同じ構造。ポートによる
        // 到達可否の分離は実行時の ListenerPortGuardMiddleware が担うため、エンドポイント表
        // レベルでは両者は同居する——ViewerEndpointAllowlistTests のコメント参照)。
        // appAuthEnabled: true では事前確保した adminPort に bind する（上記コメント参照）。
        builder.WebHost.ConfigureKestrel(o => o.Listen(
            System.Net.IPAddress.Loopback, appAuthEnabled ? adminPort : 0));

        var app = builder.Build();

        // Program.cs と同じ配線: Razor Components ページ（antiforgery メタデータ付き）の
        // 描画・アンチフォージェリ検証に必要（GET /admin/login のトークン発行と
        // POST /admin/login/app の検証——ADR-0010 Phase 1 の実 HTTP フローテストが使う）。
        app.UseAntiforgery();

        // 静的アセットのマニフェストは明示指定する(M8-1): 既定解決は
        // {ApplicationName}.staticwebassets.endpoints.json だが、テスト実行時の
        // ApplicationName はエントリアセンブリ由来の "testhost" になり解決できない。
        // テスト出力には Yagura.Web(RCL)単位のマニフェストが生成されることを実機確認済みの
        // ため、それを指定する(MudBlazor 等の参照パッケージのアセットを含む——
        // YaguraWebViewerExtensions.MapYaguraWebViewer の引数コメント参照)。
        var razorComponents = app.MapYaguraWebViewer("Yagura.Web.staticwebassets.endpoints.json");
        app.MapYaguraAdmin(razorComponents, windowsAuthEnabled: windowsAuthEnabled);

        await app.StartAsync();

        return new ViewerHostHarness(app);
    }

    /// <summary>登録済みの全エンドポイント(閲覧・管理の両方)。</summary>
    public IReadOnlyList<Endpoint> GetAllEndpoints() =>
        _app.Services.GetServices<EndpointDataSource>()
            .SelectMany(ds => ds.Endpoints)
            .ToList();

    /// <summary>
    /// 実際に bind した loopback アドレス(OS 採番ポート込み)。ForwarderKitDownloadEndpointTests
    /// のように実 HTTP 応答(ステータス・ヘッダ・ボディ)を検証する場合に使う
    /// (<c>IServerAddressesFeature</c> は <c>StartAsync</c> 後にのみ確定値を持つ——
    /// ASP.NET Core の実装上の制約であり、本ハーネスの <see cref="StartAsync"/> が
    /// 既に <c>app.StartAsync()</c> を経ているため確定済みの値が返る)。
    /// </summary>
    public Uri GetBaseAddress()
    {
        var addressesFeature = _app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();

        var address = addressesFeature?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("bind 済みアドレスが取得できない。");

        return new Uri(address);
    }

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

        public Task<IReadOnlyList<SystemEvent>> QuerySystemEventsAsync(DateTimeOffset? from, DateTimeOffset? to, int limit, TimeSpan timeout, string? kind = null, CancellationToken cancellationToken = default)
            => Task.FromResult((IReadOnlyList<SystemEvent>)new List<SystemEvent>());

        public Task<IReadOnlyList<SourceActivity>> QuerySourceActivityAsync(int limit, TimeSpan timeout, CancellationToken cancellationToken = default)
            => Task.FromResult((IReadOnlyList<SourceActivity>)new List<SourceActivity>());

        public Task<IReadOnlyList<SeverityCount>> QuerySeverityDistributionAsync(DateTimeOffset from, DateTimeOffset to, TimeSpan timeout, CancellationToken cancellationToken = default)
            => Task.FromResult((IReadOnlyList<SeverityCount>)new List<SeverityCount>());

        public Task<IReadOnlyList<SourceActivity>> QueryTopTalkersAsync(DateTimeOffset from, DateTimeOffset to, int limit, TimeSpan timeout, CancellationToken cancellationToken = default)
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

    /// <summary>
    /// <see cref="Yagura.Web.ForwarderKit.IForwarderMsiSource"/> の既定スタブ（ADR-0008 設計条件 9）:
    /// 常に未検出を返す。<see cref="ForwarderKitDownloadEndpointTests"/> の includeMsi 系ケースは
    /// 個別に差し替えたインスタンスを DI へ追加登録して使う。
    /// </summary>
    private sealed class StubForwarderMsiSource : Yagura.Web.ForwarderKit.IForwarderMsiSource
    {
        public string FolderPath => @"C:\ProgramData\Yagura\forwarder";

        public Yagura.Web.ForwarderKit.ForwarderMsiLookup Lookup(Yagura.Web.ForwarderKit.ForwarderMsiArchitecture architecture) =>
            Yagura.Web.ForwarderKit.ForwarderMsiLookup.NotFound();
    }

    /// <summary>
    /// 管理画面（Razor Components ページ）の DI 要求を満たすためのスタブ（本ハーネスの目的は
    /// ルーティング表の機械列挙であり、ウィザードの実処理は対象外——実処理は
    /// <c>Yagura.Host.Tests</c> の SetupWizardServiceTests / PromotionWizardServiceTests が検証する）。
    /// </summary>
    private sealed class StubSetupWizardService : Yagura.Abstractions.Administration.ISetupWizardService
    {
        public Task<Yagura.Abstractions.Administration.SetupWizardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("ルーティング列挙専用ハーネス。");

        public Task<Yagura.Abstractions.Administration.SetupWizardSnapshot> ConfirmStepAsync(
            Yagura.Abstractions.Administration.SetupWizardStep step,
            IReadOnlyDictionary<string, string> values,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("ルーティング列挙専用ハーネス。");

        public Task<Yagura.Abstractions.Administration.SetupWizardApplyResult> ApplyAsync(
            string idempotencyToken,
            string? operatorAddress = null,
            string? operatorScheme = null,
            string? operatorPrincipal = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("ルーティング列挙専用ハーネス。");
    }

    private sealed class StubPromotionWizardService : Yagura.Abstractions.Administration.IPromotionWizardService
    {
        public Task<Yagura.Abstractions.Administration.PromotionWizardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("ルーティング列挙専用ハーネス。");

        public Task<Yagura.Abstractions.Administration.PromotionWizardSnapshot> SetConnectionFormAsync(
            Yagura.Abstractions.Administration.PromotionConnectionForm form,
            string? password = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("ルーティング列挙専用ハーネス。");

        public Task<Yagura.Abstractions.Administration.PromotionWizardSnapshot> SetRawConnectionStringAsync(
            string connectionString,
            string? password = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("ルーティング列挙専用ハーネス。");

        public Task<Yagura.Abstractions.Administration.PromotionValidationResult> ValidateConnectionAsync(
            string? operatorAddress = null,
            string? operatorScheme = null,
            string? operatorPrincipal = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("ルーティング列挙専用ハーネス。");

        public Task<Yagura.Abstractions.Administration.PromotionWizardSnapshot> ChooseOldDatabaseDisposalAsync(
            Yagura.Abstractions.Administration.OldDatabaseDisposal disposal,
            string? evacuationDirectory = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("ルーティング列挙専用ハーネス。");

        public Task<Yagura.Abstractions.Administration.PromotionApplyResult> ExecuteAsync(
            string idempotencyToken,
            string? operatorAddress = null,
            string? operatorScheme = null,
            string? operatorPrincipal = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("ルーティング列挙専用ハーネス。");
    }

    private sealed class StubAdminAuthenticationAdminService : Yagura.Abstractions.Administration.IAdminAuthenticationAdminService
    {
        public Task<Yagura.Abstractions.Administration.AdminAuthenticationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("ルーティング列挙専用ハーネス。");

        public Task<Yagura.Abstractions.Administration.AdminAuthenticationStatus> ConfigureAsync(
            bool windowsAuthEnabled,
            bool kerberosOnly,
            bool appAuthEnabled,
            bool requireForLoopback,
            string? newAppUsername,
            string? newAppPassword,
            string? operatorAddress = null,
            string? operatorScheme = null,
            string? operatorPrincipal = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("ルーティング列挙専用ハーネス。");
    }

    private sealed class StubAppAdminAuthenticator : Yagura.Abstractions.Administration.IAppAdminAuthenticator
    {
        public Task<Yagura.Abstractions.Administration.AppAuthenticationOutcome> TryAuthenticateAsync(
            string username, string password, Yagura.Abstractions.Administration.AdminAuthAttemptContext context,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("ルーティング列挙専用ハーネス。");
    }
}
