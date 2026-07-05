using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Configuration;
using Microsoft.Extensions.Logging.EventLog;
using Yagura.Host.Configuration;
using Yagura.Ingestion;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Tcp;
using Yagura.Ingestion.Udp;
using Yagura.Storage;
using Yagura.Storage.Auditing;
using Yagura.Storage.Sqlite;
using Yagura.Storage.SqlServer;
using Yagura.Storage.Spool;
using Yagura.Web;
using Yagura.Web.Diagnostics;

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

        // architecture.md §1.2 起動手順 1「スプール領域を開く（前回退避分の存在確認を含む）」。
        // 受信開始（IngestionHostedService.StartAsync）より先に行う必要があるため、DI 登録前の
        // ここで同期的に開く。開けなかった場合はスプールなし縮退運転として続行する
        // （受信は止めない。§1.2「起動失敗（= 受信断の固定化）よりも、可視化された縮退の方が
        // 『ログを失わない』原則への害が小さい」）。opt-out で無効化されている場合も
        // 縮退運転と同じ扱い（スプールなしでパイプラインを構成する）でよいが、利用者が明示的に
        // 無効化した場合は「縮退」ではなく意図した構成のため、警告・メトリクスは出さない。
        // 警告ログ自体は EventLog プロバイダ登録（後述）を経た後の DI ロガーで出す必要があるため、
        // ここでは開けたかどうかの結果のみを保持し、実際の警告は app.Build() 後に出す。
        DiskSpool? spool = null;
        Exception? spoolOpenFailure = null;
        var spoolDegraded = false;

        if (resolvedConfiguration.SpoolEnabled)
        {
            spool = DiskSpool.TryOpen(
                new DiskSpoolOptions
                {
                    Directory = resolvedConfiguration.SpoolDirectory,
                    QuotaBytes = resolvedConfiguration.SpoolQuotaBytes,
                },
                out spoolOpenFailure);

            spoolDegraded = spool is null;
        }

        var builder = WebApplication.CreateBuilder(args);

        // リスナ分離(M6-1。Issue #51。CF-1 確定値: 閲覧 8514 / 管理 8515)。
        //
        // bind 先の計算そのものは ListenerBindPlan に切り出してある(単体テストで
        // 「管理リスナの bind 先は入力に関わらず常に loopback の両系統になる」を実サーバ
        // 起動なしに検証するため——ListenerBindPlanTests 参照)。ここでは計算結果を
        // そのまま Kestrel の Listen/ListenAnyIP へ渡すだけにする。
        //
        // UseUrls ではなく ConfigureKestrel + Listen を使う理由: UseUrls は単一の URL 文字列
        // (またはカンマ区切りの複数 URL)を Kestrel の既定オプションへ変換する簡易 API であり、
        // IPv4/IPv6 の両系統を明示的に別アドレスとして bind する(本 Issue の要件)場合、
        // ConfigureKestrel 経由で IPAddress を直接指定する方が意図が明確になる。
        //
        // 全インターフェース bind は ListenAnyIP を使う(Listen(IPAddress.Any, port) と
        // Listen(IPAddress.IPv6Any, port) を両方呼ぶと AddressInUseException になる——
        // Kestrel のソケットトランスポート層は IPv6Any を bind するソケットに DualMode = true
        // を設定し、その 1 ソケットだけで IPv4 も IPv6 も受け付ける実装になっている
        // (dotnet/aspnetcore の SocketTransportOptions.CreateDefaultBoundListenSocket。
        // ListenAnyIP はこの単一ソケットの Listen(IPv6Any, port) 呼び出しをラップした
        // 公式の簡易 API——実機確認・ソース確認済み、確認日 2026-07-05)。
        var listenerBindEntries = ListenerBindPlan.Create(resolvedConfiguration);
        builder.WebHost.ConfigureKestrel(kestrelOptions =>
        {
            foreach (var entry in listenerBindEntries)
            {
                if (entry.IsAnyIP)
                {
                    kestrelOptions.ListenAnyIP(entry.Port);
                }
                else
                {
                    kestrelOptions.Listen(entry.Address!, entry.Port);
                }
            }
        });

        // ポートゲート(後述 UseYaguraListenerPortGuard)の判定に使う管理ポートの実値。
        // resolvedConfiguration.AdminHttpPort をそのまま使わない理由: 0(OS 採番。テスト用)
        // 指定時、ListenerBindPlan.Create が実際に予約した具体ポート番号はここでしか
        // 得られない(ResolvedYaguraConfiguration 自体は 0 のまま——ListenerBindPlan の
        // ResolvePortForDualStackLoopback コメント参照)。
        var effectiveAdminPort = listenerBindEntries.First(e => e.Kind == ListenerKind.Admin).Port;

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

        // provider 選択の結線（M5-3。database.md §1）。YaguraConfigurationLoader が
        // Storage:Provider = sqlserver かつ接続文字列未設定の場合を既に SQLite へ縮小済みのため
        // （ResolveStorageProvider のコメント参照）、ここでは解決済みの値をそのまま分岐すればよい。
        builder.Services.AddSingleton<ILogStore>(_ => resolvedConfiguration.StorageProvider switch
        {
            Yagura.Host.Configuration.StorageProvider.SqlServer =>
                new SqlServerLogStore(resolvedConfiguration.SqlServerConnectionString!),
            _ => new SqliteLogStore(databasePath),
        });
        builder.Services.AddSingleton(new UdpSyslogListenerOptions
        {
            BindAddress = resolvedConfiguration.UdpBindAddress,
            Port = resolvedConfiguration.UdpPort,
        });
        builder.Services.AddSingleton(new TcpSyslogListenerOptions
        {
            BindAddress = resolvedConfiguration.TcpBindAddress,
            Port = resolvedConfiguration.TcpPort,
        });

        // 保持期間削除スケジューラ（M5-1。database.md §3）。容量枯渇（§1.2 契約 3）を契機とした
        // 前倒し実行の自走復旧経路（§4・§5.3）でもあるため、ICapacityExhaustionHandler として
        // IngestionPipeline へ渡す——RetentionDays が null（既定 30 日への自動フォールバックを
        // 避ける不正値時のフォールバック。DB-1 確定に伴い既定は通常 30 が入る）でも、
        // スケジューラ自体は常に構成し、容量枯渇時の警告発火（保持期間の設定を促す）は行う。
        builder.Services.AddSingleton(sp => new Yagura.Host.Retention.RetentionScheduler(
            sp.GetRequiredService<ILogStore>(),
            new Yagura.Host.Retention.RetentionSchedulerOptions(
                resolvedConfiguration.RetentionDays,
                resolvedConfiguration.RetentionExecutionTimeOfDay),
            timeProvider: null,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<Yagura.Host.Retention.RetentionScheduler>()));

        builder.Services.AddSingleton(sp => new IngestionPipeline(
            sp.GetRequiredService<UdpSyslogListenerOptions>(),
            sp.GetRequiredService<TcpSyslogListenerOptions>(),
            sp.GetRequiredService<ILogStore>(),
            new NoopIngressGate(),
            sp.GetRequiredService<ILoggerFactory>(),
            spool,
            sp.GetRequiredService<Yagura.Host.Retention.RetentionScheduler>()));

        // メタデータ領域（architecture.md §4.3）: IngestionPipeline が構築する
        // IngestionMetrics をそのまま渡す（Meter を 2 つ持たせず、パイプラインの計測点と
        // 同じインスタンスへメタデータ領域の値を引き継ぐ・書き出す）。
        builder.Services.AddSingleton(sp => new Observability.ObservabilityCoordinator(
            dataRoot,
            sp.GetRequiredService<IngestionPipeline>().Metrics,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Yagura.Host.Observability")));

        builder.Services.AddHostedService<IngestionHostedService>();

        // 監査記録の最小基盤（security.md §4.1・§4.2。M6-2。Issue #52）。
        //
        // Yagura.Web（ListenerPortGuardMiddleware）は Yagura.Storage.Auditing.IAuditRecorder
        // インターフェースのみを参照し、実体（アプリ記録ファイル + Windows イベントログ併記）は
        // ここ Yagura.Host が結線する——architecture.md の参照構造（Web は Storage 抽象のみを
        // 参照する）と、既存の「メタデータ領域・スプールは Host 管轄のホスト管轄ローカル
        // ファイル」という判断（architecture.md §4.3）に揃える。
        //
        // WebGuardMetrics は Yagura.Ingestion.Diagnostics.IngestionMetrics とは別インスタンスだが
        // 同じ Meter 名 "Yagura" を使う（Yagura.Web は Yagura.Ingestion を参照しない設計のため、
        // インスタンス共有ではなく Meter 名の一致で単一の計測空間に統合する。
        // WebGuardMetrics のコメント参照）。
        builder.Services.AddSingleton<WebGuardMetrics>();
        builder.Services.AddSingleton<IAuditRecorder>(sp => new Yagura.Host.Observability.Auditing.FileAuditRecorder(
            dataRoot,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Yagura.Host.Observability.Auditing"),
            sp.GetRequiredService<WebGuardMetrics>()));

        builder.Services.AddYaguraWebViewer();

        var app = builder.Build();

        if (spoolDegraded)
        {
            // §1.2「縮退はイベントログへの警告（§4.6）とメトリクスで強く可視化し、黙って
            // opt-out 相当に落ちることを許さない」。app.Build() 後の DI ロガーを使うことで、
            // この警告は EventLog プロバイダ（既定 Warning 以上を書き出す。本ファイル上部の
            // AddEventLog 参照）にも到達する。メトリクス側の可視化は、spool が null のまま
            // IngestionPipeline に渡ることで、縮退中の Q2 溢れ・書込失敗が実際に発生した
            // 時点で「永続化失敗」カウンタへ計上される形で行われる（ParsingStage・
            // PersistenceWriter 側の実装参照）。
            var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Yagura.Host.Startup");
            // 先頭の [spool-degraded-mode] はロケール非依存の機械照合用トークン。日本語本文は
            // リダイレクトされた子プロセス stdout のコードページ次第で化け得るため(en-US 環境の
            // CP437 等)、E2E テストはこの ASCII トークンで照合する。恒久的なイベント ID 体系は
            // security.md §4.3 の監査記録実装時に整備する。
            startupLogger.LogWarning(
                spoolOpenFailure,
                "[spool-degraded-mode] スプール領域 {SpoolDirectory} を開けなかったため、スプールなし縮退運転で起動します。" +
                "縮退中は Q2 溢れ・書き込み失敗分が破棄され、永続化失敗カウンタへ計上されます。",
                resolvedConfiguration.SpoolDirectory);
        }

        // MapRazorComponents の既定エンドポイントは antiforgery メタデータを持つため、
        // 対応する UseAntiforgery ミドルウェアが無いと 500 になる（書き込みフォームを
        // 持たないページでも、Razor Components のパイプラインとして必須。実機確認済み）。
        // UseRouting は MapYaguraWebViewer 内の MapRazorComponents が暗黙に endpoint
        // ルーティングを組み込むため、ここでは UseAntiforgery のみを明示する。
        app.UseAntiforgery();

        // リスナ分離の実行時強制(M6-1。Issue #51)。UseRouting(MapRazorComponents が暗黙に
        // 組み込む)によりエンドポイントが確定した後、実処理(Razor Components の描画・
        // SignalR ハブへのアップグレード等)が始まる前に、管理系エンドポイントへの到達を
        // 接続の実ローカルポートで判定する(ListenerPortGuardMiddleware のコメント参照——
        // RequireHost は HTTP Host ヘッダに依存し偽装可能なため採らなかった)。
        // ミドルウェアの登録順序上、UseAntiforgery の後・MapYaguraWebViewer/MapYaguraAdmin
        // (エンドポイント実行に相当)の前に置く必要がある。effectiveAdminPort を使う理由は
        // 上記(122 行目付近)のコメント参照——0 指定時は解決済みの具体ポートで判定する必要がある。
        app.UseYaguraListenerPortGuard(effectiveAdminPort);

        // ルート登録は Yagura.Web 側の 2 つの集約点に分ける:
        // - MapYaguraWebViewer(閲覧系。書き込みエンドポイントを持たない)
        // - MapYaguraAdmin(管理系。ListenerPortGuardEndpointMetadata.Admin を持ち、
        //   上記ガードにより管理リスナ以外からは 404 になる)
        // 閲覧系ルートは管理リスナからも到達できる設計とした(ui.md §4 の線引きは「閲覧
        // リスナに書き込み系を置かない」であり、管理リスナは loopback 限定のため閲覧系が
        // 同居しても安全側に働く——管理者がローカルで全部見られることはむしろ自然。
        // MapYaguraWebViewer はガード対象のメタデータを持たないため、両リスナから到達できる)。
        // Host 側で個別に MapGet 等を追加しない(各拡張メソッドのコメント参照)。
        app.MapYaguraWebViewer();
        app.MapYaguraAdmin();

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
        try
        {
            await app.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            // DiskSpool はここ（Program）が所有者として開いたため、ここで解放する
            // （IngestionPipeline は借用しているだけで dispose しない）。
            spool?.Dispose();
        }
    }
}
