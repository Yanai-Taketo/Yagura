using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yagura.Host.Configuration;
using Yagura.Ingestion;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Udp;
using Yagura.Storage;
using Yagura.Storage.Sqlite;
using Yagura.Web;

namespace Yagura.Host;

public static class Program
{
    public static async Task Main(string[] args)
    {
        // データルートは設定ファイル自体の置き場所を決める入力のため、ファイル読み込みに
        // 先立って解決する（configuration.md §2。環境変数 > 既定値 %ProgramData%\Yagura）。
        var dataRoot = YaguraConfigurationLoader.ResolveDataRoot();
        Directory.CreateDirectory(dataRoot);

        // 設定基盤（M3-1）: データルート直下の JSON 設定ファイル（既定 yagura.json）を
        // 読み込み、検証・3 分類の適用・環境変数上書きを経た最終設定を得る。設定ファイルが
        // 存在しない場合は既定値のみで起動する（ゼロ設定ファーストラン）。
        // ここで使うロガーは DI コンテナ構築前の一時的なものであり、Generic Host 標準の
        // コンソールロガーと同じ出力先（標準出力）に揃える。
        using var bootstrapLoggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
        var configurationLogger = bootstrapLoggerFactory.CreateLogger("Yagura.Host.Configuration");

        ConfigurationLoadResult configurationLoadResult;
        try
        {
            configurationLoadResult = YaguraConfigurationLoader.Load(dataRoot, configurationLogger);
        }
        catch (ConfigurationValidationException ex)
        {
            // §1「起動失敗」分類: 受信の成立に不可欠なキーが不正な場合はホスト起動を失敗させる。
            configurationLogger.LogCritical(ex, "設定ファイルの検証に失敗したため起動を中止します。");
            throw;
        }

        if (configurationLoadResult.UnknownKeys.Count > 0)
        {
            configurationLogger.LogWarning(
                "設定ファイルに未知のキーが {Count} 件見つかりました: {Keys}",
                configurationLoadResult.UnknownKeys.Count,
                string.Join(", ", configurationLoadResult.UnknownKeys));
        }

        var resolvedConfiguration = configurationLoadResult.Configuration;
        var databasePath = Path.Combine(dataRoot, resolvedConfiguration.SqliteFileName);

        var builder = WebApplication.CreateBuilder(args);

        // 閲覧リスナは暫定で loopback 束縛のみ（CF-1 確定待ち。YaguraHostEnvironment.DefaultHttpPort
        // のコメント参照）。
        builder.WebHost.UseUrls($"http://127.0.0.1:{resolvedConfiguration.HttpPort}");

        builder.Services.AddSingleton<ILogStore>(_ => new SqliteLogStore(databasePath));
        builder.Services.AddSingleton(new UdpSyslogListenerOptions
        {
            BindAddress = resolvedConfiguration.UdpBindAddress,
            Port = resolvedConfiguration.UdpPort,
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
}
