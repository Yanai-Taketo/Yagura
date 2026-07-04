using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yagura.Ingestion;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Udp;
using Yagura.Storage;
using Yagura.Storage.Sqlite;
using Yagura.Web;

namespace Yagura.Host;

public static class Program
{
    /// <summary>
    /// データルートを上書きする環境変数名。本格的な設定基盤（JSON 設定・ウィザード）は M3。
    /// </summary>
    public const string DataRootEnvironmentVariable = "YAGURA_DATAROOT";

    /// <summary>
    /// UDP 受信ポートを上書きする環境変数名。<c>0</c> を指定すると OS がポートを採番する
    /// （テスト用。<see cref="IngestionPipeline.BoundPort"/> で実ポートを取得できる）。
    /// </summary>
    public const string UdpPortEnvironmentVariable = "YAGURA_UDP_PORT";

    /// <summary>
    /// 閲覧 HTTP リスナのポートを上書きする環境変数名。<c>0</c> を指定すると OS がポートを
    /// 採番する（テスト用）。
    /// </summary>
    public const string HttpPortEnvironmentVariable = "YAGURA_HTTP_PORT";

    /// <summary>
    /// 閲覧 HTTP リスナの既定ポート（暫定値）。
    /// </summary>
    /// <remarks>
    /// <b>CF-1（configuration.md での既定確定）待ちの暫定値</b>。設計上の既定は「LAN 公開」
    /// だが、閲覧/管理リスナの分離と loopback 束縛の不変条件テストは M6 の作業であり、
    /// それまでは安全側として localhost（127.0.0.1）束縛とする。ポート番号 8514 自体も
    /// 暫定であり、syslog の既定ポート 514 との対応（8 を前置しただけ）以上の根拠はまだない。
    /// </remarks>
    public const int DefaultHttpPort = 8514;

    public static async Task Main(string[] args)
    {
        var dataRoot = ResolveDataRoot();
        Directory.CreateDirectory(dataRoot);

        var databasePath = Path.Combine(dataRoot, "yagura.db");

        var builder = WebApplication.CreateBuilder(args);

        // 閲覧リスナは暫定で loopback 束縛のみ（CF-1 確定待ち。上記 DefaultHttpPort のコメント参照）。
        var httpPort = ResolveHttpPort();
        builder.WebHost.UseUrls($"http://127.0.0.1:{httpPort}");

        builder.Services.AddSingleton<ILogStore>(_ => new SqliteLogStore(databasePath));
        builder.Services.AddSingleton(new UdpSyslogListenerOptions
        {
            BindAddress = UdpSyslogListenerOptions.DefaultBindAddress,
            Port = ResolveUdpPort(),
        });
        builder.Services.AddSingleton(sp => new IngestionPipeline(
            sp.GetRequiredService<UdpSyslogListenerOptions>(),
            sp.GetRequiredService<ILogStore>(),
            new NoopIngressGate(),
            sp.GetRequiredService<ILoggerFactory>()));
        builder.Services.AddHostedService<IngestionHostedService>();

        builder.Services.AddYaguraWebViewer();

        var app = builder.Build();

        // MapRazorComponents の既定エンドポイントは antiforgery メタデータを持つため、
        // 対応する UseAntiforgery ミドルウェアが無いと 500 になる（書き込みフォームを
        // 持たないページでも、Razor Components のパイプラインとして必須。実機確認済み）。
        // UseRouting は MapYaguraWebViewer 内の MapRazorComponents が暗黙に endpoint
        // ルーティングを組み込むため、ここでは UseAntiforgery のみを明示する。
        app.UseAntiforgery();

        // ルート登録は Yagura.Web 側の MapYaguraWebViewer に集約する（Yagura.Web
        // /YaguraWebViewerExtensions.cs のコメント参照。書き込み系エンドポイントを
        // Host 側で個別に追加しない）。
        app.MapYaguraWebViewer();

        // architecture.md §1.2 の起動順序（受信を最初に開く）は IngestionHostedService が
        // 担う。IHostedService.StartAsync は ASP.NET Core の規約により Kestrel が listen を
        // 開始するより先に完了まで待たれる（Microsoft Learn "Background tasks with hosted
        // services in ASP.NET Core" の "IHostedService interface" > "StartAsync" 節:
        // "StartAsync is called before: The app's request processing pipeline is
        // configured. The server is started and IApplicationLifetime.ApplicationStarted
        // is triggered." と明記されている。WebApplication は内部でこの Generic Host の
        // 規約に従う。確認日 2026-07-05、learn.microsoft.com/aspnet/core/fundamentals/
        // host/hosted-services）。本 E2E テスト（tests/Yagura.E2E.Tests）の起動ログ順で
        // 実際に「UDP listener started」ログが「Now listening on:」より先に出ることも
        // 実証している。
        await app.RunAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// データルートを解決する。既定は <c>%ProgramData%\Yagura</c>（configuration.md §2）。
    /// <see cref="DataRootEnvironmentVariable"/> 環境変数で上書きできる。
    /// </summary>
    private static string ResolveDataRoot()
    {
        var overridden = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "Yagura");
    }

    /// <summary>
    /// UDP 受信ポートを解決する。<see cref="UdpPortEnvironmentVariable"/> 環境変数で
    /// 上書きできる（<c>0</c> 指定で OS 採番。E2E テスト用）。未指定時は
    /// <see cref="UdpSyslogListenerOptions.DefaultPort"/>。
    /// </summary>
    private static int ResolveUdpPort()
    {
        var overridden = Environment.GetEnvironmentVariable(UdpPortEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden) && int.TryParse(overridden, out var port))
        {
            return port;
        }

        return UdpSyslogListenerOptions.DefaultPort;
    }

    /// <summary>
    /// 閲覧 HTTP リスナのポートを解決する。<see cref="HttpPortEnvironmentVariable"/>
    /// 環境変数で上書きできる（<c>0</c> 指定で OS 採番。E2E テスト用）。未指定時は
    /// <see cref="DefaultHttpPort"/>。
    /// </summary>
    private static int ResolveHttpPort()
    {
        var overridden = Environment.GetEnvironmentVariable(HttpPortEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden) && int.TryParse(overridden, out var port))
        {
            return port;
        }

        return DefaultHttpPort;
    }
}
