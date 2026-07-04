using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Configuration;
using Microsoft.Extensions.Logging.EventLog;
using Yagura.Host.Configuration;
using Yagura.Ingestion;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Udp;
using Yagura.Storage;
using Yagura.Storage.Sqlite;
using Yagura.Web;

namespace Yagura.Host;

/// <summary>
/// Yagura は Windows ネイティブな syslog 集約サーバであり（CLAUDE.md・ADR-0001）、本プロジェクトの
/// ターゲットフレームワークは Windows 専用の <c>net10.0-windows</c> ではなく <c>net10.0</c> のまま
/// 維持する（<c>Yagura.Host.Tests</c> / <c>Yagura.E2E.Tests</c> は <c>net10.0</c> であり、
/// ProjectReference は同一 TFM 系列でないと NU1201 で復元できない実機確認済みのため、Host 単体の
/// TFM 変更はテストプロジェクト側にも波及する——本 Issue（#31）のスコープを超える）。
/// そのため <c>Microsoft.Extensions.Logging.EventLog</c>（<c>[SupportedOSPlatform("windows")]</c>
/// 付与済み）の呼び出しは CA1416（プラットフォーム互換性）の対象になる。<see cref="Program"/>
/// 全体に <see cref="SupportedOSPlatformAttribute"/> を付与し、「このエントリポイントは Windows
/// 専用である」という設計意図をアナライザに伝えることで警告を解消する（推測抑制ではなく、
/// 製品方針そのものを表明する属性として使う）。
/// </summary>
[SupportedOSPlatform("windows")]
public static class Program
{
    /// <summary>
    /// Windows サービスとして登録する際のサービス名（暫定。architecture.md §1.1 「ホスト」の
    /// 責務としての Windows サービス統合）。インストーラ（M9）の <c>sc.exe create</c> /
    /// <c>New-Service</c> の <c>-Name</c> はこの値と一致させる。
    /// </summary>
    public const string WindowsServiceName = "Yagura";

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

        // Windows サービス統合（M3-2 #31。architecture.md §1.1 「ホスト」の責務）。
        //
        // AddWindowsService は「実行時にプロセスが実際に Windows サービスとして起動されて
        // いるかどうかをコンテキストとして検出し、その場合にのみ IHost の Lifetime を
        // WindowsServiceLifetime に差し替える」設計であることが公式 API リファレンスに
        // 明記されている（"This is context aware and will only activate if it detects the
        // process is running as a Windows Service." — learn.microsoft.com/dotnet/api/
        // microsoft.extensions.hosting.windowsservicelifetimehostbuilderextensions.
        // addwindowsservice, 確認日 2026-07-05）。コンソール実行時・デバッガ添付時は
        // 何もせず既定の ConsoleLifetime のまま動作するため、E2E テスト
        // （Process 起動によるコンソール実行）や開発時のインナーループには影響しない。
        //
        // WindowsServiceLifetime は SCM の停止要求（サービス制御マネージャが送る
        // SERVICE_CONTROL_STOP）を受けると IHostApplicationLifetime.StopApplication() を
        // 呼び、Generic Host の通常の停止シーケンス（登録済み IHostedService.StopAsync を
        // 逆順に呼ぶ）を経由する。IngestionHostedService.StopAsync（本ファイルの直下 using
        // 先。§1.3 の drain ベストエフォート停止）は SCM 経由の停止でもコンソールの
        // Ctrl+C 経由の停止でも同じ経路を通る——WindowsServiceLifetime は「停止の契機」を
        // 差し替えるだけで、IHostedService の停止順序そのものは変更しない。
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = WindowsServiceName;
        });

        // イベントログ警告経路（M3-2 #31。architecture.md §4.6 の「Windows イベントログへの
        // 警告書き出し」の受け皿）。実際の発火点（スプール退避・上限到達等）は M4 で追加する。
        //
        // ソース名は暫定でサービス名と同じ "Yagura" とする。EventLogSettings.SourceName の
        // 既定は ".NET Runtime" だが、Event Viewer 上で本製品のログと識別できるよう明示する。
        //
        // イベントソースの事前登録が必要という Windows の制約: 公式ドキュメント
        // （learn.microsoft.com/dotnet/api/system.diagnostics.eventlog.writeevent の
        // Remarks、確認日 2026-07-05）に "You must have administrative rights on the
        // computer to create a new event source." と明記されている。ソース "Yagura" の
        // 登録は本 PR の範囲外とし、M9（インストーラ）で管理者権限下の登録を行う前提とする。
        //
        // 未登録時に例外で落ちないことは .NET ランタイムの実装から確認済み（推測ではない）:
        // Microsoft.Extensions.Logging.EventLog の WindowsEventLog.WriteEntry は
        // SecurityException を catch し、(a) 以後そのプロバイダインスタンスへの書き込みを
        // 恒久的に無効化するフラグを立て、(b) 既定の "Application" ソースへ 1 回だけ
        // フォールバック書き込みを試みる（"Unable to log .NET application events. …"
        // というメッセージで）。フォールバックも失敗すれば例外を握りつぶして何もしない。
        // つまり「ソース未登録 + 管理者権限なし」でもホストプロセスは落ちず、実質的には
        // 「初回 Warning が Application ソースの下で 1 回だけ記録され、以降は記録されない」
        // という縮退になる（ソース出典: dotnet/runtime の
        // Microsoft.Extensions.Logging.EventLog/src/WindowsEventLog.cs、確認日 2026-07-05）。
        // M9 でのソース事前登録により、この縮退状態は解消される。
        //
        // コンソール実行時の二重出力について: EventLog プロバイダは Console プロバイダとは
        // 独立した ILoggerProvider であり、既定の LogLevel フィルタ（EventLog 既定は
        // Warning 以上。Console は Information 以上）が異なるため、両者は「同じログを
        // 二重に出す」のではなく「対象範囲が異なる 2 つの出力先」として共存する。
        // コンソール実行時にイベントログへも書くことを止める特別分岐は設けない
        // ——常時稼働する Windows サービスと日常の開発内側ループを同じログ配線で検証できる
        // ことを優先する（設定ファイルで EventLog プロバイダの LogLevel を個別に絞ることは
        // 利用者側で可能。Logging:EventLog:LogLevel:Default キー）。
        LoggerProviderOptions.RegisterProviderOptions<EventLogSettings, EventLogLoggerProvider>(builder.Services);
        builder.Logging.AddEventLog(settings =>
        {
            settings.SourceName = WindowsServiceName;
        });

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
