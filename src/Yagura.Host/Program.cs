using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Configuration;
using Microsoft.Extensions.Logging.EventLog;
using Yagura.Host.Configuration;
using Yagura.Host.Observability.ActiveNotification.Email;
using Yagura.Ingestion;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Tcp;
using Yagura.Ingestion.Tls;
using Yagura.Ingestion.Udp;
using Yagura.Storage;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Storage.Sqlite;
using Yagura.Storage.SqlServer;
using Yagura.Storage.Spool;
using Yagura.Web;
using Yagura.Web.Administration;
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

    /// <summary>
    /// Yagura サービスの<b>既定の</b>仮想サービスアカウント名（ADR-0004 決定 4。
    /// <c>NT SERVICE\&lt;ServiceName&gt;</c> は Windows がサービス登録時に自動で認識する予約済み
    /// アカウント）。gMSA opt-in（ADR-0015。configuration.md §4.4）では実行アカウントが
    /// <c>DOMAIN\name$</c> に変わるため、秘密鍵権限の付与先等の実行時の付与・照合は本定数ではなく
    /// 実効実行アカウント（<see cref="Yagura.Host.Configuration.ServiceAccountStartupInspector.ResolveEffectiveAccountName"/>）
    /// から導出する（security.md §5.2）。本定数はインストーラ既定値との対応（MSI プロパティ
    /// <c>YAGURA_SERVICE_ACCOUNT</c> の既定）を表す。
    /// </summary>
    public const string YaguraServiceAccountName = $"NT SERVICE\\{WindowsServiceName}";

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
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            // configuration.md §1「読み取り・解析の失敗」（構文エラー・文字化け・重複キー）。
            // キー単位の縮退に分解できず「何が既定へ落ちたか」を提示できないため、
            // 「可視化された縮退」（architecture.md §1.2）の系に載せられない——よって起動を止める。
            LogConfigurationFileUnreadable(configurationLogger, dataRoot, ex);
            throw;
        }
        catch (ConfigurationValidationException ex)
        {
            // §1「起動失敗」分類: 受信の成立に不可欠なキーが不正な場合はホスト起動を失敗させる。
            // ADR-0010 決定 6: fail-closed 拒否（loopback 認証 opt-in の誤設定等）は専用の
            // イベント ID（1000 番台。ex.EventId）を伴う——「なぜ起動しないか・何を直せばよいか」
            // が一目で分かる警告を求める要件（委任事項 5）を、専用 ID + 詳細メッセージ
            // （例外メッセージ自体に誘導文言を含む）の組み合わせで満たす。個別 ID を持たない
            // 従来の起動失敗（受信ポート不正等）は既定の EventId（0）のまま記録する。
            configurationLogger.LogCritical(ex.EventId ?? default, ex, "設定ファイルの検証に失敗したため起動を中止します。");
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

        // 設定ライブ再読み込み（CF-4 層1。Issue #262）の差分計算の初期基準——起動時点の
        // ファイルの生 options を捕捉しておく（ConfigurationReloadService の doc コメント参照）。
        // 受理範囲は Load と一致しているため（§1 の不変条件）ここで新たに失敗することは想定しないが、
        // 念のため同じ扱い（1024 + 起動失敗）へ寄せる——片方だけが落ちる状態を再び作らない。
        YaguraConfigurationOptions startupRawOptions;
        try
        {
            startupRawOptions = YaguraConfigurationWriter.Read(dataRoot).Options;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            LogConfigurationFileUnreadable(configurationLogger, dataRoot, ex);
            throw;
        }

        // ここまでで読み取りと検証を通ったので、良好構成の写しを更新する（§1）。
        // 復旧元として使うだけで自動適用はしない（写しの内容は現在の意図より開放側でありうる）。
        LastKnownGoodConfiguration.Save(
            dataRoot,
            onFailure: failure => configurationLogger.LogWarning(
                failure,
                "良好構成の写しを保存できませんでした（起動は継続します）。設定ファイルが読めなくなった際の復旧元が古いままになります。"));

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

        // 定期自己検証（architecture.md §3.2.5。Issue #152）の投入・照合の橋渡し。投入
        // （ActiveNotificationMonitor。下記）と照合（SpoolDrainCoordinator。IngestionPipeline
        // 経由）が同一インスタンスを共有することで、「drain の実機構に読ませて照合する」を
        // 実現する。スプールが無い（opt-out・縮退運転のいずれか）場合は投入対象自体が無いため
        // 生成しない（null のまま——両受け取り側とも null を「自己検証を行わない」として扱う）。
        var selfTestTracker = spool is not null
            ? new Yagura.Storage.Spool.SpoolSelfTestTracker()
            : null;

        var builder = WebApplication.CreateBuilder(args);

        // 静的アセット配信面の最小化(M8-1。Issue #68)。.NET 10 の MapStaticAssets は、
        // build 出力のマニフェスト使用時に限り「マニフェスト外のファイルも web root から
        // 配信する」開発時フォールバック(catch-all ルート {**path:file}。GET/HEAD 限定・
        // 実在ファイル制約付き)を追加登録する。publish 出力のマニフェストでは登録されない
        // (dotnet/aspnetcore release/10.0 の StaticAssetDevelopmentRuntimeHandler.cs で
        // 実体確認済み: IsEnabled は isBuildManifest を条件とし、本キー
        // DisableStaticAssetNotFoundRuntimeFallback=true で無効化できる。確認日 2026-07-06)。
        // 閲覧リスナの配信面は「マニフェスト記載のアセットのみ」に固定する——開発実行・E2E・
        // publish のすべてで L-5 許可リスト(security.md §1)と同じ面になり、開発時だけ広い
        // 配信面が現れることを避ける(Yagura.Host は自前の wwwroot を持たず、フォールバックが
        // 有用な場面もない)。
        builder.Configuration["DisableStaticAssetNotFoundRuntimeFallback"] = "true";

        // 静的 Web アセットの解決を実行環境名に依存させない(M8-1)。既定では
        // ASPNETCORE_ENVIRONMENT=Development のときだけ runtime マニフェスト
        // ({AppName}.staticwebassets.runtime.json。NuGet キャッシュ上の MudBlazor 実ファイル
        // への対応表)が読み込まれるため、build 出力を Production(既定)のまま実行すると
        // _content/MudBlazor/* が中身 0 バイトで応答する事象を実機確認した(2026-07-06)。
        // ここで無条件にマニフェストを読み込むことで、build 出力からの実行(E2E テスト・
        // 開発時)でもアセットが解決される。publish 出力には runtime マニフェストが含まれず
        // 実ファイルが wwwroot/ 配下へ物理配置されるため、この呼び出しは無害な no-op になる
        // (dotnet/aspnetcore release/10.0 の StaticWebAssetsLoader.ResolveManifest が
        // マニフェスト不在時に "the feature is not enabled" として何もしないことをソースで
        // 確認済み。確認日 2026-07-06)。
        // 注: builder.WebHost.UseStaticWebAssets() 拡張ではなくローダーを直接呼ぶ。
        // 拡張経由(ConfigureAppConfiguration コールバック内)では最終的な
        // WebRootFileProvider へ反映されないことを実機確認した(builder.Environment に
        // 対して直接適用するのが minimal hosting での確実な経路。同日実機確認)。
        Microsoft.AspNetCore.Hosting.StaticWebAssets.StaticWebAssetsLoader.UseStaticWebAssets(
            builder.Environment,
            builder.Configuration);

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
        // 管理リスナのリモート HTTPS（ADR-0010 Phase 2 決定 4）: bind エントリを Kestrel へ渡す前に
        // 証明書ストア参照を試みる。YaguraConfigurationLoader.Load は「認証 + HTTPS が静的に構成
        // 済みであること」までを fail-closed で検証済みだが、拇印が実際に証明書ストアで解決できる
        // か（証明書が存在するか・秘密鍵が読めるか・期限内か）は環境依存のため、ここでの解決結果に
        // 応じて「開けなかった bind エントリを縮小継続としてスキップする」（configuration.md §4.1
        // 「指定した bind 先が使用できない場合...そのリスナは開かずに縮小側で継続する」と同型の扱い。
        // ADR-0010 Phase 2 決定 4「loopback 経由の管理リスナは HTTPS の対象外のまま残る」——
        // リモート面 1 本が開けなくても管理リスナ全体・loopback 面は影響を受けない）。
        System.Security.Cryptography.X509Certificates.X509Certificate2? adminHttpsCertificate = null;
        string? adminHttpsCertificateUnavailableReason = null;

        var listenerBindEntries = ListenerBindPlan.Create(resolvedConfiguration);

        if (listenerBindEntries.Any(e => e.RequiresHttps))
        {
            var certificateLoadResult = Yagura.Host.Administration.Https.CertificateProvider.Load(
                resolvedConfiguration.AdminHttpsCertificateThumbprint!);

            if (!certificateLoadResult.Succeeded)
            {
                adminHttpsCertificateUnavailableReason = certificateLoadResult.FailureReason;
            }
            else if (certificateLoadResult.IsExpired)
            {
                adminHttpsCertificateUnavailableReason =
                    $"証明書（拇印 {resolvedConfiguration.AdminHttpsCertificateThumbprint}）の有効期間外です" +
                    $"（NotBefore={certificateLoadResult.Certificate!.NotBefore:O}, NotAfter={certificateLoadResult.Certificate.NotAfter:O}）。";
            }
            else
            {
                adminHttpsCertificate = certificateLoadResult.Certificate;
            }
        }

        // TLS 受信（RFC 5425。opt-in・既定無効。security.md §6。Issue #137）: 証明書ストア参照は
        // 管理リスナのリモート HTTPS（上記）と同一の実装（CertificateProvider.Load）を再利用
        // する——設定キーは独立（Ingestion:Tls:*）だが、参照方式（Windows 証明書ストア・拇印指定）
        // は共有し、重複実装しない（security.md §6「参照方式は Web UI の HTTPS と同型」）。
        // <b>管理 HTTPS との非対称</b>: 管理 HTTPS は IsExpired を「未解決」として扱い bind を
        // スキップするが、TLS 受信は「止めない」設計のため、起動時点で既に期限切れの証明書でも
        // そのまま受け入れる（IsExpired は判定に使わない）——縮小継続の対象になるのは、拇印が
        // 未設定・不正形式、または証明書そのものが解決できない（見つからない・秘密鍵が無い）
        // 場合のみである。
        System.Security.Cryptography.X509Certificates.X509Certificate2? ingestionTlsCertificate = null;
        string? ingestionTlsCertificateUnavailableReason = null;

        if (resolvedConfiguration.IngestionTlsEnabled)
        {
            if (resolvedConfiguration.IngestionTlsCertificateThumbprint is null)
            {
                ingestionTlsCertificateUnavailableReason =
                    "TLS 受信証明書拇印（Ingestion:Tls:CertificateThumbprint）が未設定、または" +
                    "SHA-1 拇印（16 進 40 桁）として解釈できない形式です。";
            }
            else
            {
                var ingestionTlsCertificateLoadResult = Yagura.Host.Administration.Https.CertificateProvider.Load(
                    resolvedConfiguration.IngestionTlsCertificateThumbprint);

                if (!ingestionTlsCertificateLoadResult.Succeeded)
                {
                    ingestionTlsCertificateUnavailableReason = ingestionTlsCertificateLoadResult.FailureReason;
                }
                else
                {
                    ingestionTlsCertificate = ingestionTlsCertificateLoadResult.Certificate;
                }
            }
        }

        builder.WebHost.ConfigureKestrel(kestrelOptions =>
        {
            foreach (var entry in listenerBindEntries)
            {
                if (entry.RequiresHttps && adminHttpsCertificate is null)
                {
                    // 証明書が解決できなかった（未検出・秘密鍵アクセス不可・期限切れ）——
                    // このエントリだけを bind せず縮小継続する（警告は app.Build() 後、下記参照）。
                    continue;
                }

                void ConfigureHttpsIfRequired(Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions listenOptions)
                {
                    if (!entry.RequiresHttps)
                    {
                        return;
                    }

                    listenOptions.UseHttps(httpsOptions =>
                    {
                        // 最低 TLS 1.2・1.3 優先を明示固定する（ADR-0010 Phase 2 決定 4。田中の指摘）。
                        // OS 既定（schannel ポリシー）に暗黙に委ねない——Windows Server の版が混在する
                        // 導入先で TLS 1.0/1.1 が意図せず有効のまま露出することを防ぐ。
                        httpsOptions.SslProtocols =
                            System.Security.Authentication.SslProtocols.Tls12 |
                            System.Security.Authentication.SslProtocols.Tls13;

                        // ServerCertificateSelector は TLS ハンドシェイクのたびに呼ばれる公式の
                        // 拡張点（証明書のホットスワップ用途で用意されている）。ここでは「起動後に
                        // 証明書が期限切れへ遷移した場合、以後の新規ハンドシェイクを拒否する
                        // （= リモート HTTPS リスナを事実上停止する。configuration.md §6 の既存方針
                        // 『HTTPS リスナは停止し HTTP へは落とさない』をリモート面に適用——決定 4）」
                        // という runtime の挙動を、Kestrel のリスナを再構成することなく実現する
                        // ために転用する。null を返すと当該ハンドシェイクは失敗する（TLS レベルで
                        // 拒否——loopback 面には一切影響しない。管理者は RDP + loopback から
                        // 引き続き復旧操作ができる）。
                        var capturedCertificate = adminHttpsCertificate!;
                        httpsOptions.ServerCertificateSelector = (_, _) =>
                        {
                            var now = DateTime.Now;
                            return now >= capturedCertificate.NotBefore && now <= capturedCertificate.NotAfter
                                ? capturedCertificate
                                : null;
                        };
                    });
                }

                if (entry.IsAnyIP)
                {
                    kestrelOptions.ListenAnyIP(entry.Port, ConfigureHttpsIfRequired);
                }
                else
                {
                    kestrelOptions.Listen(entry.Address!, entry.Port, ConfigureHttpsIfRequired);
                }
            }
        });

        // ポートゲート(後述 UseYaguraListenerPortGuard)の判定に使う管理ポートの実値一式
        // (ADR-0010 Phase 2 決定 1。loopback 用に加え、証明書が解決できてリモート HTTPS bind を
        // 実際に行った場合はそのポートも含める)。resolvedConfiguration.AdminHttpPort をそのまま
        // 使わない理由: 0(OS 採番。テスト用)指定時、ListenerBindPlan.Create が実際に予約した
        // 具体ポート番号はここでしか得られない(ResolvedYaguraConfiguration 自体は 0 のまま——
        // ListenerBindPlan の ResolvePortForDualStackLoopback コメント参照)。
        var effectiveAdminPort = listenerBindEntries.First(e => e is { Kind: ListenerKind.Admin, RequiresHttps: false }).Port;
        // リモート HTTPS の実ポートも同様に listenerBindEntries から読む(resolvedConfiguration.AdminHttpsPort
        // ではない——OS 採番(0)指定時、ListenerBindPlan.Create が実際に予約した具体ポート番号は
        // ここでしか得られない。ListenerBindPlan.ResolvePortForAnyIP のコメント参照。
        // resolvedConfiguration.AdminHttpsPort をそのまま使うと、テスト用の 0 指定時にポート
        // ガードが実ポートを認識できず、リモート HTTPS 経由の到達が全て 404 になる実バグを踏む)。
        var effectiveAdminPorts = adminHttpsCertificate is not null
            ? new[] { effectiveAdminPort, listenerBindEntries.First(e => e.RequiresHttps).Port }
            : [effectiveAdminPort];

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

        // CF-5(2026-07-16 オーナー裁定。Issue #262): Windows サービスとして動いている場合のみ、
        // 既定の WindowsServiceLifetime を SCM カスタム制御コード対応版へ置き換える
        // (sc control Yagura 128 = 設定の再読み込み。YaguraWindowsServiceLifetime 参照)。
        // AddWindowsService 自体が IsWindowsService 判定で登録するため、置き換えも同じ条件で行う。
        if (OperatingSystem.IsWindows() && Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
        {
            builder.Services.AddSingleton<IHostLifetime, YaguraWindowsServiceLifetime>();
        }

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

        // 能動通知の第 2 の書き出し先（メール。ADR-0017 決定 7。opt-in・既定無効）。
        // EventLog と同じ ILogger レールに乗せることで、発火点（ActiveNotificationMonitor・
        // Yagura.Ingestion の PersistenceWriter・起動時経路・AdminAuthFailureDefense）の
        // コードを一切触らずに捕捉できる。対象の選別は EmailNotificationAllowlist のみで行う。
        //
        // 決定 7 の「利用者の Logging:* 設定がメールを止めない」は、AddEmailNotificationSink が
        // 積む本プロバイダ名指しのフィルタ規則（Trace）で成立させる——登録経路の選び方では
        // フィルタを迂回できない（PR #366 レビューで判明。詳細は同メソッドの remarks と
        // EmailNotificationLoggingTests）。
        //
        // キューは合成ルートが所有し、プロバイダ（投入側）とディスパッチャ（消化側）で共有する。
        // 通知が捕捉されるのはこの登録より後に発火したものに限られる——決定 7 が挙げる
        // 起動時経路の ID（1001・1022 等）は、いずれも app.Build() 後に DI ロガー経由で発火する
        // ため、構造上必ず本登録（builder 段階）の後になる（発火点: 1001 = 本メソッド末尾の
        // startupLogger、1022 = IngestionHostedService.StartAsync）。
        // ライブ計器（ADR-0017 決定 5。Issue #386）: 破棄数・送信失敗を外部監視から観測できる形で
        // 計上する（単一 Meter「Yagura」へ統合。カード表示用のプロセス内カウンタとは役割が別）。
        var emailNotificationMetrics = new EmailNotificationMetrics();
        var emailNotificationQueue = new EmailNotificationQueue(timeProvider: null, emailNotificationMetrics);
        // 無効構成の間は投入自体を受け付けない（Issue #384——無効期間中の蓄積が有効化時に
        // 流量制御を経ず一斉送信されるのを防ぐ。ディスパッチャの UpdateConfiguration が以後の
        // 変更を追従する）。
        emailNotificationQueue.SetEnabled(resolvedConfiguration.EmailNotification is not null);
        builder.Logging.AddEmailNotificationSink(emailNotificationQueue);
        builder.Services.AddSingleton(emailNotificationQueue);

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
            BindAddressIsExplicit = resolvedConfiguration.UdpBindAddressIsExplicit,
            Port = resolvedConfiguration.UdpPort,
            ReceiveBufferBytes = resolvedConfiguration.UdpReceiveBufferBytes,
        });
        builder.Services.AddSingleton(new TcpSyslogListenerOptions
        {
            BindAddress = resolvedConfiguration.TcpBindAddress,
            BindAddressIsExplicit = resolvedConfiguration.TcpBindAddressIsExplicit,
            Port = resolvedConfiguration.TcpPort,
        });

        // TLS 受信（Issue #137）: 証明書が解決できた場合のみ構成する（null の間は
        // IngestionPipeline へ tlsListenerOptions = null を渡し、TLS 受信自体を構成しない——
        // DI 経由ではなくローカル変数の閉包で IngestionPipeline のファクトリへ渡す。adminHttpsCertificate
        // の扱い（上記 ConfigureKestrel 内 capturedCertificate）と同じパターン）。
        TlsSyslogListenerOptions? tlsListenerOptions = ingestionTlsCertificate is not null
            ? new TlsSyslogListenerOptions
            {
                BindAddress = resolvedConfiguration.IngestionTlsBindAddress,
                BindAddressIsExplicit = resolvedConfiguration.IngestionTlsBindAddressIsExplicit,
                Port = resolvedConfiguration.IngestionTlsPort,
            }
            : null;
        Func<System.Security.Cryptography.X509Certificates.X509Certificate2?>? tlsCertificateSelector =
            ingestionTlsCertificate is not null
                ? () => ingestionTlsCertificate
                : null;

        // ILogStore の書き込みゲート（Issue #151。LogStoreWriteGate の doc コメント参照）:
        // ライブ書き込み（PersistenceWriter）・スプール drain（SpoolDrainCoordinator）・
        // 保持期間削除（RetentionScheduler）の 3 経路を単一のゲートで直列化し、
        // 「書き込みは単一 writer が呼び出す」契約（ILogStore）を実配線で満たす。
        // 単一インスタンスをここで構築し、3 経路すべてへ同じものを渡す。
        builder.Services.AddSingleton<LogStoreWriteGate>();

        // 保持期間削除スケジューラ（M5-1。database.md §3）。容量枯渇（§1.2 契約 3）を契機とした
        // 前倒し実行の自走復旧経路（§4・§5.3）でもあるため、ICapacityExhaustionHandler として
        // IngestionPipeline へ渡す——RetentionDays が null（既定 30 日への自動フォールバックを
        // 避ける不正値時のフォールバック。DB-1 確定に伴い既定は通常 30 が入る）でも、
        // スケジューラ自体は常に構成し、容量枯渇時の警告発火（保持期間の設定を促す）は行う。
        // 起動時キャッチアップ（Issue #150）もこのスケジューラの Start() が担う。
        builder.Services.AddSingleton(sp => new Yagura.Host.Retention.RetentionScheduler(
            sp.GetRequiredService<ILogStore>(),
            new Yagura.Host.Retention.RetentionSchedulerOptions(
                resolvedConfiguration.RetentionDays,
                resolvedConfiguration.RetentionExecutionTimeOfDay),
            timeProvider: null,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<Yagura.Host.Retention.RetentionScheduler>(),
            sp.GetRequiredService<LogStoreWriteGate>()));

        // 流量制御（architecture.md §3.3。ADR-0002 決定 2「送信元単位の流量制御（既定有効）」。
        // Issue #260）: 既定は TokenBucketIngressGate、opt-out（Ingestion:FlowControl:Enabled =
        // false）時のみ NoopIngressGate を結線する。破棄の計上は各リスナ（挿入点の呼び出し元）が
        // 行う——「発火は必ず計測される」§3.3。SwappableIngressGate で 1 段包むのは設定ライブ
        // 再読み込み（CF-4 層1。Issue #262）で実装を無瞬断に差し替えるため。
        var ingressGate = new SwappableIngressGate(
            CreateIngressGate(resolvedConfiguration));

        static IIngressGate CreateIngressGate(ResolvedYaguraConfiguration configuration) =>
            configuration.FlowControlEnabled
                ? new TokenBucketIngressGate(
                    configuration.FlowControlMessagesPerSecond,
                    configuration.FlowControlBurstSize)
                : new NoopIngressGate();

        // 送信元の途絶検知（ADR-0018。opt-in・既定無効。Issue #351）。追跡器と判定器は
        // 合成ルートが所有し、受信段（ParsingStage）・監視ループ・設定の即時反映の 3 者で共有する。
        // 機能が無効（ウォッチリスト未設定）でも構築はしておく——設定の即時反映で有効化された
        // 時点から、サービスを再起動せずに追跡が始まる（決定 6）。
        var sourceActivityTracker = new Yagura.Host.Observability.ActiveNotification.SourceSilence.SourceActivityTracker();
        var sourceSilenceDetector = new Yagura.Host.Observability.ActiveNotification.SourceSilence.SourceSilenceDetector(
            sourceActivityTracker);
        sourceActivityTracker.ApplyWatchlist(resolvedConfiguration.SourceSilence?.Watchlist);
        sourceSilenceDetector.ApplyWatchlist(resolvedConfiguration.SourceSilence?.Watchlist);

        // ウォッチリスト反映の口を 1 本に束ねる（決定 6）——設定の再読み込み
        // （ImmediateConfigurationApplier）と管理画面の保存（SourceSilenceAdminService）が
        // 同じ実体を使い、経路によって反映挙動が食い違う余地を作らない。
        Action<IReadOnlyList<Yagura.Host.Configuration.SourceSilenceWatchEntry>?> applySourceSilenceWatchlist =
            watchlist =>
            {
                sourceActivityTracker.ApplyWatchlist(watchlist);
                sourceSilenceDetector.ApplyWatchlist(watchlist);
            };

        builder.Services.AddSingleton(sp => new IngestionPipeline(
            sp.GetRequiredService<UdpSyslogListenerOptions>(),
            sp.GetRequiredService<TcpSyslogListenerOptions>(),
            sp.GetRequiredService<ILogStore>(),
            ingressGate,
            sp.GetRequiredService<ILoggerFactory>(),
            spool,
            sp.GetRequiredService<Yagura.Host.Retention.RetentionScheduler>(),
            resolvedConfiguration.DefaultRfc3164TimeZone,
            sp.GetRequiredService<LogStoreWriteGate>(),
            selfTestTracker,
            tlsListenerOptions,
            tlsCertificateSelector,
            sourceActivityTracker));

        // 能動通知の周期監視（architecture.md §4.6。Issue #149）: スプール使用率・退避継続・
        // 監視対象ボリュームの空き容量・SQL Server Express の DB 容量接近を定期評価する。
        // Express 判定は provider 非依存の ILogStore 契約を汚さないよう、SqlServerLogStore への
        // 型検査を Host（合成ルート）側の LogStoreExpressCapacityChecker に閉じ込める
        // （ExpressCapacityChecker.cs の remarks 参照）。
        //
        // 空き容量の監視対象はデータルートに加えて、スプール有効時はスプール置き場所も含める
        // （PR #188 レビュー指摘への対応）: Spool:Directory は設定で独立に変更でき
        // （configuration.md §8「スプール」区分）、データルートと別ドライブに向いた構成では
        // 「夜間にスプールが満ちていく」現場のボリュームが監視から外れてしまうため。
        // 既定構成（スプールはデータルート配下）では両パスは同一ボリュームであり、
        // MonitoredVolumeInfo が読み取り時に 1 件へ重複排除する（警告の二重発火はしない）。
        // スプール無効（opt-out）時は、監視すべきスプール成長自体が存在しないためデータルートのみ。
        // スプールなし縮退運転（spool が null）でも SpoolEnabled = true ならスプール置き場所を
        // 監視対象に含める——縮退の原因がそのボリュームの障害・満杯である可能性があり、
        // 空き容量の観測はむしろ復旧判断の入力になるため。
        string[] monitoredVolumePaths = resolvedConfiguration.SpoolEnabled
            ? [dataRoot, resolvedConfiguration.SpoolDirectory]
            : [dataRoot];
        builder.Services.AddSingleton<Yagura.Host.Observability.ActiveNotification.IMonitoredVolumeInfo>(
            _ => new Yagura.Host.Observability.ActiveNotification.MonitoredVolumeInfo(monitoredVolumePaths));
        builder.Services.AddSingleton<Yagura.Host.Observability.ActiveNotification.IExpressCapacityChecker>(
            sp => new Yagura.Host.Observability.ActiveNotification.LogStoreExpressCapacityChecker(
                sp.GetRequiredService<ILogStore>()));
        // 管理リスナのリモート HTTPS 証明書の周期監視プローブ（ADR-0010 Phase 2 決定 4。
        // 期限接近の事前警告 = 1014・稼働中の使用不能検知 = 1015）。リモート HTTPS bind が実際に
        // 有効な場合（= 起動時に証明書を解決できた場合）にのみ結線する——起動時に解決できず
        // 縮小継続した構成は 1013 が既に報告済みで、再起動なしに bind が有効化されることもない
        // ため周期監視の対象にしない（EvaluateAdminHttpsCertificate の doc コメント参照）。
        Yagura.Host.Observability.ActiveNotification.ICertificateStatusProbe? adminHttpsCertificateProbe =
            adminHttpsCertificate is not null
                ? new Yagura.Host.Administration.Https.StoreAdminHttpsCertificateStatusProbe(
                    resolvedConfiguration.AdminHttpsCertificateThumbprint!)
                : null;

        // TLS 受信証明書の周期監視プローブ（security.md §6。期限接近の事前警告 = 1017・稼働中の
        // 使用不能検知 = 1018）。証明書が実際に解決できて TLS 受信が有効な場合にのみ結線する——
        // 上記 adminHttpsCertificateProbe と同じ「起動時に解決できず縮小継続した構成は重複監視
        // しない」判断（EvaluateIngestionTlsCertificate の doc コメント参照）。
        Yagura.Host.Observability.ActiveNotification.ICertificateStatusProbe? ingestionTlsCertificateProbe =
            ingestionTlsCertificate is not null
                ? new Yagura.Host.Ingestion.Tls.StoreIngestionTlsCertificateStatusProbe(
                    resolvedConfiguration.IngestionTlsCertificateThumbprint!)
                : null;

        // フォワーダ MSI 配置フォルダ（ADR-0008 設計条件 9 / ADR-0020 配置経路 (b)）の実パス。
        // 検出側（IForwarderMsiSource）・書き込み側（IForwarderMsiStore）・ACL 周期検出
        // （ActiveNotificationMonitor）の三者で共有する。
        var forwarderMsiFolderPath = Path.Combine(
            dataRoot, Yagura.Web.ForwarderKit.ForwarderMsiConstraints.PlacementSubPath);

        builder.Services.AddSingleton(sp =>
        {
            var pipelineForMonitor = sp.GetRequiredService<IngestionPipeline>();
            return new Yagura.Host.Observability.ActiveNotification.ActiveNotificationMonitor(
                spool,
                pipelineForMonitor.Metrics,
                sp.GetRequiredService<Yagura.Host.Observability.ActiveNotification.IMonitoredVolumeInfo>(),
                sp.GetRequiredService<Yagura.Host.Observability.ActiveNotification.IExpressCapacityChecker>(),
                timeProvider: null,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<Yagura.Host.Observability.ActiveNotification.ActiveNotificationMonitor>(),
                selfTestTracker,
                adminHttpsCertificateProbe,
                ingestionTlsCertificateProbe,
                // ADR-0011 決定 6: 三層防御の能動通知への昇格。AdminAuthFailureDefense は
                // 本メソッド内で後段に登録されるが、AddSingleton のファクトリは遅延解決されるため
                // 登録順は問題にならない（Build() 完了後の初回解決時には両方とも登録済み）。
                sp.GetService<Yagura.Host.Administration.AdminAuthentication.AdminAuthFailureDefense>(),
                sourceSilenceDetector,
                // 受信断保留の判定源（ADR-0018 委任 6）: 全リスナ受信不能の間は途絶判定を保留し、
                // 回復で保留中に閾値超過となったエントリを再アームする。判定器がスレッド安全で
                // ないため、ListenerBindRecovered の購読ではなく監視ループ内のポーリングで観測する
                // （EvaluateSourceSilence 参照）。
                listenerAvailabilityProbe: () => pipelineForMonitor.ListenerAvailability,
                // 起動時 seed（ADR-0018 決定 3。Issue #381）: 監視開始時に最終受信時刻を 1 回だけ
                // DB から取り込み、登録済みエントリの基準を「起動時刻」から「実際の最終受信」へ
                // 置き換える（照会失敗時は起動時刻仮基準のまま）。
                sourceActivitySeedQuery: ct => sp.GetRequiredService<ILogStore>().QuerySourceActivityAsync(
                    Yagura.Host.Observability.ActiveNotification.SourceSilence.SourceSilenceConstants.MaxWatchlistEntries,
                    Yagura.Host.Observability.ActiveNotification.SourceSilence.SourceSilenceConstants.SeedQueryTimeout,
                    ct),
                // ADR-0020 決定 2・委任 7: 配置フォルダの書き込み ACE の稼働中検出（二系統——
                // 乖離警告 1033 / 開放継続通知 1034）。判定は ACL の読み取りのみ（実書き込み
                // プローブを毎分打つと SACL 監査を汚染するため——ForwarderMsiFolderAclInspector 参照）。
                // 非 Windows は null（判定不能 = 沈黙側）。
                forwarderMsiFolderWritableProbe: () => OperatingSystem.IsWindows()
                    ? Yagura.Host.Administration.ForwarderKitUpload.ForwarderMsiFolderAclInspector
                        .IsWritableByCurrentIdentity(forwarderMsiFolderPath)
                    : null,
                forwarderMsiUploadEnabled: resolvedConfiguration.AdminForwarderMsiUploadEnabled);
        });

        // メール通知の送信ループ（ADR-0017 決定 5）。プロバイダ（投入側）と同じキューを共有する。
        // ActiveNotificationMonitor と同じく IHostedService にはしない——停止順序を Generic Host の
        // 逆順登録で表現できないため、親（IngestionHostedService）が Start/StopAsync を明示的に呼ぶ。
        builder.Services.AddSingleton(sp => new EmailNotificationDispatcher(
            emailNotificationQueue,
            new MailKitEmailSender(),
            resolvedConfiguration.EmailNotification,
            timeProvider: null,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<EmailNotificationDispatcher>(),
            emailNotificationMetrics));

        // メタデータ領域（architecture.md §4.3）: IngestionPipeline が構築する
        // IngestionMetrics をそのまま渡す（Meter を 2 つ持たせず、パイプラインの計測点と
        // 同じインスタンスへメタデータ領域の値を引き継ぐ・書き出す）。
        builder.Services.AddSingleton(sp => new Observability.ObservabilityCoordinator(
            dataRoot,
            sp.GetRequiredService<IngestionPipeline>().Metrics,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Yagura.Host.Observability")));

        builder.Services.AddHostedService<IngestionHostedService>();

        // 閲覧画面向けの読み取り専用の観測値公開（M8-3。Issue #70）。契約は Yagura.Abstractions
        // （IYaguraSystemStatusReader——書き込み系マーカー IYaguraWriteService を実装しない
        // 読み取り専用契約）、実装はホスト管轄（IngestionMetrics・DiskSpool・設定適用値の
        // 結線はここでしかできない——architecture.md §1.1 の参照構造）。
        builder.Services.AddSingleton<Yagura.Abstractions.Observability.IYaguraSystemStatusReader>(sp =>
            new Observability.SystemStatusReader(
                sp.GetRequiredService<IngestionPipeline>().Metrics,
                spool,
                spoolQuotaBytes: resolvedConfiguration.SpoolQuotaBytes,
                spoolDegraded: spoolDegraded,
                retentionDays: resolvedConfiguration.RetentionDays,
                listeners:
                [
                    new Yagura.Abstractions.Observability.YaguraListenerEndpoint("UDP", resolvedConfiguration.UdpPort),
                    new Yagura.Abstractions.Observability.YaguraListenerEndpoint("TCP", resolvedConfiguration.TcpPort),
                ],
                // 流量制限の発火上位送信元（Issue #288）: SwappableIngressGate を渡す——設定
                // ライブ再読み込みでゲートが差し替わっても、読み取りは常に現在の実装へ届く
                // （流量制御 opt-out 時は NoopIngressGate のため空になる）。
                flowControlRejections: ingressGate,
                // 途絶検知のエントリ状態（ADR-0018 決定 4。Issue #351）: UI-4 の登録済みマーク・
                // 途絶中強調の入力。機能無効（ウォッチリスト未設定）の間は空を返す。
                sourceSilenceEntries: sourceSilenceDetector.SnapshotEntryStatuses));

        // 監査記録の最小基盤（security.md §4.1・§4.2。M6-2。Issue #52）。
        //
        // Yagura.Web（ListenerPortGuardMiddleware）は Yagura.Abstractions.Auditing.IAuditRecorder
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
        // 監査記録のデコレータチェーン: IAuditRecorder =
        //   AggregatingAuditRecorder（SEC-4 集約。#268）
        //     → ResilientAuditRecorder（SEC-10 障害中保持・書き戻し。#269）
        //       → FileAuditRecorder（ファイル + イベントログの実書き込み。#52）。
        // 全呼び出し側は IAuditRecorder 型で解決するため、両デコレータは透過的に効く。
        // Resilient は「アプリ記録ファイルへ確実に残ったか」を内側の TryRecord の戻り値で判定する
        // ため、集約の内側（＝実書き込みの直上）に置く。両デコレータは周期処理（集約の静穏サマリ・
        // 復旧スキャン）のため IHostedService としても登録する（各同一インスタンス）。
        builder.Services.AddSingleton(sp => new Yagura.Host.Observability.Auditing.FileAuditRecorder(
            dataRoot,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Yagura.Host.Observability.Auditing"),
            sp.GetRequiredService<WebGuardMetrics>()));
        builder.Services.AddSingleton<Yagura.Host.Observability.Auditing.ResilientAuditRecorder>(sp =>
            new Yagura.Host.Observability.Auditing.ResilientAuditRecorder(
                sp.GetRequiredService<Yagura.Host.Observability.Auditing.FileAuditRecorder>(),
                sp.GetRequiredService<WebGuardMetrics>(),
                timeProvider: null,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<Yagura.Host.Observability.Auditing.ResilientAuditRecorder>()));
        builder.Services.AddSingleton<Yagura.Host.Observability.Auditing.AggregatingAuditRecorder>(sp =>
            new Yagura.Host.Observability.Auditing.AggregatingAuditRecorder(
                sp.GetRequiredService<Yagura.Host.Observability.Auditing.ResilientAuditRecorder>(),
                timeProvider: null,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<Yagura.Host.Observability.Auditing.AggregatingAuditRecorder>()));
        builder.Services.AddSingleton<IAuditRecorder>(sp =>
            sp.GetRequiredService<Yagura.Host.Observability.Auditing.AggregatingAuditRecorder>());
        builder.Services.AddHostedService(sp =>
            sp.GetRequiredService<Yagura.Host.Observability.Auditing.ResilientAuditRecorder>());
        builder.Services.AddHostedService(sp =>
            sp.GetRequiredService<Yagura.Host.Observability.Auditing.AggregatingAuditRecorder>());

        // 監査記録の保持期間削除（SEC-2 = 既定 365 日。security.md §4.2。Issue #261）:
        // 起動時 1 回 + ログ本体の保持期間削除と同じ実行時刻（Retention:ExecutionTimeOfDay）で
        // 日次実行する。独立した IHostedService として登録する——受信パイプラインと順序依存が
        // なく（対象はホスト管轄のローカルファイルのみ。ILogStore・書き込みゲートに触れない）、
        // IngestionHostedService の起動順序制約（受信先行 §1.2）に関与させない。
        builder.Services.AddSingleton(sp => new Yagura.Host.Observability.Auditing.AuditRetentionScheduler(
            dataRoot,
            resolvedConfiguration.AuditRetentionDays,
            resolvedConfiguration.RetentionExecutionTimeOfDay,
            sp.GetRequiredService<IAuditRecorder>(),
            timeProvider: null,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<Yagura.Host.Observability.Auditing.AuditRetentionScheduler>()));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Yagura.Host.Observability.Auditing.AuditRetentionScheduler>());

        // 蓄積ログ移行（SQLite → SQL Server。database.md §6.2。DB-5。Issue #266）: 昇格後に
        // 旧 SQLite の蓄積ログを現行 provider へ移送する管理操作。書き込みは他経路と同じ
        // 書き込みゲートでバッチ単位に直列化する（移行中も受信を止めない——§6.2 要件①）。
        builder.Services.AddSingleton<ILogMigrationService>(sp => new Yagura.Host.Administration.LogMigrationService(
            dataRoot,
            databasePath,
            resolvedConfiguration.StorageProvider == Yagura.Host.Configuration.StorageProvider.SqlServer,
            sp.GetRequiredService<ILogStore>(),
            sp.GetRequiredService<LogStoreWriteGate>(),
            sp.GetRequiredService<IAuditRecorder>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<Yagura.Host.Administration.LogMigrationService>()));

        // ファイアウォール規則の不一致検出 + インストール記録の転記（CF-2。configuration.md §4.3。
        // Issue #265）。起動時（app.Build() 後）とリスナ再構成の適用時に照合する。
        builder.Services.AddSingleton<Yagura.Host.Firewall.IFirewallRuleReader>(sp =>
            new Yagura.Host.Firewall.WindowsFirewallRuleReader(
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<Yagura.Host.Firewall.WindowsFirewallRuleReader>()));
        builder.Services.AddSingleton(sp => new Yagura.Host.Firewall.FirewallStartupInspector(
            dataRoot,
            sp.GetRequiredService<Yagura.Host.Firewall.IFirewallRuleReader>(),
            sp.GetRequiredService<IAuditRecorder>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<Yagura.Host.Firewall.FirewallStartupInspector>()));

        // サービス実行アカウントの証跡化（ADR-0015 決定 8。Issue #263。security.md §4.1）:
        // インストーラ構成記録の初回転記（2024）と実効実行アカウントの変化検出（2025）。
        builder.Services.AddSingleton(sp => new ServiceAccountStartupInspector(
            dataRoot,
            sp.GetRequiredService<IAuditRecorder>(),
            TimeProvider.System,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<ServiceAccountStartupInspector>()));

        // 設定ライブ再読み込み（configuration.md §3。CF-4 層1。Issue #262）。即時反映の口
        // （ImmediateConfigurationApplier）はここ（合成ルート）で各コンポーネントの更新メソッドを
        // 束ねる。ここに登録されていないキーの変更は「再起動待ち」として明示される（1020）。
        builder.Services.AddSingleton<IConfigurationReloadService>(sp => new ConfigurationReloadService(
            dataRoot,
            startupRawOptions,
            new[]
            {
                // 流量制御（Issue #260）: ゲート実装ごと差し替える（SwappableIngressGate）。
                new ImmediateConfigurationApplier(
                    ["Ingestion:FlowControl:Enabled", "Ingestion:FlowControl:MessagesPerSecond", "Ingestion:FlowControl:BurstSize"],
                    newConfiguration => ingressGate.Swap(CreateIngressGate(newConfiguration))),
                // ログ本体の保持期間（M5-1）。
                new ImmediateConfigurationApplier(
                    ["Retention:Days", "Retention:ExecutionTimeOfDay"],
                    newConfiguration => sp.GetRequiredService<Yagura.Host.Retention.RetentionScheduler>().UpdateOptions(
                        new Yagura.Host.Retention.RetentionSchedulerOptions(
                            newConfiguration.RetentionDays,
                            newConfiguration.RetentionExecutionTimeOfDay))),
                // メール通知（ADR-0017 決定 9）。送信クライアントは接続を保持しないため参照の
                // 差し替えだけで足りる（次回送信から新しい値が効く）。DPAPI 復号は
                // YaguraConfigurationLoader が再読み込み時にも通るため、解決済みの値を渡すだけでよい。
                // 無効化（null）の場合はキュー内の未送信通知も破棄する——送り切りを待たず
                // 無効化の意図を優先する（決定 5）。
                // 宛先（配列キー Notification:Email:To）も対象に含む——ADR-0017 委任 9 で
                // ChangePlanner が配列キーを比較するようになったため、手編集で宛先だけを
                // 変えた再読み込みもここへ届く。
                new ImmediateConfigurationApplier(
                    ["Notification:Email:Enabled", "Notification:Email:From", "Notification:Email:To",
                     "Notification:Email:Smtp:Host", "Notification:Email:Smtp:Port",
                     "Notification:Email:Smtp:Security", "Notification:Email:Smtp:Username",
                     "Notification:Email:Smtp:Password"],
                    newConfiguration => sp.GetRequiredService<EmailNotificationDispatcher>()
                        .UpdateConfiguration(newConfiguration.EmailNotification)),
                // 送信元の途絶検知（ADR-0018 決定 6）。ウォッチリストの参照交換のみで反映できる。
                // **既存エントリの追跡状態（最終受信時刻・途絶フラグ）は保持し、削除された
                // エントリの状態は破棄する**——保持しないと設定を触るたびに全エントリが
                // 「登録時点基準」へ戻り、長い閾値のエントリが実質永久に発火しなくなる。
                new ImmediateConfigurationApplier(
                    ["Notification:SourceSilence:Watchlist", "Notification:SourceSilence:DefaultThresholdMinutes"],
                    newConfiguration => applySourceSilenceWatchlist(newConfiguration.SourceSilence?.Watchlist)),
                // 監査記録の保持期間（Issue #261）。実行時刻は Retention 側と共有のため日数のみ。
                new ImmediateConfigurationApplier(
                    ["Audit:RetentionDays"],
                    newConfiguration => sp.GetRequiredService<Yagura.Host.Observability.Auditing.AuditRetentionScheduler>()
                        .UpdateRetentionDays(newConfiguration.AuditRetentionDays)),
                // 逆引きホスト名表示（ADR-0007）。読み取り契約（IReverseDnsResolver）に書き込み
                // 操作を足さない（L-5 の参照分離を保つ）ため、実体への cast で更新の口を呼ぶ。
                new ImmediateConfigurationApplier(
                    ["Viewer:ReverseDns:Enabled"],
                    newConfiguration => (sp.GetRequiredService<Yagura.Web.ReverseDns.IReverseDnsResolver>()
                            as Yagura.Web.ReverseDns.ReverseDnsResolver)?
                        .UpdateOptions(new Yagura.Web.ReverseDns.ReverseDnsDisplayOptions(newConfiguration.ViewerReverseDnsEnabled))),
                // RFC 3164 既定タイムゾーン（Issue #134）: 解析段へパススルー。
                new ImmediateConfigurationApplier(
                    ["Ingestion:Rfc3164:DefaultTimeZone"],
                    newConfiguration => sp.GetRequiredService<IngestionPipeline>()
                        .UpdateDefaultRfc3164TimeZone(newConfiguration.DefaultRfc3164TimeZone)),
                // スプール上限（M-12）: 開いているスプールがある場合のみ実反映（無効・縮退運転時は
                // 対象が無く no-op——Spool:Enabled / Spool:Directory の変更は再起動待ちに落ちる）。
                new ImmediateConfigurationApplier(
                    ["Spool:QuotaBytes"],
                    newConfiguration => spool?.UpdateQuotaBytes(newConfiguration.SpoolQuotaBytes)),
                // 受信リスナの無瞬断再構成（CF-4 層2。Issue #262）: UDP/TCP の bind を張り替える。
                // 失敗時は旧構成で復旧（それも失敗なら CF-6 の定期再試行）。瞬断区間は
                // 受信断のシステムイベント（downtime.listener-reconfigure）として記録する
                // ——書き込みは他経路（永続化段・drain・保持期間削除）と同じ書き込みゲートで
                // 直列化する（ILogStore の単一 writer 契約。Issue #151）。
                // TLS 受信キー（Ingestion:Tls:*）は対象外——宣言どおり再起動（証明書ストア参照・
                // 秘密鍵アクセス権付与を伴うため。ConfigurationKeyMetadata 参照）。
                new ImmediateConfigurationApplier(
                    ["Ingestion:Udp:BindAddress", "Ingestion:Udp:Port", "Ingestion:Udp:ReceiveBufferBytes",
                     "Ingestion:Tcp:BindAddress", "Ingestion:Tcp:Port"],
                    async newConfiguration =>
                    {
                        var pipeline = sp.GetRequiredService<IngestionPipeline>();
                        var result = await pipeline.ReconfigureListenersAsync(
                            new UdpSyslogListenerOptions
                            {
                                BindAddress = newConfiguration.UdpBindAddress,
                                BindAddressIsExplicit = newConfiguration.UdpBindAddressIsExplicit,
                                Port = newConfiguration.UdpPort,
                                ReceiveBufferBytes = newConfiguration.UdpReceiveBufferBytes,
                            },
                            new TcpSyslogListenerOptions
                            {
                                BindAddress = newConfiguration.TcpBindAddress,
                                BindAddressIsExplicit = newConfiguration.TcpBindAddressIsExplicit,
                                Port = newConfiguration.TcpPort,
                            }).ConfigureAwait(false);

                        // ポート変更の適用時の規則突合（CF-2。configuration.md §4.3「起動時と
                        // ポート変更の適用時に検出して警告する」。Issue #265）。
                        sp.GetRequiredService<Yagura.Host.Firewall.FirewallStartupInspector>()
                            .CheckConsistency(newConfiguration);

                        // 瞬断区間の記録（configuration.md §3——「瞬断の観測は §3 の区間記録で行う」）。
                        var writeGate = sp.GetRequiredService<LogStoreWriteGate>();
                        var logStore = sp.GetRequiredService<ILogStore>();
                        foreach (var outcome in new[] { result.Udp, result.Tcp })
                        {
                            if (outcome is { GapStartedAt: { } gapStart, GapEndedAt: { } gapEnd })
                            {
                                using var gateHold = await writeGate.AcquireAsync(CancellationToken.None).ConfigureAwait(false);
                                await logStore.WriteSystemEventAsync(
                                    new SystemEvent(
                                        Yagura.Storage.SystemEventKinds.DowntimeListenerReconfigure,
                                        gapStart,
                                        gapEnd,
                                        Approximate: false),
                                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
                            }
                        }
                    }),
            },
            sp.GetRequiredService<IAuditRecorder>(),
            timeProvider: null,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<ConfigurationReloadService>()));

        // 起動時の設定差分照合（Issue #329）: 前回適用スナップショットと起動時設定の差分を
        // 監査 2019 へ記録する（手編集 + 再起動で反映された変更の軽量補完。security.md §4.1）。
        // 起動時（app.Build() 後）に照合し、契機①としてスナップショットを取り直す。
        builder.Services.AddSingleton(sp => new StartupConfigurationInspector(
            dataRoot,
            sp.GetRequiredService<IAuditRecorder>(),
            timeProvider: null,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<StartupConfigurationInspector>()));

        // 監査の 2000 番台（管理操作）はレベル「情報」でイベントログへ併記する（security.md §4.3）。
        // EventLog プロバイダの既定フィルタは Warning 以上のため、監査カテゴリに限り Information
        // まで通す（「ソース Yagura の警告以上を通知」という最小監視構成は 1000/3000 番台の
        // 警告で成立したまま、管理操作の証跡もイベントログに残る）。
        builder.Logging.AddFilter<EventLogLoggerProvider>(
            "Yagura.Host.Observability.Auditing",
            LogLevel.Information);

        // 途絶からの復帰（1029。情報レベル——1000 番台に情報レベルを置く初例）もイベントログへ
        // 届ける（ADR-0018 決定 3——途絶警告と対で「ログが欠けていた期間」の終端を証跡に残す。
        // Issue #382: 既定フィルタが Warning 以上のため、このフィルタなしでは 1029 がコンソールに
        // しか出ず、証跡が実在しなかった）。カテゴリを監視ループに限定し、他カテゴリの
        // Information は開放しない（同カテゴリの他の Information——保留開始・回復・seed 失敗——も
        // イベントログに残るが、いずれも受信断・判定状態の運用証跡であり対象として妥当）。
        builder.Logging.AddFilter<EventLogLoggerProvider>(
            "Yagura.Host.Observability.ActiveNotification.ActiveNotificationMonitor",
            LogLevel.Information);

        // ---- 管理画面の書き込み系サービス（M8-4。Issue #71）----
        //
        // 契約は Yagura.Abstractions.Administration（IYaguraWriteService 実装群——security.md
        // §1 L-5 の参照分離検査の対象）、実体は設定ファイル・DB 接続を管轄する Host 側に置く
        // （FileAuditRecorder と同じ結線パターン。architecture.md §1.1）。
        builder.Services.AddSingleton<Yagura.Host.Administration.ISqlServerConnectionValidator,
            Yagura.Host.Administration.SqlServerConnectionValidator>();
        builder.Services.AddSingleton<Yagura.Abstractions.Administration.ISetupWizardService>(sp =>
            new Yagura.Host.Administration.SetupWizardService(
                dataRoot,
                sp.GetRequiredService<IAuditRecorder>(),
                timeProvider: null,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<Yagura.Host.Administration.SetupWizardService>()));
        builder.Services.AddSingleton<Yagura.Abstractions.Administration.IPromotionWizardService>(sp =>
            new Yagura.Host.Administration.PromotionWizardService(
                dataRoot,
                sp.GetRequiredService<Yagura.Host.Administration.ISqlServerConnectionValidator>(),
                sp.GetRequiredService<IAuditRecorder>()));

        // circuit 統治（M8-4。security.md §2）: リスナ帰属判定・上限ガードが参照する管理ポートの
        // 実値と、circuit 管理（一覧・個別切断）のサービスを登録する。
        builder.Services.AddSingleton(new Yagura.Web.Administration.YaguraAdminListenerPort(effectiveAdminPorts));
        builder.Services.AddYaguraAdmin();

        // ---- 管理 UI 認証（ADR-0010 Phase 1）----
        //
        // 管理者アカウントストアは ILogStore と同じ provider 選択（Program.cs 上の switch）に
        // 相乗りする（ADR-0010 決定 3「既存のデータ provider 抽象に載る単一テーブル」）が、
        // ILogStore とは独立の契約（Yagura.Storage.Administration.IAdminAccountStore）とする
        // ——ログレコード専用の形をした ILogStore の性質を歪めないため（IAdminAccountStore の
        // remarks 参照）。
        builder.Services.AddSingleton<Yagura.Storage.Administration.IAdminAccountStore>(_ => resolvedConfiguration.StorageProvider switch
        {
            Yagura.Host.Configuration.StorageProvider.SqlServer =>
                new Yagura.Storage.Administration.SqlServer.SqlServerAdminAccountStore(resolvedConfiguration.SqlServerConnectionString!),
            _ => new Yagura.Storage.Administration.Sqlite.SqliteAdminAccountStore(databasePath),
        });
        // ADR-0011 決定 2〜5.1: 三層防御（バックオフ・IP レート制限・グローバルトークンバケット）の
        // 状態保持はプロセス内シングルトン——AppAdminAuthenticationService（ログイン判定）と
        // ActiveNotificationMonitor（能動通知への昇格。決定 6）の両方が同一インスタンスを参照する。
        builder.Services.AddSingleton<Yagura.Host.Administration.AdminAuthentication.AdminAuthFailureDefense>();
        builder.Services.AddSingleton<Yagura.Host.Administration.AdminAuthentication.AppAdminAuthenticationService>();
        builder.Services.AddSingleton<Yagura.Abstractions.Administration.IAppAdminAuthenticator>(
            sp => sp.GetRequiredService<Yagura.Host.Administration.AdminAuthentication.AppAdminAuthenticationService>());
        builder.Services.AddSingleton<Yagura.Abstractions.Administration.IAdminAuthenticationAdminService>(sp =>
            new Yagura.Host.Administration.AdminAuthentication.AdminAuthenticationAdminService(
                dataRoot,
                sp.GetRequiredService<Yagura.Storage.Administration.IAdminAccountStore>(),
                sp.GetRequiredService<Yagura.Host.Administration.AdminAuthentication.AppAdminAuthenticationService>(),
                sp.GetRequiredService<IAuditRecorder>()));

        // 管理リモート HTTPS 証明書の選択 UI 用の read-only 列挙（ADR-0012 決定 2）。副作用なし・
        // 依存なし（LocalMachine\My を ReadOnly で開くだけ）。実体は Windows 専用（他の Store* 型と
        // 同様、合成ルートで直接 new する）。
        builder.Services.AddSingleton<Yagura.Abstractions.Administration.ICertificateStoreReader>(
            _ => new Yagura.Host.Administration.Https.WindowsCertificateStoreReader());

        // 管理リモート HTTPS の設定保存・保存前 fail-closed 検証 + 監査（ADR-0012 決定 1・4・7。
        // 上記 read-only 列挙とは別契約の書き込み系サービス——IAdminAuthenticationAdminService と
        // 同じ「dataRoot + IAuditRecorder を渡して Host 実体を結線する」形式）。
        builder.Services.AddSingleton<Yagura.Abstractions.Administration.IAdminRemoteAccessAdminService>(sp =>
            new Yagura.Host.Administration.Https.AdminRemoteAccessAdminService(
                dataRoot,
                sp.GetRequiredService<IAuditRecorder>()));

        // TLS 受信の証明書設定の保存・保存前 fail-closed 検証 + 監査（ADR-0019 決定 1・2・5。
        // Issue #349）。上記の管理リモート HTTPS 版と同型で、証明書の列挙・解決・EKU 判定・
        // 秘密鍵の読取検証は実装を共有する（二重実装しない）。挙動が割れるのは期限切れと
        // 秘密鍵読取不可の 2 点のみ（IngestionTlsAdminService の remarks 参照）。
        builder.Services.AddSingleton<Yagura.Abstractions.Administration.IIngestionTlsAdminService>(sp =>
            new Yagura.Host.Ingestion.Tls.IngestionTlsAdminService(
                dataRoot,
                sp.GetRequiredService<IAuditRecorder>()));

        // メール通知の設定・テスト送信・健全性参照（ADR-0017 決定 4・8。Issue #350）。
        // ディスパッチャは Func 経由で遅延解決する——本サービスとディスパッチャの登録順に
        // 依存させないため（AddSingleton のファクトリは Build 後の初回解決時に走る）。
        builder.Services.AddSingleton<IEmailNotificationAdminService>(sp =>
            new EmailNotificationAdminService(
                dataRoot,
                sp.GetRequiredService<IAuditRecorder>(),
                emailNotificationQueue,
                () => sp.GetService<EmailNotificationDispatcher>()));

        // 途絶検知のウォッチリスト設定（ADR-0018 決定 4・5・6。Issue #351）。即時反映の口は
        // 再読み込み経路（ImmediateConfigurationApplier）と同一のデリゲートを渡す——反映経路を
        // 1 本に保つ。候補選択（決定 4）は ILogStore の送信元別集計から取る。
        builder.Services.AddSingleton<Yagura.Abstractions.Administration.ISourceSilenceAdminService>(sp =>
            new Yagura.Host.Observability.ActiveNotification.SourceSilence.SourceSilenceAdminService(
                dataRoot,
                sp.GetRequiredService<IAuditRecorder>(),
                sp.GetRequiredService<ILogStore>(),
                applySourceSilenceWatchlist,
                sourceSilenceDetector.SnapshotEntryStatuses));

        // AD グループ → 役割マッピング（SEC-9。ADR-0010 決定 5・7・委任事項 8）。設定の生指定（名/SID）を
        // 起動時に SID 集合へ解決してキャッシュする（名 → SID は Windows 専用の NTAccount.Translate——本
        // エントリポイントは [SupportedOSPlatform("windows")]）。解決できない指定は警告してスキップされる
        // （認可を付与しない安全側。WindowsSecurityGroupResolver の remarks 参照）。
        //
        // 解決は factory 登録にして、ロガーを DI（app.Build() 後に構築されるロギングパイプライン）から
        // 取る（Issue #346）。以前は bootstrapLoggerFactory（コンソールのみ）で解決していたため、
        // [sec9-group-unresolved] の警告が EventLog プロバイダの構築前に出て**運用者に届かなかった**
        // ——「スキップ」は成立しているのに「警告」が成立しておらず、グループ名のタイプミスが
        // 「そのグループの所属者が黙って認可されない」という形でしか現れなかった。
        // FirewallStartupInspector / StartupConfigurationInspector と同じ形（sp から ILoggerFactory を
        // 取り、実行は app.Build() 後）に揃える。解決の実行時点は下の app.Build() 直後で固定する
        // （遅延解決のまま放置すると初回のログイン要求まで警告が出ない）。
        builder.Services.AddSingleton(sp =>
        {
            var sec9Logger = sp.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Yagura.Host.Administration.WindowsGroupResolver");

            return new Yagura.Web.Administration.WindowsGroupAuthorizationOptions(
                AdminGroupSids: Yagura.Host.Administration.AdminAuthentication.WindowsSecurityGroupResolver.ResolveToSids(
                    resolvedConfiguration.AdminWindowsAdminGroups, "Admin:Authentication:Windows:AdminGroups", sec9Logger),
                ViewerGroupSids: Yagura.Host.Administration.AdminAuthentication.WindowsSecurityGroupResolver.ResolveToSids(
                    resolvedConfiguration.ViewerWindowsViewerGroups, "Viewer:Authentication:Windows:ViewerGroups", sec9Logger),
                ViewerAdminGroupSids: Yagura.Host.Administration.AdminAuthentication.WindowsSecurityGroupResolver.ResolveToSids(
                    resolvedConfiguration.ViewerWindowsAdminGroups, "Viewer:Authentication:Windows:AdminGroups", sec9Logger));
        });

        // 認証スキーム（Negotiate/AppAuth Cookie）・認可ポリシー（管理 + 閲覧）の登録
        // （AdminAuthenticationExtensions 参照）。スキームは管理・閲覧で共用の単一構成（ADR-0013 決定 1 の
        // 単一 Cookie を閲覧へも展開——オーナー決定 2026-07-12）。実効値は起動時に固定される（反映方式は
        // §3「サービス再起動」——ConfigurationKeyMetadata 参照）。
        builder.Services.AddYaguraAdminAuthentication(
            resolvedConfiguration.AdminWindowsAuthEnabled,
            resolvedConfiguration.AdminWindowsAuthKerberosOnly,
            resolvedConfiguration.AdminAppAuthEnabled,
            viewerWindowsAuthEnabled: resolvedConfiguration.ViewerWindowsAuthEnabled,
            viewerKerberosOnly: resolvedConfiguration.ViewerWindowsAuthKerberosOnly);
        builder.Services.AddSingleton(new Yagura.Web.Administration.AdminAuthenticationRuntimeOptions(
            RequireAuthentication: resolvedConfiguration.AdminAuthRequireForLoopback,
            WindowsAuthEnabled: resolvedConfiguration.AdminWindowsAuthEnabled,
            AppAuthEnabled: resolvedConfiguration.AdminAppAuthEnabled));

        // 閲覧 UI 認証（ADR-0010 Phase 4 決定 7）の実効値。閲覧ログイン画面・MainLayout の circuit 層 viewer
        // ガードが参照する。AppAuthAvailable = アプリ独自認証の有効/無効（アプリアカウントは管理・閲覧で
        // 共有の単一ストア——閲覧ログインでも Windows + アプリ両方を提示するオーナー決定 2026-07-12）。
        builder.Services.AddSingleton(new Yagura.Web.Administration.ViewerAuthenticationRuntimeOptions(
            Enabled: resolvedConfiguration.ViewerWindowsAuthEnabled,
            AppAuthAvailable: resolvedConfiguration.AdminAppAuthEnabled));

        // 認証セッションの世代番号ストア（ADR-0013 決定 2）。緊急全失効（世代バンプ）と各要求での
        // fail-closed 照合の状態源。データルート配下に永続化し、定常再起動では同じ世代で復帰する
        // （既発行セッション生存）。IAdminAuthenticationAdminService 等と同じ「dataRoot を渡して Host 実体を
        // 結線する」形式。
        builder.Services.AddSingleton<Yagura.Abstractions.Administration.IAdminSessionGenerationStore>(
            _ => new Yagura.Host.Administration.FileAdminSessionGenerationStore(dataRoot));

        // 認証セッション Cookie の暗号鍵（Data Protection）をデータルート配下に永続化する（ADR-0013 決定 6）。
        // 既定の DP キー格納先（サービスアカウントのプロファイル/レジストリ）はプロセス/アカウント環境に
        // 依存し、揮発するとサービス再起動のたびに全 Cookie が無効化される（＝全利用者再ログイン）。
        // データルート配下に固定することで、定常再起動をまたいで Cookie が生存する（世代番号——上記——の
        // バンプによる緊急全失効とは独立）。格納先はデータルートの ACL（security.md §5——SYSTEM/
        // Administrators/サービスアカウントのみ）を継承し、`Users`/`Authenticated Users` の ACE を持たない。
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataRoot, "dataprotection-keys")))
            .SetApplicationName("Yagura.Admin");

        // フォワーダ配布キットの MSI オプトイン同梱（ADR-0008 設計条件 9・委任 #7）: 配置フォルダは
        // データルート配下 forwarder（%ProgramData%\Yagura\forwarder）。Web 層はデータルートの
        // 実パスを直接知らないため（INicCandidateSource と異なり外部設定＝データルートに依存する）、
        // ISetupWizardService / IPromotionWizardService と同じ結線パターンでここ Host が実パスを
        // 注入する（architecture.md §1.1 の参照構造）。フォルダの作成・ACL 設定はインストーラ
        // （WiX）の領分であり、ここでは作成しない（無ければ未検出として扱う——実装 PR の判断）。
        builder.Services.AddSingleton<Yagura.Web.ForwarderKit.IForwarderMsiSource>(
            _ => new Yagura.Web.ForwarderKit.SystemForwarderMsiSource(forwarderMsiFolderPath));

        // ADR-0020（配置経路 (b)）: 書き込み側 store と機能 opt-in の実効値。store は機能の
        // 有効/無効に関わらず登録する（無効時は誰も呼ばない——エンドポイントは構造的に不在・
        // 画面区画は表出しない）。ACL の変更は一切行わない（決定 2——書き込み経路の開放は
        // OS 管理者の明示 ACE 付与のみ）。
        builder.Services.AddSingleton<Yagura.Web.ForwarderKit.IForwarderMsiStore>(
            _ => new Yagura.Web.ForwarderKit.SystemForwarderMsiStore(forwarderMsiFolderPath));
        builder.Services.AddSingleton(new Yagura.Web.ForwarderKit.ForwarderMsiUploadRuntimeOptions(
            resolvedConfiguration.AdminForwarderMsiUploadEnabled));

        // 逆引きホスト名表示の設定（ADR-0007。Viewer:ReverseDns:Enabled——検証・縮小適用済みの
        // 値を Web 層へ渡す。AddYaguraWebViewer の TryAdd 既定（無効）より先に登録すること）。
        builder.Services.AddSingleton(
            new Yagura.Web.ReverseDns.ReverseDnsDisplayOptions(resolvedConfiguration.ViewerReverseDnsEnabled));

        builder.Services.AddYaguraWebViewer();

        var app = builder.Build();

        // SEC-9（Issue #346）: AD グループ → SID の解決をここで実行する。singleton の遅延解決に
        // 任せると初回のログイン要求まで解決されず、解決できない指定の警告
        // （[sec9-group-unresolved]）が起動時に出ない。ここで一度取得して起動時点に固定する
        // （以降の参照は同一インスタンス。下の自己ロックアウト注意でも使う）。
        // 構築済みのロギングパイプライン経由なので、警告は EventLog へ到達する。
        var windowsGroupAuthorization =
            app.Services.GetRequiredService<Yagura.Web.Administration.WindowsGroupAuthorizationOptions>();

        // CF-2（Issue #265）: 起動時のファイアウォール規則突合 + インストール記録の初回転記。
        // いずれも失敗が起動を妨げない（Inspector 内で完結）。転記は非同期で開始し完了を待たない
        // （監査レール——FileAuditRecorder——は例外を投げない契約）。
        {
            var firewallInspector = app.Services.GetRequiredService<Yagura.Host.Firewall.FirewallStartupInspector>();
            firewallInspector.CheckConsistency(resolvedConfiguration);
            _ = firewallInspector.TranscribeInstallationRecordOnceAsync(CancellationToken.None);
        }

        // 起動時の設定差分照合 + 前回適用スナップショットの取り直し（Issue #329）。失敗は起動を
        // 妨げない（Inspector 内で完結）。非同期で開始し完了を待たない（CF-2 の転記と同型——
        // 監査レールは例外を投げない契約）。
        {
            var startupConfigurationInspector = app.Services.GetRequiredService<StartupConfigurationInspector>();
            _ = startupConfigurationInspector.InspectAndRefreshSnapshotAsync(startupRawOptions, CancellationToken.None);
        }

        // サービス実行アカウントの実効値（プロセスが実際に動いている Windows 識別）。証跡化
        // （2024/2025）と、以降の秘密鍵権限の付与先（security.md §5.2「付与先を実行アカウントから
        // 導出する」——gMSA opt-in で固定名 NT SERVICE\Yagura と実効値が乖離するため）に使う。
        var effectiveServiceAccountName = ServiceAccountStartupInspector.ResolveEffectiveAccountName();

        // サービス実行アカウントの構成転記（2024）と前回起動時からの変化検出（2025）
        // （ADR-0015 決定 8。Issue #263）。失敗は起動を妨げない（Inspector 内で完結）。
        {
            var serviceAccountInspector = app.Services.GetRequiredService<ServiceAccountStartupInspector>();
            _ = serviceAccountInspector.TranscribeInstallationRecordOnceAsync(CancellationToken.None);
            _ = serviceAccountInspector.DetectAccountChangeAndRefreshAsync(effectiveServiceAccountName, CancellationToken.None);
        }

        // bind 再試行（CF-6）による受信再開の記録（Issue #291）: 開けなかった区間を受信断の
        // システムイベント（downtime.listener-bind-retry）として残す。書き込みは他経路と同じ
        // 書き込みゲートで直列化する（Issue #151——再試行成功時は消費ループが既に動いている）。
        {
            var pipelineForRecovery = app.Services.GetRequiredService<IngestionPipeline>();
            var recoveryLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Yagura.Host.Startup");
            pipelineForRecovery.ListenerBindRecovered += recovery => _ = Task.Run(async () =>
            {
                try
                {
                    var writeGate = app.Services.GetRequiredService<LogStoreWriteGate>();
                    var logStore = app.Services.GetRequiredService<ILogStore>();
                    using var gateHold = await writeGate.AcquireAsync(CancellationToken.None).ConfigureAwait(false);
                    await logStore.WriteSystemEventAsync(
                        new SystemEvent(
                            Yagura.Storage.SystemEventKinds.DowntimeListenerBindRetry,
                            recovery.GapStartedAt,
                            recovery.RecoveredAt,
                            Approximate: false),
                        cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // 記録失敗は受信の再開自体を妨げない（ログのみ）。
                    recoveryLogger.LogWarning(
                        ex,
                        "bind 再試行による受信再開（{Protocol}）の受信断区間の記録に失敗しました。",
                        recovery.ProtocolLabel);
                }
            });
        }

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
            // CP437 等)、E2E テストはこの ASCII トークンで照合する。イベント ID は
            // ActiveNotificationEventIds.SpoolDegradedStartup（1001。security.md §4.3「1000 番台
            // = 運用警告」区画。Issue #149 で 1000 番台を実配線した際に遡及割当・記録した）。
            startupLogger.LogWarning(
                Yagura.Host.Observability.ActiveNotification.ActiveNotificationEventIds.SpoolDegradedStartup,
                spoolOpenFailure,
                "[spool-degraded-mode] スプール領域 {SpoolDirectory} を開けなかったため、スプールなし縮退運転で起動します。" +
                "縮退中は Q2 溢れ・書き込み失敗分が破棄され、永続化失敗カウンタへ計上されます。",
                resolvedConfiguration.SpoolDirectory);
        }

        // フォワーダ MSI アップロード（ADR-0020 決定 2・3）の起動時処理:
        // ①孤児ステージングファイルの掃除（中断・プロセス停止の残骸。決定 3——起動時 + 新規
        //   アップロード開始時の二点で掃除する）、
        // ②ACL 乖離の起動時一回検査（決定 2——周期監視〔1 分後から〕に加えて起動直後にも一度
        //   検査し、乖離警告 1033 の初回検出を最大 1 周期分早める。開放継続通知 1034 は
        //   「継続」の観測を要するため周期監視のみが担う）。
        // いずれも失敗は起動を妨げない。
        {
            var forwarderStartupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Yagura.Host.Startup");
            try
            {
                var forwarderStore = app.Services.GetRequiredService<Yagura.Web.ForwarderKit.IForwarderMsiStore>();
                var removedStaging = forwarderStore.CleanupStagingFiles();
                if (removedStaging > 0)
                {
                    forwarderStartupLogger.LogInformation(
                        "フォワーダ MSI 配置フォルダの孤児ステージングファイル {Count} 件を起動時に削除しました（ADR-0020 決定 3）。",
                        removedStaging);
                }
            }
            catch (Exception ex)
            {
                forwarderStartupLogger.LogWarning(ex, "フォワーダ MSI 配置フォルダの起動時掃除に失敗しました（起動は継続します）。");
            }

            if (!resolvedConfiguration.AdminForwarderMsiUploadEnabled && OperatingSystem.IsWindows())
            {
                var writable = Yagura.Host.Administration.ForwarderKitUpload.ForwarderMsiFolderAclInspector
                    .IsWritableByCurrentIdentity(forwarderMsiFolderPath);
                if (writable is true)
                {
                    forwarderStartupLogger.LogWarning(
                        Yagura.Host.Observability.ActiveNotification.ActiveNotificationEventIds.ForwarderMsiFolderAclDrift,
                        "[forwarder-msi-acl-drift] フォワーダ MSI 配置フォルダ {FolderPath} にサービス実行アカウントの" +
                        "書き込み権限（ACE）が残っていますが、管理画面アップロード機能" +
                        "（Admin:ForwarderKit:MsiUpload:Enabled）は無効です。機能の利用を終えた後の" +
                        "ACE の撤去（閉じ忘れ）を確認してください（ADR-0020 決定 2。撤去手順は利用者ガイド参照）。",
                        forwarderMsiFolderPath);
                }
            }
        }

        // 管理リスナのリモート HTTPS（ADR-0010 Phase 2 決定 4）: 証明書が解決できなかった場合の
        // 起動時警告（縮小継続——起動は中止しない。ConfigurationEventIds.AdminHttpsCertificateUnavailableAtStartup
        // = 1013 参照）。app.Build() 後の DI ロガーを使うことで EventLog プロバイダへも到達する。
        if (adminHttpsCertificateUnavailableReason is not null)
        {
            var httpsLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Yagura.Host.Administration.Https");
            httpsLogger.LogWarning(
                Yagura.Host.Configuration.ConfigurationEventIds.AdminHttpsCertificateUnavailableAtStartup,
                "[admin-https-certificate-unavailable] 管理リスナのリモート HTTPS（ポート {HttpsPort}）用証明書を" +
                "解決できなかったため、このリスナは開かずに縮小継続します（loopback 経由の管理リスナ（ポート {AdminPort}）は" +
                "影響を受けません——ADR-0010 Phase 2 決定 4）。理由: {Reason}",
                resolvedConfiguration.AdminHttpsPort,
                effectiveAdminPort,
                adminHttpsCertificateUnavailableReason);
        }
        else if (adminHttpsCertificate is not null)
        {
            // 秘密鍵の読み取り権限をサービスアカウントへ付与する（configuration.md §6 の既存方式と
            // 同型。付与は監査記録の対象——ADR-0010 Phase 2 決定 4）。付与先は固定名ではなく
            // 実効実行アカウント（gMSA opt-in——ADR-0015——で乖離する。security.md §5.2）。
            // ベストエフォート: 付与に失敗しても HTTPS リスナ自体は（証明書が現在の実行アカウント
            // から既に読める限り）動作を継続できるため、起動は妨げない——警告のみ残す
            // （CF-D2 の手動手順への誘導）。
            var httpsLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Yagura.Host.Administration.Https");
            var grantResult = Yagura.Host.Administration.Https.CertificatePrivateKeyAccessGranter.TryGrantReadAccess(
                adminHttpsCertificate, effectiveServiceAccountName);

            var auditRecorder = app.Services.GetRequiredService<IAuditRecorder>();
            if (grantResult.Succeeded)
            {
                await auditRecorder.RecordAsync(new AuditEvent(
                    OccurredAt: TimeProvider.System.GetUtcNow(),
                    Kind: AuditEventKind.AdminHttpsCertificatePrivateKeyAccessGranted,
                    RemoteAddress: null,
                    RemotePort: null,
                    Detail: $"thumbprint={resolvedConfiguration.AdminHttpsCertificateThumbprint};account={effectiveServiceAccountName}"))
                    .ConfigureAwait(false);
            }
            else
            {
                httpsLogger.LogWarning(
                    "[admin-https-private-key-grant-failed] 管理リスナのリモート HTTPS 証明書の秘密鍵読み取り権限を" +
                    "{Account} へ自動付与できませんでした（理由: {Reason}）。サービスアカウントが証明書へ既存の権限で" +
                    "アクセスできない場合、リモート HTTPS の接続受付が失敗する可能性があります。証明書スナップイン" +
                    "（certlm.msc）から手動で権限を付与してください（configuration.md §6 CF-D2）。",
                    effectiveServiceAccountName,
                    grantResult.FailureReason);
            }
        }

        // TLS 受信（RFC 5425。opt-in。security.md §6。Issue #137）: 証明書が解決できなかった場合の
        // 起動時警告（縮小継続——起動は中止しない。平文 UDP/TCP 受信には一切影響しない。
        // ConfigurationEventIds.IngestionTlsCertificateUnavailableAtStartup = 1016 参照）。
        // 管理リスナのリモート HTTPS（上記）と同じパターン。
        if (ingestionTlsCertificateUnavailableReason is not null)
        {
            var tlsLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Yagura.Ingestion.Tls");
            tlsLogger.LogWarning(
                Yagura.Host.Configuration.ConfigurationEventIds.IngestionTlsCertificateUnavailableAtStartup,
                "[ingestion-tls-certificate-unavailable] TLS 受信（ポート {TlsPort}）用証明書を解決できなかったため、" +
                "このリスナは開かずに縮小継続します（平文 UDP/TCP 受信（ポート {UdpPort}/{TcpPort}）は影響を" +
                "受けません——ADR-0004 決定 3）。理由: {Reason}",
                resolvedConfiguration.IngestionTlsPort,
                resolvedConfiguration.UdpPort,
                resolvedConfiguration.TcpPort,
                ingestionTlsCertificateUnavailableReason);
        }
        else if (ingestionTlsCertificate is not null)
        {
            // 秘密鍵の読み取り権限をサービスアカウントへ付与する（管理リスナのリモート HTTPS と
            // 同型。付与は監査記録の対象——security.md §6。付与先は実効実行アカウント——
            // security.md §5.2）。ベストエフォート: 付与に失敗しても TLS 受信自体は（証明書が
            // 現在の実行アカウントから既に読める限り）動作を継続できるため、起動は妨げない——
            // 警告のみ残す。
            var tlsLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Yagura.Ingestion.Tls");
            var ingestionTlsGrantResult = Yagura.Host.Administration.Https.CertificatePrivateKeyAccessGranter.TryGrantReadAccess(
                ingestionTlsCertificate, effectiveServiceAccountName);

            var ingestionTlsAuditRecorder = app.Services.GetRequiredService<IAuditRecorder>();
            if (ingestionTlsGrantResult.Succeeded)
            {
                await ingestionTlsAuditRecorder.RecordAsync(new AuditEvent(
                    OccurredAt: TimeProvider.System.GetUtcNow(),
                    Kind: AuditEventKind.IngestionTlsCertificatePrivateKeyAccessGranted,
                    RemoteAddress: null,
                    RemotePort: null,
                    Detail: $"thumbprint={resolvedConfiguration.IngestionTlsCertificateThumbprint};account={effectiveServiceAccountName}"))
                    .ConfigureAwait(false);
            }
            else
            {
                tlsLogger.LogWarning(
                    "[ingestion-tls-private-key-grant-failed] TLS 受信証明書の秘密鍵読み取り権限を" +
                    "{Account} へ自動付与できませんでした（理由: {Reason}）。サービスアカウントが証明書へ既存の権限で" +
                    "アクセスできない場合、TLS 受信の接続受付が失敗する可能性があります。証明書スナップイン" +
                    "（certlm.msc）から手動で権限を付与してください（configuration.md §6 CF-D2 と同型の手順）。",
                    effectiveServiceAccountName,
                    ingestionTlsGrantResult.FailureReason);
            }
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
        app.UseYaguraListenerPortGuard(effectiveAdminPorts);

        // 管理 UI 認証（ADR-0010 Phase 1）。ポートガードの直後（管理系 404 の判定が認証
        // チャレンジより優先——閲覧リスナ経由の到達はそもそも認証を試みずに 404 で終わる）・
        // circuit 統治ガードの前（circuit 確立可否の判定より、確立要求の認証状態を先に確定させる）。
        app.UseYaguraAdminAuthentication(
            resolvedConfiguration.AdminWindowsAuthKerberosOnly,
            resolvedConfiguration.ViewerWindowsAuthKerberosOnly);

        // circuit 統治のガード(M8-4。security.md §2.1 origin 検証・§2.2 上限。SEC-1/SEC-8 仮値)。
        // ポートガードの直後(管理系 404 の判定が先——存在を漏らさない応答が上限案内より優先)。
        app.UseYaguraCircuitGuard();

        // ルート登録は Yagura.Web 側の 2 つの集約点に分ける:
        // - MapYaguraWebViewer(閲覧系。書き込みエンドポイントを持たない)
        // - MapYaguraAdmin(管理系。M8-4 から管理画面は Razor Components のページ——
        //   Yagura.Web.Administration 名前空間——であり、MapYaguraAdmin はそれらの
        //   エンドポイントへ ListenerPortGuardEndpointMetadata.Admin を機械的に付与する
        //   規約を差し込む。上記ガードにより管理リスナ以外からは 404 になる)
        // 閲覧系ルートは管理リスナからも到達できる設計とした(ui.md §4 の線引きは「閲覧
        // リスナに書き込み系を置かない」であり、管理リスナは loopback 限定のため閲覧系が
        // 同居しても安全側に働く——管理者がローカルで全部見られることはむしろ自然)。
        // Host 側で個別に MapGet 等を追加しない(各拡張メソッドのコメント参照)。
        // 閲覧 UI 認証（ADR-0010 Phase 4 決定 7）: 閲覧認証有効時のみ閲覧ページ・CSV に ViewerPolicy を
        // 付与し、閲覧ログインエンドポイント（/login/*）を登録する。既定（無効）は現状どおり認証なし。
        var razorComponents = app.MapYaguraWebViewer(
            staticAssetsManifestPath: null,
            viewerAuthEnabled: resolvedConfiguration.ViewerWindowsAuthEnabled,
            appAuthAvailable: resolvedConfiguration.AdminAppAuthEnabled);
        // 認可(AdminPolicyName)を管理系エンドポイントへ「付与するかどうか」の判定
        // (ADR-0010 Phase 1 決定 1 の RequireForLoopback だけでなく、Phase 2 のリモートバインドが
        // 有効な場合も付与が要る——リモート経由の管理操作は RequireForLoopback の値に関わらず
        // 常に認証必須のため(決定 1)。実際に「loopback は無条件許可・リモートは必須」という
        // 接続元による分岐は、付与された認可ポリシー自体(AdminAuthenticationExtensions.AdminPolicyName
        // の RequireAssertion)の内部で行う——ここでは「ポリシーを一切付与しない」既定運用
        // (Phase 1 のみで RequireForLoopback も RemoteBinding も無効な既定構成)との切り分けのみ行う。
        var adminAuthorizationRequired =
            resolvedConfiguration.AdminAuthRequireForLoopback || resolvedConfiguration.AdminRemoteBindingEnabled;
        app.MapYaguraAdmin(
            razorComponents,
            adminAuthorizationRequired,
            resolvedConfiguration.AdminWindowsAuthEnabled,
            // ADR-0020 決定 1: 有効時のみアップロード関連エンドポイントが登録される（構造的非存在）。
            // 有効は fail-closed（1032）により adminAuthorizationRequired = true を含意する。
            resolvedConfiguration.AdminForwarderMsiUploadEnabled);

        // 管理者アカウントストアのスキーマ初期化（ADR-0010 Phase 1。ILogStore と同じ
        // 「受信開始（Kestrel の listen 開始）より前に初期化を終える」順序——
        // IngestionHostedService.StartAsync が listen 開始前に必ず待たれる（上記コメント参照）
        // のと同じ理由で、ここ（app.RunAsync() 呼び出し前）で同期的に完了させる。
        var adminAccountStore = app.Services.GetRequiredService<Yagura.Storage.Administration.IAdminAccountStore>();
        await adminAccountStore.InitializeAsync().ConfigureAwait(false);

        // 自己ロックアウトの footgun に対する起動時警告（RequireForLoopback=true +
        // App 認証のみ有効 + アカウント未作成だと、GUI へ到達する手段が一切なくなる）。
        // ハード起動失敗にはしない——それだと syslog 受信自体が止まってしまい、より悪い結果になる
        // （復旧は設定ファイル編集で RequireForLoopback を false に戻すか、いずれにせよ設定変更が要る）。
        if (resolvedConfiguration.AdminAuthRequireForLoopback &&
            resolvedConfiguration.AdminAppAuthEnabled &&
            !resolvedConfiguration.AdminWindowsAuthEnabled)
        {
            var hasAnyAccount = await adminAccountStore.HasAnyAccountAsync().ConfigureAwait(false);
            if (!hasAnyAccount)
            {
                var lockoutLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Yagura.Host.Administration.SelfLockout");
                lockoutLogger.LogWarning(
                    "[admin-self-lockout-risk] loopback 認証が必須（Admin:Authentication:RequireForLoopback=true）で、" +
                    "アプリ独自認証のみが有効（Windows 認証は無効）ですが、管理者アカウントが1件も作成されていないため、" +
                    "管理 UI へログインする手段がありません。復旧するには設定ファイルで " +
                    "Admin:Authentication:RequireForLoopback を false に変更してから再起動してください。");
            }
        }

        // 閲覧認証の自己ロックアウト注意（ADR-0010 Phase 4・SEC-9）: 閲覧 Windows 認証を有効化したのに
        // 閲覧/管理いずれのグループも解決できていない（未指定・全て解決失敗）と、Windows 経由では誰も
        // 閲覧できない。ハード起動失敗にはしない——閲覧断は syslog 受信を止めず、管理者は :8515（管理
        // リスナ）や設定ファイル編集で復旧できる。アプリ独自認証が有効なら app 管理者は閲覧に到達できる。
        if (resolvedConfiguration.ViewerWindowsAuthEnabled &&
            windowsGroupAuthorization.ViewerGroupSids.Count == 0 &&
            windowsGroupAuthorization.ViewerAdminGroupSids.Count == 0)
        {
            var viewerLockoutLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Yagura.Host.Administration.ViewerAuth");
            viewerLockoutLogger.LogWarning(
                "[viewer-auth-no-groups] 閲覧 UI の Windows 認証（Viewer:Authentication:Windows:Enabled=true）が" +
                "有効ですが、閲覧・管理いずれの AD グループも解決できていません（Viewer:Authentication:Windows:" +
                "ViewerGroups / AdminGroups が未指定、または全て解決に失敗）。この状態で Windows 経由で閲覧 UI に" +
                "サインインできるのは、ローカル Administrators（BUILTIN\\Administrators。管理役割）に限られます" +
                "——ドメインコントローラー上ではネットワークトークンに 544 が載らないため、その場合は誰もサインイン" +
                "できません。閲覧者を許可するには ViewerGroups に、AD グループ経由の管理者を許可するには AdminGroups に、" +
                "グループ名（DOMAIN\\Group）または SID を指定してください（アプリ独自認証が有効なら、その管理者" +
                "アカウントは引き続き閲覧に到達できます）。");
        }

        // 平文露出の注意（ADR-0010 Phase 4・田中のセキュリティレビュー指摘）: 閲覧リスナ（8514）は既定で
        // 平文 HTTP。Viewer:...:AdminGroups を指定すると、その所属者は閲覧リスナ上で「管理」役割の認証
        // セッション Cookie（admin_session）を得る——Cookie は host スコープゆえ管理リスナ（8515/リモート
        // 8516）へも届く。SecurePolicy=SameAsRequest（ADR-0013 決定 7）のため、平文 HTTP で発行された
        // 管理等価 Cookie は Secure 属性なしで LAN を流れる。管理操作は管理リスナ（loopback またはリモート
        // HTTPS）から行い、閲覧リスナでの管理役割付与は「閲覧のための管理⊇閲覧」に留める運用を推奨する。
        if (resolvedConfiguration.ViewerWindowsAuthEnabled && windowsGroupAuthorization.ViewerAdminGroupSids.Count > 0)
        {
            var viewerAdminGroupsLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Yagura.Host.Administration.ViewerAuth");
            viewerAdminGroupsLogger.LogWarning(
                "[viewer-admingroups-plaintext] Viewer:Authentication:Windows:AdminGroups が指定されています。" +
                "その所属者は閲覧リスナ（ポート {ViewerPort}）上で「管理」役割の認証セッション Cookie を得ます。" +
                "閲覧リスナは既定で平文 HTTP のため、この管理等価 Cookie は Secure 属性なしで LAN を流れ、host " +
                "スコープゆえ管理リスナへも届きます。機微環境では、管理役割の付与は管理リスナ（loopback または" +
                "リモート HTTPS）側で行い、閲覧リスナ経由の管理ログインは避ける運用を検討してください" +
                "（閲覧リスナ自体の HTTPS 化は今後の課題）。",
                resolvedConfiguration.HttpPort);
        }

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

    /// <summary>
    /// 設定ファイルをファイル全体として解釈できなかったことを記録する（イベント ID 1024。
    /// configuration.md §1・security.md §4.3）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>記録するのは「どのファイルの、どこで」までに留める</b>。該当行の内容や周辺の抜粋は含めない——
    /// 設定ファイルには DPAPI 暗号文や（手編集された場合は）平文の資格情報が含まれうるためである
    /// （§2「パスワード値そのものは記録しない」を読み取り失敗の文脈へ延長する）。対象ファイルの
    /// パス自体は資格情報ではないため含める（データルートを既定から変更した環境で、どのファイルを
    /// 直せばよいか分かるようにする）。
    /// </para>
    /// <para>
    /// <b>行番号は 1 始まりへ変換する</b>。<see cref="JsonException.LineNumber"/> は 0 始まりであり、
    /// そのまま出すとエディタの行番号と 1 ずれる。<see cref="JsonException.BytePositionInLine"/> は
    /// 「桁」ではなくバイト位置であり、日本語を含む行では桁として読めないため補助情報として添える。
    /// </para>
    /// </remarks>
    private static void LogConfigurationFileUnreadable(ILogger logger, string dataRoot, Exception failure)
    {
        var path = Path.Combine(dataRoot, YaguraConfigurationLoader.ConfigurationFileName);

        // 位置情報を持つ例外は入れ子の奥にある。構成システム（AddJsonFile）の場合、実測では
        // InvalidDataException → FormatException → JsonReaderException（JsonException の派生）の
        // 3 段になる（2026-07-18 確認）。段数を決め打ちせず、連鎖を辿って最初の JsonException を拾う。
        JsonException? jsonFailure = null;
        for (var current = failure; current is not null; current = current.InnerException)
        {
            if (current is JsonException found)
            {
                jsonFailure = found;
                break;
            }
        }

        var location = jsonFailure?.LineNumber is { } lineNumber
            ? $"{lineNumber + 1} 行目（バイト位置 {jsonFailure.BytePositionInLine?.ToString() ?? "不明"}）"
            : "位置不明";

        logger.LogCritical(
            ConfigurationEventIds.ConfigurationFileUnreadableStartupFailed,
            "設定ファイル {ConfigurationFile} を解釈できなかったため起動を中止します（{Location}）。{Recovery}",
            path,
            location,
            LastKnownGoodConfiguration.BuildRecoveryGuidance(dataRoot));
    }
}
