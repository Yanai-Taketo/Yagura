using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Yagura.Host.Observability.ActiveNotification.SourceSilence;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Tcp;
using Yagura.Ingestion.Tls;
using Yagura.Ingestion.Udp;
using Yagura.Storage.Spool;

namespace Yagura.Host.Configuration;

/// <summary>
/// データルート直下の JSON 設定ファイルを読み込み、検証・3 分類の適用・環境変数上書きを
/// 経て <see cref="ConfigurationLoadResult"/> を組み立てる（M3-1。configuration.md §1・§2）。
/// </summary>
/// <remarks>
/// <para>
/// <b>配置</b>: 現時点は Yagura.Host に配置する。設定モデルが増え、将来 Yagura.Web 等の
/// 他プロジェクトからも参照する必要が生じた場合は、Yagura.Configuration のような専用
/// モジュールへ切り出すことを検討する（依頼コメントのとおり。本 M3-1 時点では単一の
/// ホストプロセスからしか参照されないため、切り出しの利益がコストを上回らない）。
/// </para>
/// <para>
/// <b>優先順位</b>: 環境変数 &gt; 設定ファイル &gt; 既定値（依頼のとおり）。環境変数は
/// <c>YAGURA_DATAROOT</c> / <c>YAGURA_HTTP_PORT</c> / <c>YAGURA_UDP_PORT</c> /
/// <c>YAGURA_TCP_PORT</c>（M4-1 で追加）の 4 つを上書き手段として維持する。これらは
/// フラットな名前であり .NET 構成システムの
/// 標準 <c>AddEnvironmentVariables</c>（<c>Section__Key</c> 規約）には従わないため、
/// <see cref="IConfigurationBuilder"/> に環境変数プロバイダを追加するのではなく、
/// ファイルからバインドした値を本クラスが個別に上書きする。
/// </para>
/// <para>
/// <b>設定ファイル不在時は生成しない</b>（ゼロ設定ファーストラン）。<c>AddJsonFile(optional: true)</c>
/// によりファイル不在は無視され、既定値のみで起動する。ファイル生成は M3-3 の管轄。
/// </para>
/// </remarks>
public static class YaguraConfigurationLoader
{
    /// <summary>
    /// データルート直下に置く設定ファイル名。
    /// </summary>
    /// <remarks>
    /// <b>暫定値</b>: configuration.md §2 は設定ファイルの形式（JSON）と配置（データルート
    /// 配下）を確定しているが、具体的なファイル名までは明記していない。CF 確定待ちの
    /// 判断点として「yagura.json」を暫定名とする（本 PR の最終報告で明示する）。
    /// </remarks>
    public const string ConfigurationFileName = "yagura.json";

    /// <summary>
    /// 設定ファイル内で認識される JSON キーパス（.NET 構成システムの <c>:</c> 区切り表記）の一覧。
    /// 未知キー検出（§1）の基準集合。additive-only の起点として、キーを追加した際は
    /// 必ずこの一覧と configuration.md §8 の両方を更新すること（conventions.md 参照）。
    /// </summary>
    internal static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ingestion:Udp:BindAddress",
        "Ingestion:Udp:Port",
        "Ingestion:Udp:ReceiveBufferBytes",
        "Ingestion:Tcp:BindAddress",
        "Ingestion:Tcp:Port",
        "Ingestion:Tls:Enabled",
        "Ingestion:Tls:BindAddress",
        "Ingestion:Tls:Port",
        "Ingestion:Tls:CertificateThumbprint",
        "Ingestion:Rfc3164:DefaultTimeZone",
        "Ingestion:FlowControl:Enabled",
        "Ingestion:FlowControl:MessagesPerSecond",
        "Ingestion:FlowControl:BurstSize",
        "Viewer:HttpPort",
        "Viewer:PublicAccess",
        "Viewer:ReverseDns:Enabled",
        "Viewer:Authentication:Windows:Enabled",
        "Viewer:Authentication:Windows:KerberosOnly",
        "Admin:HttpPort",
        "Admin:Authentication:Windows:Enabled",
        "Admin:Authentication:Windows:KerberosOnly",
        "Admin:Authentication:App:Enabled",
        "Admin:Authentication:RequireForLoopback",
        "Admin:RemoteBinding:Enabled",
        "Admin:Https:Enabled",
        "Admin:Https:CertificateThumbprint",
        "Admin:Https:Port",
        "Storage:SqliteFileName",
        "Storage:Provider",
        "Storage:SqlServer:ConnectionString",
        "Spool:Enabled",
        "Spool:Directory",
        "Spool:QuotaBytes",
        "Retention:Days",
        "Retention:ExecutionTimeOfDay",
        "Audit:RetentionDays",
        "Notification:Email:Enabled",
        "Notification:Email:From",
        "Notification:Email:Smtp:Host",
        "Notification:Email:Smtp:Port",
        "Notification:Email:Smtp:Security",
        "Notification:Email:Smtp:Username",
        "Notification:Email:Smtp:Password",
        "Notification:SourceSilence:DefaultThresholdMinutes",
    };

    /// <summary>
    /// 配列（JSON 配列）としてバインドされる既知キーの一覧（SEC-9 のグループ一覧。ADR-0010 決定 5・7）。
    /// .NET 構成システムは配列を <c>&lt;key&gt;:0</c>・<c>&lt;key&gt;:1</c> … のインデックス付きリーフとして
    /// 展開するため、これらは <see cref="KnownKeys"/>（スカラーのリーフキー集合）には現れない。
    /// <see cref="DetectUnknownKeys"/> はインデックス付き子キーの親をこの集合と照合して既知判定する。
    /// <b>配列キーを追加した際は、本集合・<see cref="ConfigurationKeyMetadata.RegisteredArrayKeys"/>・
    /// <see cref="ConfigurationChangePlanner"/> の比較・configuration.md §8 の 4 箇所を同じ PR で
    /// 更新する</b>（本集合と反映方式表の双方向一致はテストで機械検証される。ADR-0017 委任 9）。
    /// </summary>
    internal static readonly HashSet<string> KnownArrayKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Admin:Authentication:Windows:AdminGroups",
        "Viewer:Authentication:Windows:ViewerGroups",
        "Viewer:Authentication:Windows:AdminGroups",
        // メール通知の宛先一覧（ADR-0017 決定 1。宛先ごとの振り分けはしない）。
        "Notification:Email:To",
    };

    /// <summary>
    /// <b>オブジェクトの</b>構造化配列キーと、その各要素が持ち得るフィールド名
    /// （ADR-0018 決定 1。本プロジェクト初のオブジェクト構造化配列キー）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="KnownArrayKeys"/>（スカラーの配列）とは平坦化の形が違う。実測（2026-07-19）:
    /// </para>
    /// <list type="bullet">
    /// <item><description>スカラー配列 <c>["a","b"]</c> → <c>key:0</c> = "a" / <c>key:1</c> = "b"</description></item>
    /// <item><description>オブジェクト配列 <c>[{"Address":"x"}]</c> → <c>key:0:Address</c> = "x"</description></item>
    /// </list>
    /// <para>
    /// 後者はリーフの親が <c>key:0</c> であり <c>key</c> ではないため、
    /// <see cref="IsKnownArrayElement"/> の「親が既知の配列キーか」という判定では既知にできない。
    /// フィールド名まで含めて照合する（<b>綴りを間違えたフィールドは未知キーとして検出される</b>
    /// ——これは望ましい: <c>Adress</c> と書いたエントリは黙って無視されるのではなく警告に現れる）。
    /// </para>
    /// </remarks>
    internal static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> KnownObjectArrayKeys =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Notification:SourceSilence:Watchlist"] =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Address", "Label", "ThresholdMinutes" },
        };

    /// <summary>
    /// データルート配下の設定ファイルを読み込み、検証済みの設定と警告一式を返す。
    /// </summary>
    /// <param name="dataRoot">データルートの絶対パス（既に解決済みであること。§2 参照）。</param>
    /// <param name="logger">警告・未知キーを起動時ログへ出力するための ILogger。</param>
    public static ConfigurationLoadResult Load(string dataRoot, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);
        ArgumentNullException.ThrowIfNull(logger);

        var configurationFilePath = Path.Combine(dataRoot, ConfigurationFileName);

        var configurationRoot = new ConfigurationBuilder()
            .SetBasePath(dataRoot)
            // optional: true によりファイル不在は例外にせず既定値のみで起動を継続する
            // （ゼロ設定ファーストラン。configuration.md §2）。
            .AddJsonFile(ConfigurationFileName, optional: true, reloadOnChange: false)
            .Build();

        var unknownKeys = DetectUnknownKeys(configurationRoot);
        foreach (var unknownKey in unknownKeys)
        {
            logger.LogWarning(
                "設定ファイル {ConfigurationFile} に未知のキー {Key} があるため無視します。",
                configurationFilePath,
                unknownKey);
        }

        // 型の読み替え検出（Issue #334）: 平坦化後の値からはトークン型を復元できないため、
        // 元ファイルを JsonDocument として別途走査する。報告対象の絞り込み（不正値警告・
        // 未知キーとの重複除外）は警告の収集が終わった Load の末尾で行う。
        var allTypeCoercions = DetectTypeCoercions(configurationFilePath);

        var options = new YaguraConfigurationOptions();
        configurationRoot.Bind(options);

        var warnings = new List<ConfigurationWarning>();

        // --- 受信: UDP bind アドレス（§1「縮小側で継続」） ---
        var (udpBindAddress, udpBindAddressIsExplicit) = ResolveUdpBindAddress(options, warnings);

        // --- 受信: UDP ポート（§1「起動失敗」——受信の成立に不可欠なキー） ---
        var udpPort = ResolveUdpPort(options);

        // --- 受信: UDP 受信バッファサイズ（§1「既定値で継続」。M-2） ---
        var udpReceiveBufferBytes = ResolveUdpReceiveBufferBytes(options, warnings);

        // --- 受信: TCP bind アドレス（§1「縮小側で継続」。UDP と同じ分類。M4-1） ---
        var (tcpBindAddress, tcpBindAddressIsExplicit) = ResolveTcpBindAddress(options, warnings);

        // --- 受信: TCP ポート（§1「起動失敗」——UDP と同じ分類。M4-1） ---
        var tcpPort = ResolveTcpPort(options);

        // --- 受信: TLS 受信 opt-in（RFC 5425。security.md §6。Issue #137）。§1「縮小側で継続」——
        //     TLS 受信は opt-in 機能であり、不正値・未解決の証明書は無効側・非稼働側へ倒す
        //     （fail-closed の起動拒否は行わない。実際の証明書ストア参照の成否は Program 側で
        //     確認し、開けなければリスナ 1 本のみ縮小継続する——Admin:Https と同じ二段構え） ---
        var ingestionTlsEnabled = ResolveSecurityFlag(options.Ingestion?.Tls?.Enabled, "Ingestion:Tls:Enabled", warnings);
        var (ingestionTlsBindAddress, ingestionTlsBindAddressIsExplicit) = ResolveIngestionTlsBindAddress(options, warnings);
        var ingestionTlsPort = ResolveIngestionTlsPort(options, warnings);
        var ingestionTlsCertificateThumbprint = NormalizeCertificateThumbprintOrNull(
            options.Ingestion?.Tls?.CertificateThumbprint,
            "Ingestion:Tls:CertificateThumbprint",
            warnings,
            "TLS 受信は未構成のまま扱います（Ingestion:Tls:Enabled が true の場合、Program 起動時に" +
                "縮小継続の警告として記録されます）");

        // --- 受信: RFC 3164 TIMESTAMP の既定タイムゾーン（§1「既定値で継続」。Issue #134） ---
        var defaultRfc3164TimeZone = ResolveDefaultRfc3164TimeZone(options, warnings);

        // --- 流量制御: 有効/無効・送信元別閾値（§1「既定値で継続」。ADR-0002 決定 2。Issue #260） ---
        var flowControlEnabled = ResolveFlowControlEnabled(options, warnings);
        var flowControlMessagesPerSecond = ResolveFlowControlMessagesPerSecond(options, warnings);
        var flowControlBurstSize = ResolveFlowControlBurstSize(options, warnings);

        // --- UI: 閲覧 HTTP ポート（§1「既定値で継続」） ---
        var httpPort = ResolveHttpPort(options, warnings);

        // --- UI: 閲覧リスナの公開範囲（§1「縮小側で継続」。M6-1） ---
        var viewerPublicAccess = ResolveViewerPublicAccess(options, warnings);

        // --- UI: 逆引きホスト名表示の有効/無効（§1「縮小側で継続」。ADR-0007） ---
        var viewerReverseDnsEnabled = ResolveViewerReverseDnsEnabled(options, warnings);

        // --- UI: 閲覧 UI 認証（ADR-0010 Phase 4 決定 7・SEC-9。opt-in。§1「縮小側で継続」——
        //     不正値は無効側へ倒す。既定は現状維持＝認証なし・LAN 公開） ---
        var viewerWindowsAuthEnabled = ResolveSecurityFlag(
            options.Viewer?.Authentication?.Windows?.Enabled, "Viewer:Authentication:Windows:Enabled", warnings);
        var viewerWindowsAuthKerberosOnly = ResolveSecurityFlag(
            options.Viewer?.Authentication?.Windows?.KerberosOnly, "Viewer:Authentication:Windows:KerberosOnly", warnings);
        var viewerWindowsViewerGroups = ResolveGroupSpecs(options.Viewer?.Authentication?.Windows?.ViewerGroups);
        var viewerWindowsAdminGroups = ResolveGroupSpecs(options.Viewer?.Authentication?.Windows?.AdminGroups);

        // --- UI: 管理 HTTP ポート（§1「既定値で継続」。bind 先は常に loopback 固定。M6-1） ---
        var adminHttpPort = ResolveAdminHttpPort(options, warnings);

        // --- UI: 管理 UI 認証（ADR-0010 Phase 1。opt-in。§1「縮小側で継続」——
        //     公開範囲・bind 先・認証関連のセキュリティ項目は不正値で開放側へ落とさない） ---
        var adminWindowsAuthEnabled = ResolveSecurityFlag(
            options.Admin?.Authentication?.Windows?.Enabled, "Admin:Authentication:Windows:Enabled", warnings);
        var adminWindowsAuthKerberosOnly = ResolveSecurityFlag(
            options.Admin?.Authentication?.Windows?.KerberosOnly, "Admin:Authentication:Windows:KerberosOnly", warnings);
        // SEC-9: 「管理」役割にマップする AD グループの生指定（名/SID）。名→SID 解決は Windows 専用の
        //        ため Program 起動時に行う。ここでは形の正規化（空要素除去・重複排除）のみ。
        var adminWindowsAdminGroups = ResolveGroupSpecs(options.Admin?.Authentication?.Windows?.AdminGroups);
        var adminAppAuthEnabled = ResolveSecurityFlag(
            options.Admin?.Authentication?.App?.Enabled, "Admin:Authentication:App:Enabled", warnings);
        var adminAuthRequireForLoopback = ResolveSecurityFlag(
            options.Admin?.Authentication?.RequireForLoopback, "Admin:Authentication:RequireForLoopback", warnings);

        // --- UI: 管理リスナのリモートバインド・HTTPS（ADR-0010 Phase 2。opt-in。§1「縮小側で継続」——
        //     公開範囲・bind 先・認証関連のセキュリティ項目は不正値で開放側へ落とさない） ---
        var adminRemoteBindingEnabled = ResolveSecurityFlag(
            options.Admin?.RemoteBinding?.Enabled, "Admin:RemoteBinding:Enabled", warnings);
        var adminHttpsEnabled = ResolveSecurityFlag(
            options.Admin?.Https?.Enabled, "Admin:Https:Enabled", warnings);
        var adminHttpsCertificateThumbprint = ResolveAdminHttpsCertificateThumbprint(options, warnings);
        var adminHttpsPort = ResolveAdminHttpsPort(options, warnings);

        // --- fail-closed 不変条件（ADR-0010 Phase 2 決定 1・4。リモートバインドの解禁は
        //     「認証が有効」かつ「HTTPS が構成済み」の両方を前提条件とする。configuration.md §1
        //     「縮小側で継続」ではなく「起動失敗」に分類する——既存 L-4（リモートバインドの
        //     fail-closed）・ADR-0010 Phase 1 の loopback 認証 opt-in fail-closed と対称の扱い。
        //     ここで検証できるのは設定の静的な形（フラグ・拇印の形式）のみであり、拇印が実際に
        //     証明書ストアで解決できるかどうかは Program 側で確認する（縮小側の扱い——下記
        //     ConfigurationEventIds.AdminHttpsCertificateUnavailableAtStartup 参照）) ---
        if (adminRemoteBindingEnabled)
        {
            var authenticationConfigured = adminWindowsAuthEnabled || adminAppAuthEnabled;
            var httpsConfigured = adminHttpsEnabled && adminHttpsCertificateThumbprint is not null;

            if (!authenticationConfigured || !httpsConfigured)
            {
                var missing = new List<string>();
                if (!authenticationConfigured)
                {
                    missing.Add("認証方式（Admin:Authentication:Windows:Enabled または Admin:Authentication:App:Enabled）");
                }

                if (!httpsConfigured)
                {
                    missing.Add("HTTPS（Admin:Https:Enabled = true と有効な Admin:Https:CertificateThumbprint）");
                }

                throw new ConfigurationValidationException(
                    "Admin:RemoteBinding:Enabled が有効ですが、次の前提条件が満たされていません: " +
                    string.Join(" / ", missing) + "。" +
                    "この組み合わせのまま起動すると、認証または通信保護のいずれかを欠いた状態で" +
                    "管理リスナが loopback 以外へ束縛されてしまいます（ADR-0010 Phase 2 決定 1・4 の" +
                    "fail-closed 不変条件）。上記の前提条件をすべて満たすか、Admin:RemoteBinding:Enabled を" +
                    "false に戻してから再起動してください。",
                    ConfigurationEventIds.AdminRemoteBindingFailClosedStartupRejected);
            }
        }

        // --- fail-closed 不変条件（ADR-0010 決定 1・委任事項 5。configuration.md §1 の
        //     「縮小側で継続」ではなく「起動失敗」に分類する——リモートバインドの fail-closed
        //     （configuration.md §1 の既存 L-4 不変条件）と対称の扱い。「loopback 認証 opt-in が
        //     有効なのに認証方式が一つも構成されていない」は、認証手段が存在しないまま
        //     loopback にも認証を要求してしまい、佐藤・鈴木の両ペルソナが最重要視した
        //     「最終復旧経路」を誤設定 1 つで自壊させるため、既定値へのフォールバック
        //     （§1「縮小側で継続」の通常運用）ではなく起動そのものを止める） ---
        if (adminAuthRequireForLoopback && !adminWindowsAuthEnabled && !adminAppAuthEnabled)
        {
            throw new ConfigurationValidationException(
                "Admin:Authentication:RequireForLoopback が有効ですが、認証方式" +
                "（Admin:Authentication:Windows:Enabled / Admin:Authentication:App:Enabled）が" +
                "一つも有効になっていません。この組み合わせのまま起動すると、認証手段が存在しないのに" +
                "loopback 経由の管理操作にも認証が要求され、管理 UI へ一切到達できなくなります" +
                "（ADR-0010 決定 1 の fail-closed 不変条件）。" +
                "Admin:Authentication:Windows:Enabled または Admin:Authentication:App:Enabled の" +
                "少なくとも一方を true にするか、Admin:Authentication:RequireForLoopback を" +
                "false に戻してから再起動してください。",
                ConfigurationEventIds.AdminAuthenticationFailClosedStartupRejected);
        }

        // --- 永続化: SQLite ファイル名（§1「既定値で継続」） ---
        var sqliteFileName = ResolveSqliteFileName(options, warnings);

        // --- 永続化: provider 選択・SQL Server 接続文字列（§1「既定値で継続」。M5-3） ---
        var (storageProvider, sqlServerConnectionString) = ResolveStorageProvider(options, warnings);

        // --- スプール: 有効/無効・置き場所・上限（§1「既定値で継続」。M4-3） ---
        var spoolEnabled = ResolveSpoolEnabled(options, warnings);
        var spoolDirectory = ResolveSpoolDirectory(options, dataRoot, warnings);
        var spoolQuotaBytes = ResolveSpoolQuotaBytes(options, warnings);

        // --- 保持期間: 日数・実行時間帯（§1「既定値で継続」。M5-1） ---
        var retentionDays = ResolveRetentionDays(options, warnings);
        var retentionExecutionTimeOfDay = ResolveRetentionExecutionTimeOfDay(options, warnings);

        // --- 監査: 保持期間（SEC-2。security.md §4.2。Issue #261） ---
        var auditRetentionDays = ResolveAuditRetentionDays(options, warnings);

        // --- 能動通知: メール（ADR-0017。opt-in・既定無効。Issue #350） ---
        var emailNotification = ResolveEmailNotification(options, warnings);

        // --- 能動通知: 送信元の途絶検知（ADR-0018。opt-in・既定無効。Issue #351） ---
        var sourceSilence = ResolveSourceSilence(options, warnings, logger);

        foreach (var warning in warnings)
        {
            logger.LogWarning(
                "設定キー {Key} の値 {InvalidValue} は不正のため既定/安全側の値 {AppliedValue} を適用しました（{Reason}）。",
                warning.Key,
                warning.InvalidValue,
                warning.AppliedValue,
                warning.Reason);
        }

        var resolved = new ResolvedYaguraConfiguration(
            DataRoot: dataRoot,
            UdpBindAddress: udpBindAddress,
            UdpPort: udpPort,
            UdpReceiveBufferBytes: udpReceiveBufferBytes,
            TcpBindAddress: tcpBindAddress,
            TcpPort: tcpPort,
            DefaultRfc3164TimeZone: defaultRfc3164TimeZone,
            HttpPort: httpPort,
            ViewerPublicAccess: viewerPublicAccess,
            ViewerReverseDnsEnabled: viewerReverseDnsEnabled,
            ViewerWindowsAuthEnabled: viewerWindowsAuthEnabled,
            ViewerWindowsAuthKerberosOnly: viewerWindowsAuthKerberosOnly,
            ViewerWindowsViewerGroups: viewerWindowsViewerGroups,
            ViewerWindowsAdminGroups: viewerWindowsAdminGroups,
            AdminHttpPort: adminHttpPort,
            AdminWindowsAuthEnabled: adminWindowsAuthEnabled,
            AdminWindowsAuthKerberosOnly: adminWindowsAuthKerberosOnly,
            AdminWindowsAdminGroups: adminWindowsAdminGroups,
            AdminAppAuthEnabled: adminAppAuthEnabled,
            AdminAuthRequireForLoopback: adminAuthRequireForLoopback,
            AdminRemoteBindingEnabled: adminRemoteBindingEnabled,
            AdminHttpsEnabled: adminHttpsEnabled,
            AdminHttpsCertificateThumbprint: adminHttpsCertificateThumbprint,
            AdminHttpsPort: adminHttpsPort,
            SqliteFileName: sqliteFileName,
            SpoolEnabled: spoolEnabled,
            SpoolDirectory: spoolDirectory,
            SpoolQuotaBytes: spoolQuotaBytes,
            RetentionDays: retentionDays,
            RetentionExecutionTimeOfDay: retentionExecutionTimeOfDay,
            StorageProvider: storageProvider,
            SqlServerConnectionString: sqlServerConnectionString,
            IngestionTlsEnabled: ingestionTlsEnabled,
            IngestionTlsBindAddress: ingestionTlsBindAddress,
            IngestionTlsPort: ingestionTlsPort,
            IngestionTlsCertificateThumbprint: ingestionTlsCertificateThumbprint,
            FlowControlEnabled: flowControlEnabled,
            FlowControlMessagesPerSecond: flowControlMessagesPerSecond,
            FlowControlBurstSize: flowControlBurstSize,
            AuditRetentionDays: auditRetentionDays)
        {
            // bind アドレスの明示指定フラグ（PR #193 レビュー対応。IPv6 不可の環境での
            // 「既定は IPv4 縮小 / 明示は fail-fast」の分岐の入力——受信段へ引き渡す）。
            UdpBindAddressIsExplicit = udpBindAddressIsExplicit,
            TcpBindAddressIsExplicit = tcpBindAddressIsExplicit,
            IngestionTlsBindAddressIsExplicit = ingestionTlsBindAddressIsExplicit,
            EmailNotification = emailNotification,
            SourceSilence = sourceSilence,
        };

        // 型の読み替えの報告対象（Issue #334）: 不正値の警告・未知キーの警告が既に出るキーは
        // 情報一覧から除外する——同じキーを二重に報告しない。§1 は情報レベルの対象を「意図が
        // 一意に読み取れる型の読み替え」に限っており、不正値と判定された値（例: "Enabled": 1）は
        // 既存の警告 3 点（キー・不正値・適用値）が正本になる。
        var reportedElsewhereKeys = warnings.Select(w => w.Key)
            .Concat(unknownKeys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var typeCoercions = allTypeCoercions
            .Where(coercion => !reportedElsewhereKeys.Contains(coercion.Key))
            .ToArray();

        foreach (var coercion in typeCoercions)
        {
            // 警告ではなく情報（§1——受理は正常系。警告の感度を落とさない）。
            logger.LogInformation(
                "設定ファイル {ConfigurationFile} のキー {Key} は JSON の{JsonType}で書かれているため、文字列として受理しました（適用値: {AppliedValue}）。",
                configurationFilePath,
                coercion.Key,
                coercion.JsonType,
                coercion.AppliedValue);
        }

        return new ConfigurationLoadResult(resolved, warnings, unknownKeys, typeCoercions);
    }

    /// <summary>
    /// データルートを解決する（環境変数 <see cref="YaguraHostEnvironment.DataRootEnvironmentVariable"/>
    /// &gt; 既定値 <c>%ProgramData%\Yagura</c>）。設定ファイル自体の置き場所を決める入力のため、
    /// ファイル内キーの対象にはしない（configuration.md §2）。
    /// </summary>
    public static string ResolveDataRoot()
    {
        var overridden = Environment.GetEnvironmentVariable(YaguraHostEnvironment.DataRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "Yagura");
    }

    /// <summary>
    /// 設定ファイルを <see cref="JsonDocument"/> として走査し、スカラー位置に数値・真偽値の
    /// トークンが現れたキー（型の読み替え。configuration.md §1。Issue #334）を収集する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 構成システム（<c>AddJsonFile</c>）は平坦化の際にトークン型を捨てるため、平坦化後の値からは
    /// <c>4194304</c> と <c>"4194304"</c> を区別できない——元のトークン型を見られる読み手をここに置く
    /// （§1 の制約 1）。受理範囲は構成システムに合わせ、末尾カンマ・コメントを受理する
    /// （<see cref="YaguraConfigurationWriter"/> の DeserializerOptions と同じ根拠）。
    /// </para>
    /// <para>
    /// ファイルが読めない・解析できない場合は空を返す——読み取り・解析の失敗の警告・起動失敗は
    /// 既存経路（イベント ID 1024・1021）の管轄であり、情報表示のためだけの本走査から新しい
    /// 失敗様式を作らない。
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<ConfigurationTypeCoercion> DetectTypeCoercions(string configurationFilePath)
    {
        string text;
        try
        {
            if (!File.Exists(configurationFilePath))
            {
                return [];
            }

            // 文字コードは StreamReader の BOM 自動判別でデコードする（YaguraConfigurationWriter.Read と
            // 同じ機構——Issue #344 / #389）。バイト列を直接 JsonDocument.Parse へ渡すと、両読み手が
            // 受理する UTF-16 BOM 付きファイルで本走査だけが JsonException → 空振りし、受理される
            // ファイルなのに型読み替えの情報表示が無音で欠ける。
            using var reader = new StreamReader(configurationFilePath);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(
                text,
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

            var coercions = new List<ConfigurationTypeCoercion>();
            CollectTypeCoercions(document.RootElement, path: null, coercions);
            return coercions;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void CollectTypeCoercions(
        JsonElement element, string? path, List<ConfigurationTypeCoercion> coercions)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectTypeCoercions(
                        property.Value,
                        path is null ? property.Name : $"{path}:{property.Name}",
                        coercions);
                }

                break;

            case JsonValueKind.Array when path is not null:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    // 配列要素は構成システムの平坦化と同じインデックス付きパス（key:0 等）で表す。
                    CollectTypeCoercions(item, $"{path}:{index}", coercions);
                    index++;
                }

                break;

            case JsonValueKind.Number when path is not null:
                // 表記のまま保つ（514.0 を "514" に正規化しない——ConfigurationValueStringConverter と同じ）。
                coercions.Add(new ConfigurationTypeCoercion(path, "数値", element.GetRawText()));
                break;

            case JsonValueKind.True or JsonValueKind.False when path is not null:
                // 構成システムの平坦化結果と同じ表記（"True" / "False"。実測は Issue #312）。
                coercions.Add(new ConfigurationTypeCoercion(
                    path, "真偽値", element.ValueKind == JsonValueKind.True ? bool.TrueString : bool.FalseString));
                break;
        }
    }

    private static IReadOnlyList<string> DetectUnknownKeys(IConfigurationRoot configurationRoot)
    {
        var unknown = new List<string>();

        foreach (var entry in configurationRoot.AsEnumerable())
        {
            // AsEnumerable は中間ノード（値を持たないセクション）も列挙するため、
            // 実際に値を持つリーフキーのみを未知キー判定の対象にする。
            if (entry.Value is null)
            {
                continue;
            }

            // 空の JSON 配列（"To": []）は要素を 1 つも展開せず、配列キー自身がリーフとして
            // 現れる（値は空文字）。これを未知キー扱いにすると「全ての宛先を消した」「グループ
            // 指定を空にした」という正当な編集が、綴り間違いと同じ警告として出てしまうため、
            // 配列キー自身も既知として扱う（Issue #350 で顕在化。グループ一覧も同じ性質を持つ）。
            if (KnownKeys.Contains(entry.Key)
                || KnownArrayKeys.Contains(entry.Key)
                || KnownObjectArrayKeys.ContainsKey(entry.Key)
                || IsKnownArrayElement(entry.Key)
                || IsKnownObjectArrayField(entry.Key))
            {
                continue;
            }

            unknown.Add(entry.Key);
        }

        return unknown;
    }

    /// <summary>
    /// <paramref name="key"/> が既知の配列キー（<see cref="KnownArrayKeys"/>）のインデックス付き要素
    /// （<c>&lt;arrayKey&gt;:&lt;整数&gt;</c>）かどうか。SEC-9 のグループ一覧を未知キー扱いにしないための判定。
    /// </summary>
    private static bool IsKnownArrayElement(string key)
    {
        var lastSeparator = key.LastIndexOf(':');
        if (lastSeparator <= 0 || lastSeparator == key.Length - 1)
        {
            return false;
        }

        var indexPart = key.AsSpan(lastSeparator + 1);
        foreach (var c in indexPart)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        var parentKey = key[..lastSeparator];
        return KnownArrayKeys.Contains(parentKey);
    }

    /// <summary>
    /// <paramref name="key"/> が既知のオブジェクト構造化配列キーの要素フィールド
    /// （<c>&lt;arrayKey&gt;:&lt;整数&gt;:&lt;フィールド名&gt;</c>）かどうか。
    /// </summary>
    /// <remarks>
    /// フィールド名まで照合するため、<b>綴りを間違えたフィールドは未知キーとして検出される</b>。
    /// これは意図した挙動である——<c>Adress</c> と書いたエントリが黙って無視される
    /// （＝監視されているつもりで監視されていない）のを防ぐ。
    /// </remarks>
    private static bool IsKnownObjectArrayField(string key)
    {
        var lastSeparator = key.LastIndexOf(':');
        if (lastSeparator <= 0 || lastSeparator == key.Length - 1)
        {
            return false;
        }

        var fieldName = key[(lastSeparator + 1)..];
        var indexedParent = key[..lastSeparator];

        var indexSeparator = indexedParent.LastIndexOf(':');
        if (indexSeparator <= 0 || indexSeparator == indexedParent.Length - 1)
        {
            return false;
        }

        foreach (var c in indexedParent.AsSpan(indexSeparator + 1))
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        var arrayKey = indexedParent[..indexSeparator];
        return KnownObjectArrayKeys.TryGetValue(arrayKey, out var fields) && fields.Contains(fieldName);
    }

    /// <summary>
    /// UDP bind アドレスを解決する。環境変数による上書きは現時点で提供しない
    /// （既存 3 環境変数のみ維持。依頼範囲外）。不正値は §1「縮小側で継続」により
    /// loopback（127.0.0.1）へ縮小する。
    /// </summary>
    /// <returns>
    /// 解決済みアドレスと、キーが明示指定されていたか（PR #193 レビュー対応——IPv6 不可の
    /// 環境で「既定の <c>::</c> は IPv4 縮小 / 明示の <c>::</c> は fail-fast」を分けるための入力）。
    /// </returns>
    private static (string Address, bool IsExplicit) ResolveUdpBindAddress(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        var raw = options.Ingestion?.Udp?.BindAddress;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (UdpSyslogListenerOptions.DefaultBindAddress, IsExplicit: false);
        }

        // IPAddress として解釈できる値を受け入れる（形式不正のみ縮小対象）。
        // ワイルドカードの意味づけ（Issue #133・configuration.md §4.1）: 既定の "::" は
        // DualMode による IPv4/IPv6 両受信、明示の "0.0.0.0" は IPv4 専用（後方互換の
        // 逃げ道）——解釈は受信段（DualStackBindAddress）が行い、本メソッドは形式検証のみ担う。
        if (IPAddress.TryParse(raw, out _))
        {
            return (raw, IsExplicit: true);
        }

        warnings.Add(new ConfigurationWarning(
            Key: "Ingestion:Udp:BindAddress",
            InvalidValue: raw,
            AppliedValue: IPAddress.Loopback.ToString(),
            Reason: "bind 先アドレスの形式が不正なため安全側（loopback）へ縮小"));

        return (IPAddress.Loopback.ToString(), IsExplicit: true);
    }

    /// <summary>
    /// UDP 受信ポートを解決する（環境変数 <see cref="YaguraHostEnvironment.UdpPortEnvironmentVariable"/>
    /// が最優先）。不正値は §1「起動失敗」——受信の成立に不可欠なキーであるため、既定値へ
    /// フォールバックせず <see cref="ConfigurationValidationException"/> を送出する。
    /// </summary>
    private static int ResolveUdpPort(YaguraConfigurationOptions options)
    {
        var envOverride = Environment.GetEnvironmentVariable(YaguraHostEnvironment.UdpPortEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            return ParsePortOrThrow(envOverride, "Ingestion:Udp:Port", "環境変数 " + YaguraHostEnvironment.UdpPortEnvironmentVariable);
        }

        var raw = options.Ingestion?.Udp?.Port;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return UdpSyslogListenerOptions.DefaultPort;
        }

        return ParsePortOrThrow(raw, "Ingestion:Udp:Port", "設定ファイル");
    }

    /// <summary>
    /// UDP 受信ソケットの受信バッファサイズ（バイト）を解決する（M-2。§1「既定値で継続」——
    /// 受信バッファの拡大は OS 側ロス緩和の改善レバーであって受信の成立に不可欠ではないため、
    /// 不正値は既定値（<see cref="UdpSyslogListenerOptions.DefaultReceiveBufferBytes"/>）へ
    /// フォールバックし警告する）。下限
    /// （<see cref="UdpSyslogListenerOptions.MinReceiveBufferBytes"/>）未満・上限
    /// （<see cref="UdpSyslogListenerOptions.MaxReceiveBufferBytes"/>）超過も不正値として扱う。
    /// </summary>
    private static int ResolveUdpReceiveBufferBytes(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        var defaultBytes = UdpSyslogListenerOptions.DefaultReceiveBufferBytes;

        var raw = options.Ingestion?.Udp?.ReceiveBufferBytes;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultBytes;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes)
            && bytes >= UdpSyslogListenerOptions.MinReceiveBufferBytes
            && bytes <= UdpSyslogListenerOptions.MaxReceiveBufferBytes)
        {
            return bytes;
        }

        warnings.Add(new ConfigurationWarning(
            Key: "Ingestion:Udp:ReceiveBufferBytes",
            InvalidValue: raw,
            AppliedValue: defaultBytes.ToString(CultureInfo.InvariantCulture),
            Reason: $"バイト数として不正、または許容範囲（{UdpSyslogListenerOptions.MinReceiveBufferBytes}〜" +
                $"{UdpSyslogListenerOptions.MaxReceiveBufferBytes}）外のため既定値を適用"));

        return defaultBytes;
    }

    /// <summary>
    /// TCP bind アドレスを解決する（M4-1）。UDP と同じ分類（§1「縮小側で継続」）を適用する:
    /// 環境変数による上書きは現時点で提供しない（既存方針を踏襲）。不正値は loopback へ縮小する。
    /// 戻り値の意味は <see cref="ResolveUdpBindAddress"/> と同一。
    /// </summary>
    private static (string Address, bool IsExplicit) ResolveTcpBindAddress(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        var raw = options.Ingestion?.Tcp?.BindAddress;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (TcpSyslogListenerOptions.DefaultBindAddress, IsExplicit: false);
        }

        // ワイルドカードの意味づけは UDP 側と同一（Issue #133。ResolveUdpBindAddress のコメント参照）。
        if (IPAddress.TryParse(raw, out _))
        {
            return (raw, IsExplicit: true);
        }

        warnings.Add(new ConfigurationWarning(
            Key: "Ingestion:Tcp:BindAddress",
            InvalidValue: raw,
            AppliedValue: IPAddress.Loopback.ToString(),
            Reason: "bind 先アドレスの形式が不正なため安全側（loopback）へ縮小"));

        return (IPAddress.Loopback.ToString(), IsExplicit: true);
    }

    /// <summary>
    /// TCP 受信ポートを解決する（環境変数 <see cref="YaguraHostEnvironment.TcpPortEnvironmentVariable"/>
    /// が最優先。M4-1）。UDP と同じ分類（§1「起動失敗」——受信の成立に不可欠なキー）を適用する。
    /// </summary>
    private static int ResolveTcpPort(YaguraConfigurationOptions options)
    {
        var envOverride = Environment.GetEnvironmentVariable(YaguraHostEnvironment.TcpPortEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            return ParsePortOrThrow(envOverride, "Ingestion:Tcp:Port", "環境変数 " + YaguraHostEnvironment.TcpPortEnvironmentVariable);
        }

        var raw = options.Ingestion?.Tcp?.Port;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TcpSyslogListenerOptions.DefaultPort;
        }

        return ParsePortOrThrow(raw, "Ingestion:Tcp:Port", "設定ファイル");
    }

    /// <summary>
    /// TLS 受信（RFC 5425。opt-in。Issue #137）の bind アドレスを解決する。TCP と同じ分類
    /// （§1「縮小側で継続」）を適用する。戻り値の意味は <see cref="ResolveTcpBindAddress"/> と同一。
    /// </summary>
    private static (string Address, bool IsExplicit) ResolveIngestionTlsBindAddress(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        var raw = options.Ingestion?.Tls?.BindAddress;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (TlsSyslogListenerOptions.DefaultBindAddress, IsExplicit: false);
        }

        if (IPAddress.TryParse(raw, out _))
        {
            return (raw, IsExplicit: true);
        }

        warnings.Add(new ConfigurationWarning(
            Key: "Ingestion:Tls:BindAddress",
            InvalidValue: raw,
            AppliedValue: IPAddress.Loopback.ToString(),
            Reason: "bind 先アドレスの形式が不正なため安全側（loopback）へ縮小"));

        return (IPAddress.Loopback.ToString(), IsExplicit: true);
    }

    /// <summary>
    /// TLS 受信ポートを解決する（環境変数 <see cref="YaguraHostEnvironment.IngestionTlsPortEnvironmentVariable"/>
    /// が最優先。Issue #137）。§1「既定値で継続」——TLS 受信は opt-in であり、平文受信の成立には
    /// 不可欠ではないため、UDP/TCP ポート（§1「起動失敗」）とは分類を分ける。既定 6514（RFC 5425）。
    /// <see cref="ResolveAdminHttpsPort"/> と同じ「不正値は既定値へフォールカックし警告する」構造。
    /// </summary>
    private static int ResolveIngestionTlsPort(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        var defaultPort = TlsSyslogListenerOptions.DefaultPort;

        var envOverride = Environment.GetEnvironmentVariable(YaguraHostEnvironment.IngestionTlsPortEnvironmentVariable);
        var envIsSet = !string.IsNullOrWhiteSpace(envOverride);

        if (envIsSet
            && int.TryParse(envOverride, NumberStyles.Integer, CultureInfo.InvariantCulture, out var envPort)
            && IsValidPort(envPort))
        {
            return envPort;
        }

        var raw = options.Ingestion?.Tls?.Port;
        int portFromFileOrDefault;
        if (string.IsNullOrWhiteSpace(raw))
        {
            portFromFileOrDefault = defaultPort;
        }
        else if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && IsValidPort(port))
        {
            portFromFileOrDefault = port;
        }
        else
        {
            warnings.Add(new ConfigurationWarning(
                Key: "Ingestion:Tls:Port",
                InvalidValue: raw,
                AppliedValue: defaultPort.ToString(CultureInfo.InvariantCulture),
                Reason: "ポート番号として不正なため既定値を適用"));
            portFromFileOrDefault = defaultPort;
        }

        if (envIsSet)
        {
            warnings.Add(new ConfigurationWarning(
                Key: YaguraHostEnvironment.IngestionTlsPortEnvironmentVariable,
                InvalidValue: envOverride!,
                AppliedValue: portFromFileOrDefault.ToString(CultureInfo.InvariantCulture),
                Reason: "環境変数の値がポート番号として不正なため設定ファイル値/既定値を適用"));
        }

        return portFromFileOrDefault;
    }

    /// <summary>
    /// RFC 3164 TIMESTAMP の既定タイムゾーンを解決する（Issue #134。§1「既定値で継続」——
    /// 受信の成立に不可欠なキーではなく、DeviceTimestamp は参考情報であるため）。
    /// </summary>
    /// <remarks>
    /// 値は Windows タイムゾーン ID（例: <c>Tokyo Standard Time</c>）・IANA タイムゾーン ID
    /// （例: <c>Asia/Tokyo</c>）のいずれも受理する——<see cref="TimeZoneInfo.FindSystemTimeZoneById"/>
    /// が .NET 6 以降 Windows 上でも両方の ID 体系を解決できるため（実機検証:
    /// <c>Yagura.Ingestion.Tests</c> の <c>SyslogParserRfc3164TimeZoneTests</c>）。未設定時・
    /// 解決できない ID は UTC（現状互換）へフォールバックする。
    /// </remarks>
    private static TimeZoneInfo ResolveDefaultRfc3164TimeZone(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        var raw = options.Ingestion?.Rfc3164?.DefaultTimeZone;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(raw);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            warnings.Add(new ConfigurationWarning(
                Key: "Ingestion:Rfc3164:DefaultTimeZone",
                InvalidValue: raw,
                AppliedValue: TimeZoneInfo.Utc.Id,
                Reason: "Windows/IANA タイムゾーン ID として解決できないため既定値（UTC）を適用"));

            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>
    /// 閲覧 HTTP ポートを解決する（環境変数 <see cref="YaguraHostEnvironment.HttpPortEnvironmentVariable"/>
    /// が最優先）。不正値は §1「既定値で継続」——閲覧リスナは受信の成立に不可欠ではないため、
    /// フォールバックし警告を収集する。§1 の「キー・不正値・適用値の 3 点を明示する」要求は
    /// 値の供給源を問わないため、環境変数の不正値も設定ファイル値と同様に警告収集する
    /// （黙った縮退を作らない）。
    /// </summary>
    private static int ResolveHttpPort(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        var envOverride = Environment.GetEnvironmentVariable(YaguraHostEnvironment.HttpPortEnvironmentVariable);
        var envIsSet = !string.IsNullOrWhiteSpace(envOverride);

        if (envIsSet
            && int.TryParse(envOverride, NumberStyles.Integer, CultureInfo.InvariantCulture, out var envPort)
            && IsValidPort(envPort))
        {
            // 有効な環境変数が最優先。この場合、設定ファイル値は適用されない（shadowed）ため
            // ファイル値の検証・警告も行わない（「適用していない値」への警告 3 点は
            // 「適用した値」の報告として不正確になるため）。
            return envPort;
        }

        // 環境変数が未設定または不正な場合のフォールバック先を解決する
        // （設定ファイル値 → 既定値。ファイル値の不正はこの中で警告収集される）。
        var portFromFileOrDefault = ResolveHttpPortFromFileOrDefault(options, warnings);

        if (envIsSet)
        {
            warnings.Add(new ConfigurationWarning(
                Key: YaguraHostEnvironment.HttpPortEnvironmentVariable,
                InvalidValue: envOverride!,
                AppliedValue: portFromFileOrDefault.ToString(CultureInfo.InvariantCulture),
                Reason: "環境変数の値がポート番号として不正なため設定ファイル値/既定値を適用"));
        }

        return portFromFileOrDefault;
    }

    /// <summary>
    /// 閲覧 HTTP ポートのうち「設定ファイル値 → 既定値」の部分を解決する
    /// （環境変数を考慮しない）。設定ファイル値の不正はここで警告収集する。
    /// </summary>
    private static int ResolveHttpPortFromFileOrDefault(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        var raw = options.Viewer?.HttpPort;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return YaguraHostEnvironment.DefaultHttpPort;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && IsValidPort(port))
        {
            return port;
        }

        warnings.Add(new ConfigurationWarning(
            Key: "Viewer:HttpPort",
            InvalidValue: raw,
            AppliedValue: YaguraHostEnvironment.DefaultHttpPort.ToString(CultureInfo.InvariantCulture),
            Reason: "ポート番号として不正なため既定値を適用"));

        return YaguraHostEnvironment.DefaultHttpPort;
    }

    /// <summary>
    /// 閲覧リスナの公開範囲を解決する（M6-1。configuration.md §4.2・§1）。
    /// </summary>
    /// <remarks>
    /// <b>不正値の扱いは「縮小側で継続」</b>: 公開範囲はセキュリティ上の縮小対象キー
    /// （configuration.md §1「公開範囲・bind 先・認証関連のセキュリティ項目は、不正値のとき
    /// 製品既定（開放側）へ落とさない」）であるため、他キーの「既定値で継続」（=製品既定へ戻す）
    /// とは異なり、既定が <see cref="ViewerPublicAccess.Lan"/>（開放側）であっても不正値の
    /// フォールバック先は必ず <see cref="ViewerPublicAccess.LocalhostOnly"/>（より狭い側）とする。
    /// </remarks>
    private static ViewerPublicAccess ResolveViewerPublicAccess(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        var raw = options.Viewer?.PublicAccess;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ViewerPublicAccess.Lan;
        }

        if (string.Equals(raw, "Lan", StringComparison.OrdinalIgnoreCase))
        {
            return ViewerPublicAccess.Lan;
        }

        if (string.Equals(raw, "LocalhostOnly", StringComparison.OrdinalIgnoreCase))
        {
            return ViewerPublicAccess.LocalhostOnly;
        }

        warnings.Add(new ConfigurationWarning(
            Key: "Viewer:PublicAccess",
            InvalidValue: raw,
            AppliedValue: nameof(ViewerPublicAccess.LocalhostOnly),
            Reason: "既知の公開範囲名（Lan / LocalhostOnly）ではないため、縮小側（LocalhostOnly）を適用" +
                "（configuration.md §1「公開範囲・bind 先の不正値は製品既定へ落とさない」の適用）"));

        return ViewerPublicAccess.LocalhostOnly;
    }

    /// <summary>
    /// 逆引き（PTR）ホスト名表示の有効/無効を解決する（ADR-0007 決定 4。既定オン）。
    /// </summary>
    /// <remarks>
    /// <b>不正値の扱いは「縮小側で継続」</b>: 本機能は外向きの DNS クエリを発生させるため、
    /// 既定がオン（発生側）であっても不正値のフォールバック先は必ず無効（発生しない側）とする
    /// （configuration.md §1 の縮小側原則をセキュリティ 3 項目以外へ適用した初のキー——同 §8）。
    /// <c>Spool:Enabled</c>（既定値で継続 = 不正値でも有効へ戻す）との違いに注意。
    /// </remarks>
    private static bool ResolveViewerReverseDnsEnabled(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        const bool defaultEnabled = true;

        var raw = options.Viewer?.ReverseDns?.Enabled;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultEnabled;
        }

        if (bool.TryParse(raw, out var enabled))
        {
            return enabled;
        }

        warnings.Add(new ConfigurationWarning(
            Key: "Viewer:ReverseDns:Enabled",
            InvalidValue: raw,
            AppliedValue: bool.FalseString,
            Reason: "真偽値として不正なため縮小側（無効 = DNS クエリを発しない）を適用" +
                "（configuration.md §1 の縮小側継続——外向きクエリを発生させる機能は不正値で発生側へ倒さない。ADR-0007 決定 4）"));

        return false;
    }

    /// <summary>
    /// 管理 HTTP リスナのポートを解決する（M6-1。environment 変数 <see cref="YaguraHostEnvironment.AdminPortEnvironmentVariable"/>
    /// が最優先。bind 先アドレスは常に loopback 固定であり、設定キーを持たない——本メソッドは
    /// ポート番号のみを解決する）。不正値は §1「既定値で継続」——管理リスナ自体は loopback 限定の
    /// ため公開範囲の縮小対象ではなく、ポート番号は他の一般キーと同じ扱いでよい。
    /// </summary>
    private static int ResolveAdminHttpPort(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        var envOverride = Environment.GetEnvironmentVariable(YaguraHostEnvironment.AdminPortEnvironmentVariable);
        var envIsSet = !string.IsNullOrWhiteSpace(envOverride);

        if (envIsSet
            && int.TryParse(envOverride, NumberStyles.Integer, CultureInfo.InvariantCulture, out var envPort)
            && IsValidPort(envPort))
        {
            return envPort;
        }

        var portFromFileOrDefault = ResolveAdminHttpPortFromFileOrDefault(options, warnings);

        if (envIsSet)
        {
            warnings.Add(new ConfigurationWarning(
                Key: YaguraHostEnvironment.AdminPortEnvironmentVariable,
                InvalidValue: envOverride!,
                AppliedValue: portFromFileOrDefault.ToString(CultureInfo.InvariantCulture),
                Reason: "環境変数の値がポート番号として不正なため設定ファイル値/既定値を適用"));
        }

        return portFromFileOrDefault;
    }

    /// <summary>
    /// 管理 HTTP ポートのうち「設定ファイル値 → 既定値」の部分を解決する（環境変数を考慮しない）。
    /// </summary>
    private static int ResolveAdminHttpPortFromFileOrDefault(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        var raw = options.Admin?.HttpPort;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return YaguraHostEnvironment.DefaultAdminPort;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && IsValidPort(port))
        {
            return port;
        }

        warnings.Add(new ConfigurationWarning(
            Key: "Admin:HttpPort",
            InvalidValue: raw,
            AppliedValue: YaguraHostEnvironment.DefaultAdminPort.ToString(CultureInfo.InvariantCulture),
            Reason: "ポート番号として不正なため既定値を適用"));

        return YaguraHostEnvironment.DefaultAdminPort;
    }

    /// <summary>
    /// 管理 UI 認証関連の真偽値キーを解決する（ADR-0010 Phase 1）。§1「縮小側で継続」の
    /// セキュリティ 3 項目（公開範囲・bind 先・認証）の一つとして扱う——不正値は
    /// <c>Viewer:ReverseDns:Enabled</c> と同じ「発生しない・要求しない側（false）」へ縮小する
    /// （既定 false と一致するため通常運用では既定値継続と結果は同じだが、分類としては
    /// 縮小側であることを明示するため専用ヘルパーに切り出す）。
    /// </summary>
    /// <summary>
    /// AD グループ指定の生リスト（名 <c>DOMAIN\Group</c> または SID <c>S-1-...</c>）を正規化する
    /// （SEC-9。ADR-0010 決定 5・7・委任事項 8）。空白のみの要素を除去し、順序保持のうえ大文字小文字
    /// 無視で重複排除する。名 → SID の解決は Windows 専用 API（<c>NTAccount.Translate</c>）を要するため
    /// 本メソッドでは行わず（ロード段を OS 非依存・テスト可能に保つ）、<see cref="Yagura.Host.Program"/>
    /// 起動時に解決する。不正な指定（解決できない名等）は起動を止めず、解決段で警告してスキップする
    /// （認可を付与しない安全側——security.md §1 の縮小側原則と同じ向き）。
    /// </summary>
    private static IReadOnlyList<string> ResolveGroupSpecs(List<string>? raw)
    {
        if (raw is null || raw.Count == 0)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(raw.Count);
        foreach (var entry in raw)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var trimmed = entry.Trim();
            if (seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result.Count == 0 ? Array.Empty<string>() : result;
    }

    private static bool ResolveSecurityFlag(
        string? raw, string key, List<ConfigurationWarning> warnings, string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (bool.TryParse(raw, out var value))
        {
            return value;
        }

        warnings.Add(new ConfigurationWarning(
            Key: key,
            InvalidValue: raw,
            AppliedValue: bool.FalseString,
            Reason: reason ?? ("真偽値として不正なため縮小側（無効）を適用" +
                "（configuration.md §1 の縮小側継続——認証関連のセキュリティ項目は不正値で開放側へ落とさない）")));

        return false;
    }

    /// <summary>
    /// 管理リスナのリモート HTTPS 証明書拇印を解決する（ADR-0010 Phase 2 決定 4。
    /// configuration.md §6 と同型——SHA-1・16 進 40 桁）。空白・コロン・ハイフン区切りは
    /// 正規化して受理する（証明書 MMC スナップイン等の一般的な表示形式に合わせるため）。
    /// 不正な形式は §1「縮小側で継続」——HTTPS 未構成として扱う（<see langword="null"/> を返す）。
    /// </summary>
    private static string? ResolveAdminHttpsCertificateThumbprint(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        return NormalizeCertificateThumbprintOrNull(
            options.Admin?.Https?.CertificateThumbprint,
            "Admin:Https:CertificateThumbprint",
            warnings,
            "HTTPS 未構成として扱います（configuration.md §1 の縮小側継続——認証関連のセキュリティ項目は" +
                "不正値で開放側へ落とさない。Admin:RemoteBinding:Enabled が有効な場合、この状態は" +
                "fail-closed 拒否の対象になります）");
    }

    /// <summary>
    /// Windows 証明書ストア拇印（SHA-1・16 進 40 桁）を正規化する共通処理（configuration.md §6 と
    /// 同型の形式検証）。空白・コロン・ハイフン区切りは正規化して受理する（証明書 MMC スナップイン
    /// 等の一般的な表示形式に合わせるため）。<see cref="ResolveAdminHttpsCertificateThumbprint"/>
    /// （管理リスナのリモート HTTPS）と TLS 受信証明書（<c>Ingestion:Tls:CertificateThumbprint</c>。
    /// Issue #137）の両方から呼ばれる——参照方式（拇印の形式検証）は共有し、重複実装しない
    /// （security.md §6「参照方式は Web UI の HTTPS と同型」の設定検証層での具体化）。
    /// 不正な形式は §1「縮小側で継続」——未構成として扱う（<see langword="null"/> を返す）。
    /// </summary>
    /// <param name="raw">設定ファイルの生値。</param>
    /// <param name="key">警告に記録するキー名（呼び出し元ごとに異なる）。</param>
    /// <param name="warnings">警告の収集先。</param>
    /// <param name="unconfiguredConsequenceMessage">
    /// 未構成として扱われた場合の帰結を説明する文言（呼び出し元ごとに異なる——呼び出し元の
    /// fail-closed の有無等を反映するため、共通処理側では固定文言にしない）。
    /// </param>
    private static string? NormalizeCertificateThumbprintOrNull(
        string? raw,
        string key,
        List<ConfigurationWarning> warnings,
        string unconfiguredConsequenceMessage)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = TryNormalizeCertificateThumbprint(raw);
        if (normalized is not null)
        {
            return normalized;
        }

        warnings.Add(new ConfigurationWarning(
            Key: key,
            InvalidValue: "(証明書拇印として不正な形式——値は記録しない)",
            AppliedValue: "(未設定として扱う)",
            Reason: $"SHA-1 拇印（16 進 40 桁）として解釈できないため、{unconfiguredConsequenceMessage}"));

        return null;
    }

    /// <summary>
    /// 証明書拇印の正規化の核（空白・ハイフン・コロン区切りを除去して大文字化し、SHA-1 拇印
    /// = 16 進 40 桁として解釈できなければ <see langword="null"/>）。起動時検証
    /// （<see cref="NormalizeCertificateThumbprintOrNull"/>）と保存前検証
    /// （<c>AdminRemoteAccessAdminService</c>。ADR-0012 決定 4 の「事前検証と起動時検証の
    /// 乖離ゼロ = D-6」）が同一の正規化規則を共有するため、警告収集を伴わない純粋関数として
    /// 切り出してある。
    /// </summary>
    internal static string? TryNormalizeCertificateThumbprint(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = raw.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

        return normalized.Length == 40 && normalized.All(Uri.IsHexDigit) ? normalized : null;
    }

    /// <summary>
    /// 管理リスナのリモート HTTPS 用ポートを解決する（ADR-0010 Phase 2 決定 4。既定 8516。
    /// <see cref="ResolveAdminHttpPortFromFileOrDefault"/> と同じ「§1 既定値で継続」の分類——
    /// リモート HTTPS 自体が opt-in であり受信の成立に不可欠なキーではない）。
    /// </summary>
    private static int ResolveAdminHttpsPort(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        const int defaultPort = 8516;

        // 環境変数（テスト用。0 = OS 採番）が最優先——Admin:HttpPort 等の既存 4 ポートと
        // 同じ優先順位（環境変数 > 設定ファイル > 既定値。configuration.md §2）。
        var envOverride = Environment.GetEnvironmentVariable(YaguraHostEnvironment.AdminHttpsPortEnvironmentVariable);
        var envIsSet = !string.IsNullOrWhiteSpace(envOverride);

        if (envIsSet
            && int.TryParse(envOverride, NumberStyles.Integer, CultureInfo.InvariantCulture, out var envPort)
            && IsValidPort(envPort))
        {
            return envPort;
        }

        var raw = options.Admin?.Https?.Port;
        int portFromFileOrDefault;
        if (string.IsNullOrWhiteSpace(raw))
        {
            portFromFileOrDefault = defaultPort;
        }
        else if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && IsValidPort(port))
        {
            portFromFileOrDefault = port;
        }
        else
        {
            warnings.Add(new ConfigurationWarning(
                Key: "Admin:Https:Port",
                InvalidValue: raw,
                AppliedValue: defaultPort.ToString(CultureInfo.InvariantCulture),
                Reason: "ポート番号として不正なため既定値を適用"));
            portFromFileOrDefault = defaultPort;
        }

        if (envIsSet)
        {
            warnings.Add(new ConfigurationWarning(
                Key: YaguraHostEnvironment.AdminHttpsPortEnvironmentVariable,
                InvalidValue: envOverride!,
                AppliedValue: portFromFileOrDefault.ToString(CultureInfo.InvariantCulture),
                Reason: "環境変数の値がポート番号として不正なため設定ファイル値/既定値を適用"));
        }

        return portFromFileOrDefault;
    }

    /// <summary>
    /// データルート配下の SQLite ファイル名を解決する。パス区切り文字を含む値は
    /// データルート脱出（パストラバーサル）につながるため不正値として扱う。
    /// </summary>
    private static string ResolveSqliteFileName(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        const string defaultFileName = "yagura.db";

        var raw = options.Storage?.SqliteFileName;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultFileName;
        }

        if (raw.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
        {
            return raw;
        }

        warnings.Add(new ConfigurationWarning(
            Key: "Storage:SqliteFileName",
            InvalidValue: raw,
            AppliedValue: defaultFileName,
            Reason: "ファイル名として不正な文字を含むため既定値を適用"));

        return defaultFileName;
    }

    /// <summary>
    /// 永続化 provider の選択を解決する（M5-3。database.md §1）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>不正な provider 名の扱い</b>: <c>sqlite</c>/<c>sqlserver</c> 以外の値（大文字小文字は
    /// 区別しない）は §1「既定値で継続」により <see cref="Configuration.StorageProvider.Sqlite"/>
    /// へフォールバックし警告する。
    /// </para>
    /// <para>
    /// <b>設計判断（本 Issue の設計判断）: provider=sqlserver かつ接続文字列が未設定の場合、
    /// 起動失敗ではなく SQLite へ縮小 + 強い警告とする</b>。理由:
    /// (1) configuration.md §1 が定める「起動失敗」の対象は<b>受信の成立に不可欠なキー</b>
    /// （受信ポート等）に限定される——永続化 provider の選択はこの基準に該当しない
    /// （受信自体は継続でき、書き込み失敗時はスプールが吸収する。architecture.md §3.2）。
    /// (2) 本製品は「ログを失わない」を最優先し、縮退（既定値・安全側フォールバック）を
    /// 起動失敗より優先する設計思想を随所で採用している（同種の前例:
    /// スプール領域が開けない場合の縮退運転——<c>[spool-degraded-mode]</c> 警告。
    /// bind 失敗時の縮小継続——configuration.md §4.1）。SQL Server への昇格を意図した
    /// 環境で接続文字列の設定漏れがあっても、**サービスが全く起動せずログを一切受信できない**
    /// 事態より、**SQLite で受信を継続しながら強い警告で気づかせる**方が「ログを失わない」
    /// 原則に忠実である。
    /// (3) database.md §1.2 の契約 3 分類（一時障害・恒久障害・容量枯渇）に照らすと、
    /// 「接続文字列が無い」は SQL Server provider を構築する<b>前</b>の設定検証段階の問題であり、
    /// provider 自体の実行時障害ではない——本メソッドが SQLite へ縮小することで、
    /// 実際に構築される provider は常に接続可能な状態が保証され、後続の
    /// <see cref="SqlServerFailureClassifier"/> 等の実行時分類の対象にはならない。
    /// </para>
    /// <para>
    /// <b>警告の強さ</b>: 通常の「既定値で継続」警告と同じ経路（<see cref="ConfigurationWarning"/>）
    /// に乗せるが、Reason 文言で「SQL Server を意図していたのに SQLite で動作している」という
    /// 事故（気づかれないと本番想定の環境が組み込み DB のまま運用され続ける）を明示する。
    /// </para>
    /// <para>
    /// <b>DPAPI 暗号化表現の復号（configuration.md §2。ADR-0004 決定 5）</b>:
    /// <c>dpapi:</c> 接頭辞付きの値は <see cref="DpapiConnectionStringProtector"/> で復号して
    /// 使用する。<b>復号失敗（改ざん・別マシンへの設定コピー）は「接続文字列不備」と同じ
    /// SQLite への縮小 + 強い警告</b>とする（起動を止めない——上記 (1)(2) と同じ判断。
    /// 復号失敗は SQL Server provider を構築する前の設定検証段階の問題であり、上記 (3) の
    /// 整理にも合流する）。接頭辞のない平文は従来どおり受理し（手編集ユーザーを壊さない。
    /// 2026-07-06 オーナー決定: 平文 → 暗号化への自動書き戻しはしない）、資格情報入りの場合のみ
    /// <see cref="SqlServerConnectionStringCredentialGuard"/> の検出で警告する。
    /// </para>
    /// </remarks>
    private static (StorageProvider Provider, string? SqlServerConnectionString) ResolveStorageProvider(
        YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        var rawProvider = options.Storage?.Provider;

        if (string.IsNullOrWhiteSpace(rawProvider) || string.Equals(rawProvider, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return (StorageProvider.Sqlite, null);
        }

        if (!string.Equals(rawProvider, "sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new ConfigurationWarning(
                Key: "Storage:Provider",
                InvalidValue: rawProvider,
                AppliedValue: "sqlite",
                Reason: "既知の provider 名（sqlite / sqlserver）ではないため既定の SQLite を適用"));

            return (StorageProvider.Sqlite, null);
        }

        var connectionString = options.Storage?.SqlServer?.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            warnings.Add(new ConfigurationWarning(
                Key: "Storage:SqlServer:ConnectionString",
                InvalidValue: "(未設定)",
                AppliedValue: "sqlite への縮小",
                Reason: "Storage:Provider が sqlserver ですが接続文字列が未設定のため、" +
                    "起動を継続するために組み込み SQLite へ縮小しました。SQL Server での運用を意図する場合は" +
                    "Storage:SqlServer:ConnectionString を設定してください（本縮小は受信を止めないための" +
                    "設計判断——database.md §1「ログを失わない」原則の適用）"));

            return (StorageProvider.Sqlite, null);
        }

        // --- DPAPI 暗号化表現（dpapi:<Base64>。configuration.md §2。ADR-0004 決定 5） ---
        if (DpapiConnectionStringProtector.IsProtected(connectionString))
        {
            if (DpapiConnectionStringProtector.TryUnprotect(connectionString, out var decrypted))
            {
                return (StorageProvider.SqlServer, decrypted);
            }

            // 復号失敗（改ざん・別マシンからの yagura.json コピー——DPAPI machine スコープの
            // マシン束縛による）は「接続文字列不備」（M5-3 の未設定時）と同じ縮小側継続とする。
            // 警告に元の値は載せない（暗号文とはいえ資格情報由来の値を警告経路に流さない）。
            warnings.Add(new ConfigurationWarning(
                Key: "Storage:SqlServer:ConnectionString",
                InvalidValue: "(dpapi: 暗号化表現——復号失敗。値は記録しない)",
                AppliedValue: "sqlite への縮小",
                Reason: "DPAPI 暗号化された接続文字列を復号できないため、起動を継続するために" +
                    "組み込み SQLite へ縮小しました。原因は値の改ざん・破損、または他のマシンで" +
                    "暗号化された設定ファイルのコピーです（DPAPI machine スコープの暗号化データは" +
                    "当該マシンでのみ復号可能——configuration.md §2）。SQL Server での運用を再開するには" +
                    "昇格ウィザードで接続文字列を再入力してください（本縮小は受信を止めないための" +
                    "設計判断——database.md §1「ログを失わない」原則の適用）"));

            return (StorageProvider.Sqlite, null);
        }

        // --- 平文の接続文字列（手編集経路）は従来どおり受理する（2026-07-06 オーナー決定: ---
        // --- 自動書き換えはしない）。資格情報入りの平文のみ警告する（configuration.md §2） ---
        if (SqlServerConnectionStringCredentialGuard.ContainsPlaintextCredential(connectionString))
        {
            warnings.Add(new ConfigurationWarning(
                Key: "Storage:SqlServer:ConnectionString",
                InvalidValue: "(平文の SQL 認証資格情報を含む——値は記録しない)",
                AppliedValue: "(平文のまま受理して継続)",
                Reason: "接続文字列に平文のパスワードが含まれています（ADR-0004 決定 5「設定ファイルに" +
                    "平文で置かない」）。動作は継続しますが、昇格ウィザードで接続文字列を再入力すると" +
                    "DPAPI 暗号化表現（dpapi:）で保存し直せます。設定ファイルの自動書き換えは行いません" +
                    "（利用者のファイルを勝手に変更しない——configuration.md §2）"));
        }

        return (StorageProvider.SqlServer, connectionString);
    }

    /// <summary>
    /// スプールの有効/無効を解決する（既定 <c>true</c>。opt-out。configuration.md §8
    /// 「スプール」区分。§1「既定値で継続」——受信の成立に不可欠なキーではない）。
    /// </summary>
    /// <summary>
    /// 送信元単位の流量制御の有効/無効を解決する（ADR-0002 決定 2「既定有効」。opt-out。
    /// Issue #260。§1「既定値で継続」——真偽値として不正なら既定（有効）へフォールバックし
    /// 警告する。<see cref="ResolveSpoolEnabled"/> と同じ扱い）。
    /// </summary>
    private static bool ResolveFlowControlEnabled(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        const bool defaultEnabled = true;

        var raw = options.Ingestion?.FlowControl?.Enabled;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultEnabled;
        }

        if (bool.TryParse(raw, out var enabled))
        {
            return enabled;
        }

        warnings.Add(new ConfigurationWarning(
            Key: "Ingestion:FlowControl:Enabled",
            InvalidValue: raw,
            AppliedValue: defaultEnabled.ToString(CultureInfo.InvariantCulture),
            Reason: "真偽値として不正なため既定値（有効）を適用"));

        return defaultEnabled;
    }

    /// <summary>
    /// 送信元 1 つあたりの持続速度（件/秒）を解決する（M-4 実測確定待ちの仮値が既定。
    /// §1「既定値で継続」——閾値は受信の成立に不可欠なキーではない）。
    /// </summary>
    private static int ResolveFlowControlMessagesPerSecond(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        return ResolveFlowControlPositiveCount(
            options.Ingestion?.FlowControl?.MessagesPerSecond,
            "Ingestion:FlowControl:MessagesPerSecond",
            TokenBucketIngressGate.DefaultMessagesPerSecond,
            TokenBucketIngressGate.MinMessagesPerSecond,
            TokenBucketIngressGate.MaxMessagesPerSecond,
            warnings);
    }

    /// <summary>
    /// 送信元 1 つあたりのバーストサイズ（件）を解決する（M-4 実測確定待ちの仮値が既定。
    /// §1「既定値で継続」）。
    /// </summary>
    private static int ResolveFlowControlBurstSize(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        return ResolveFlowControlPositiveCount(
            options.Ingestion?.FlowControl?.BurstSize,
            "Ingestion:FlowControl:BurstSize",
            TokenBucketIngressGate.DefaultBurstSize,
            TokenBucketIngressGate.MinBurstSize,
            TokenBucketIngressGate.MaxBurstSize,
            warnings);
    }

    private static int ResolveFlowControlPositiveCount(
        string? raw,
        string key,
        int defaultValue,
        int min,
        int max,
        List<ConfigurationWarning> warnings)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value >= min
            && value <= max)
        {
            return value;
        }

        warnings.Add(new ConfigurationWarning(
            Key: key,
            InvalidValue: raw,
            AppliedValue: defaultValue.ToString(CultureInfo.InvariantCulture),
            Reason: $"件数として不正、または許容範囲（{min}〜{max}）外のため既定値を適用"));

        return defaultValue;
    }

    private static bool ResolveSpoolEnabled(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        const bool defaultEnabled = true;

        var raw = options.Spool?.Enabled;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultEnabled;
        }

        if (bool.TryParse(raw, out var enabled))
        {
            return enabled;
        }

        warnings.Add(new ConfigurationWarning(
            Key: "Spool:Enabled",
            InvalidValue: raw,
            AppliedValue: defaultEnabled.ToString(CultureInfo.InvariantCulture),
            Reason: "真偽値として不正なため既定値（有効）を適用"));

        return defaultEnabled;
    }

    /// <summary>
    /// スプールディレクトリを解決する（既定はデータルート配下の <c>spool</c>。
    /// configuration.md §2「スプールと組み込み DB の置き場所はそれぞれ設定で変更できる」）。
    /// パス区切り文字自体は許容する（絶対パスの指定を妨げないため。<see cref="Path.GetFullPath"/>
    /// で解決できない値のみ不正として扱う）。
    /// </summary>
    private static string ResolveSpoolDirectory(YaguraConfigurationOptions options, string dataRoot, List<ConfigurationWarning> warnings)
    {
        var defaultDirectory = Path.Combine(dataRoot, "spool");

        var raw = options.Spool?.Directory;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultDirectory;
        }

        try
        {
            return Path.GetFullPath(raw);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            warnings.Add(new ConfigurationWarning(
                Key: "Spool:Directory",
                InvalidValue: raw,
                AppliedValue: defaultDirectory,
                Reason: "パスとして不正なため既定値（データルート配下）を適用"));

            return defaultDirectory;
        }
    }

    /// <summary>
    /// スプールのディスク使用量上限（バイト）を解決する（既定は
    /// <see cref="SpoolConstants.DefaultQuotaBytes"/>。M-12 実測確定待ちの暫定値）。
    /// </summary>
    private static long ResolveSpoolQuotaBytes(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        var defaultQuotaBytes = SpoolConstants.DefaultQuotaBytes;

        var raw = options.Spool?.QuotaBytes;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultQuotaBytes;
        }

        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var quotaBytes) && quotaBytes > 0)
        {
            return quotaBytes;
        }

        warnings.Add(new ConfigurationWarning(
            Key: "Spool:QuotaBytes",
            InvalidValue: raw,
            AppliedValue: defaultQuotaBytes.ToString(CultureInfo.InvariantCulture),
            Reason: "正の整数（バイト数）として不正なため既定値を適用"));

        return defaultQuotaBytes;
    }

    /// <summary>
    /// 保持期間（日数）を解決する（database.md §3・DB-1。§1「既定値で継続」）。
    /// <b>未設定時の既定は 30 日</b>（2026-07-05 オーナー決定。PR #64 で確定した DB-1 の値。
    /// 根拠: M7-2 実測でレコード単価 ≈ メッセージ長 + 約 95 B、10 msg/s × 30 日 ≈ 7.8 GB は
    /// SQL Server Express の 10 GB 上限に収まる。容量超過は保持期間とは独立の監視が受ける
    /// 設計であり（database.md §3「ディスク空き容量・DB 容量上限への接近は保持期間とは
    /// 独立に監視・警告する」）、既定を無期限にしないことがゼロ設定ファーストラン環境の
    /// ディスク枯渇を防ぐ）。
    /// </summary>
    /// <remarks>
    /// <b>不正値時のフォールバック先は「削除しない」を維持する</b>（既定 30 日と非対称——
    /// 本 Issue の設計判断）。理由: 「既定値で継続」（§1）の趣旨は本来「製品既定へ戻す」こと
    /// だが、保持期間は他の一般キー（例: ポート番号）と異なり、フォールバックの結果が
    /// 「利用者の意図に反してログが自動的に削除され始める」という不可逆な副作用を持つ。
    /// 例えば入力ミスで <c>Retention:Days=0</c> や負数を書いた利用者は、削除を望んで
    /// いない・保持期間の意味を理解せずに設定を触っただけの可能性が高く、この場合に
    /// 30 日既定へ静かにフォールバックすると「未設定時とは異なる自動削除」が不正な入力
    /// 一つで有効化されてしまう。一方「削除しない」へのフォールバックは、既定 30 日の
    /// 環境と比べてディスク消費が増える方向にしか作用せず、その増加は§3 の独立監視
    /// （容量監視）が受ける設計になっている——安全側（実害が可逆・観測可能な側）を
    /// 優先する既存の縮小方針（bind アドレス・公開範囲の不正値と同じ流儀）に合わせる。
    /// </remarks>
    private static int? ResolveRetentionDays(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        const int defaultRetentionDays = 30;

        var raw = options.Retention?.Days;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultRetentionDays;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days > 0)
        {
            return days;
        }

        warnings.Add(new ConfigurationWarning(
            Key: "Retention:Days",
            InvalidValue: raw,
            AppliedValue: "(未設定 = 削除しない)",
            Reason: "正の整数（日数）として不正なため、意図しない自動削除の開始を避け「削除しない」を適用" +
                "（既定 30 日への自動フォールバックはしない——本 Issue の設計判断。詳細は本メソッドの remarks 参照）"));

        return null;
    }

    /// <summary>
    /// 監査記録の保持期間（日数）を解決する（SEC-2。security.md §4.2。Issue #261）。
    /// 未設定は既定 <b>365 日</b>（SEC-2 確定値）、不正値は <c>Retention:Days</c> と同じ
    /// 「削除しない」へフォールバックし警告する（意図せぬ自動削除で証跡を失う事故を避ける安全側。
    /// 監査記録は証跡であり、不正値を既定 365 日へ読み替えて削除を始めるより、削除を止めて
    /// 警告するほうが失うものが小さい）。
    /// </summary>
    private static int? ResolveAuditRetentionDays(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        const int defaultRetentionDays = 365;

        var raw = options.Audit?.RetentionDays;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultRetentionDays;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days > 0)
        {
            return days;
        }

        warnings.Add(new ConfigurationWarning(
            Key: "Audit:RetentionDays",
            InvalidValue: raw,
            AppliedValue: "(未設定 = 削除しない)",
            Reason: "正の整数（日数）として不正なため、意図しない自動削除の開始を避け「削除しない」を適用" +
                "（既定 365 日への自動フォールバックはしない——Retention:Days と同じ安全側の判断）"));

        return null;
    }

    /// <summary>
    /// メール通知（ADR-0017。opt-in・既定無効）の設定を解決する。送信可能な構成が揃って
    /// いる場合のみ <see cref="ResolvedEmailNotification"/> を返し、それ以外は
    /// <see langword="null"/>（＝送らない）を返す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>不正な構成は「機能を無効化して起動は継続」</b>（ADR-0017 決定 2。configuration.md §1 の
    /// 縮小側継続）。メール通知は受信・保存・閲覧のいずれにも不可欠でないため、構成不備で
    /// サービスの起動そのものを止めるのは釣り合わない——ただし<b>黙って無効化はしない</b>。
    /// どの縮退経路も必ず警告を 1 件積み、起動ログと管理 UI の設定警告一覧に現れる。
    /// </para>
    /// <para>
    /// <b>「無効なら以降を検証しない」</b>: <c>Enabled</c> が false のときは他のキーを一切
    /// 見ない。既定無効の機能について、使っていない利用者の設定ファイルに残った書きかけの値で
    /// 警告を出すのは雑音でしかない（ゼロ設定ファーストランの体験を汚さない——ADR-0017 決定 1）。
    /// </para>
    /// <para>
    /// <b>SMTP-AUTH の片側のみは不正</b>（決定 3）: ユーザー名だけ・パスワードだけの構成は
    /// 「認証したいのに設定が未完成」の状態であり、匿名送信へ黙って落とすと、意図しない
    /// 相手へ認証なしで送る・サーバに拒否され続けるといった形で失敗が遅れて現れる。ここで
    /// 機能ごと無効化して警告するほうが原因に近い。
    /// </para>
    /// <para>
    /// <b>DPAPI 復号失敗も同じ扱い</b>（決定 3 の fail-notify）: 別マシンからの設定コピー・
    /// 値の破損が原因。認証なし送信へのフォールバックはしない（同上）。
    /// </para>
    /// </remarks>
    private static ResolvedEmailNotification? ResolveEmailNotification(
        YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        const int defaultSmtpPort = 25;

        var email = options.Notification?.Email;

        // 警告文は既定（「認証関連のセキュリティ項目」）を使わない——メール通知の有効フラグに
        // その説明は当てはまらず、利用者を認証設定側の調査へ誤誘導する（PR #366 レビュー対応）。
        if (!ResolveSecurityFlag(email?.Enabled, "Notification:Email:Enabled", warnings,
                reason: "真偽値として不正なため縮小側（無効）を適用" +
                    "（configuration.md §1 の縮小側継続——opt-in 機能は不正値で有効側へ落とさない）"))
        {
            return null;
        }

        // --- 差出人・宛先（必須） ---
        var from = email?.From?.Trim();
        if (string.IsNullOrWhiteSpace(from) || !IsPlausibleEmailAddress(from))
        {
            warnings.Add(new ConfigurationWarning(
                Key: "Notification:Email:From",
                InvalidValue: string.IsNullOrWhiteSpace(from) ? "(未設定)" : from,
                AppliedValue: "(メール通知を無効化)",
                Reason: "メール通知が有効ですが差出人アドレスが未設定または不正です。" +
                    "起動を継続するためメール通知のみを無効化しました（ADR-0017 決定 2）"));

            return null;
        }

        var to = ResolveGroupSpecs(email?.To);
        var invalidRecipient = to.FirstOrDefault(address => !IsPlausibleEmailAddress(address));
        if (to.Count == 0 || invalidRecipient is not null)
        {
            warnings.Add(new ConfigurationWarning(
                Key: "Notification:Email:To",
                InvalidValue: invalidRecipient ?? "(未設定)",
                AppliedValue: "(メール通知を無効化)",
                Reason: invalidRecipient is not null
                    ? "宛先アドレスに不正な値が含まれるため、一部だけ送るのではなくメール通知を" +
                        "無効化しました（宛先の取りこぼしに気づけない状態を作らない）"
                    : "メール通知が有効ですが宛先が 1 件も設定されていません。" +
                        "起動を継続するためメール通知のみを無効化しました（ADR-0017 決定 2）"));

            return null;
        }

        // --- SMTP 接続先（Host は必須。Port・Security は既定あり） ---
        var smtp = email?.Smtp;
        var host = smtp?.Host?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            warnings.Add(new ConfigurationWarning(
                Key: "Notification:Email:Smtp:Host",
                InvalidValue: "(未設定)",
                AppliedValue: "(メール通知を無効化)",
                Reason: "メール通知が有効ですが SMTP サーバのホスト名が未設定です。" +
                    "起動を継続するためメール通知のみを無効化しました（ADR-0017 決定 2）"));

            return null;
        }

        var port = defaultSmtpPort;
        var rawPort = smtp?.Port;
        if (!string.IsNullOrWhiteSpace(rawPort))
        {
            if (int.TryParse(rawPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort)
                && parsedPort is >= 1 and <= 65535)
            {
                port = parsedPort;
            }
            else
            {
                // 既定の 25 番へ黙ってフォールバックしない（ADR-0017 決定 2）——指定した覚えの
                // ないポートへ送りに行く／送信が失敗し続けるという形で、設定の誤りが
                // 「届かない」としてしか現れなくなる。
                warnings.Add(new ConfigurationWarning(
                    Key: "Notification:Email:Smtp:Port",
                    InvalidValue: rawPort,
                    AppliedValue: "(メール通知を無効化)",
                    Reason: "1〜65535 の整数として不正です。既定のポートへ黙って倒さず、" +
                        "起動を継続するためメール通知のみを無効化しました（ADR-0017 決定 2）"));

                return null;
            }
        }

        var security = EmailTransportSecurity.Auto;
        var rawSecurity = smtp?.Security?.Trim();
        if (!string.IsNullOrWhiteSpace(rawSecurity))
        {
            switch (rawSecurity.ToLowerInvariant())
            {
                case "none":
                    security = EmailTransportSecurity.None;
                    break;
                case "auto":
                    security = EmailTransportSecurity.Auto;
                    break;
                case "required":
                    security = EmailTransportSecurity.Required;
                    break;
                default:
                    // どちらへも倒さない（ADR-0017 決定 2）——緩い側（auto）へ倒せば暗号化の
                    // 意図が黙って外れ、厳しい側（required）へ倒せば送信が黙って死ぬ。
                    // どちらも「設定したのに意図どおりでない」を無音にする。
                    warnings.Add(new ConfigurationWarning(
                        Key: "Notification:Email:Smtp:Security",
                        InvalidValue: rawSecurity,
                        AppliedValue: "(メール通知を無効化)",
                        Reason: "既知の値（none / auto / required）ではありません。緩い側にも" +
                            "厳しい側にも黙って倒さず、起動を継続するためメール通知のみを" +
                            "無効化しました（ADR-0017 決定 2）"));

                    return null;
            }
        }

        // --- SMTP-AUTH（任意。ただし両方揃っているか、両方無いかのいずれかであること） ---
        var username = string.IsNullOrWhiteSpace(smtp?.Username) ? null : smtp.Username.Trim();
        var rawPassword = string.IsNullOrWhiteSpace(smtp?.Password) ? null : smtp.Password;

        if ((username is null) != (rawPassword is null))
        {
            warnings.Add(new ConfigurationWarning(
                Key: username is null ? "Notification:Email:Smtp:Username" : "Notification:Email:Smtp:Password",
                InvalidValue: "(未設定——対になるキーのみ設定されています。値は記録しない)",
                AppliedValue: "(メール通知を無効化)",
                Reason: "SMTP 認証はユーザー名とパスワードの両方が必要です。片方のみが設定されて" +
                    "いるため、認証なしの送信へ落とさずメール通知を無効化しました（ADR-0017 決定 3）"));

            return null;
        }

        string? password = null;
        if (rawPassword is not null)
        {
            if (DpapiEmailPasswordProtector.IsProtected(rawPassword))
            {
                if (!DpapiEmailPasswordProtector.TryUnprotect(rawPassword, out password))
                {
                    // 警告に値は載せない（暗号文であっても資格情報由来の値を警告経路へ流さない
                    // ——Storage:SqlServer:ConnectionString の復号失敗時と同じ作法）。
                    warnings.Add(new ConfigurationWarning(
                        Key: "Notification:Email:Smtp:Password",
                        InvalidValue: "(dpapi: 暗号化表現——復号失敗。値は記録しない)",
                        AppliedValue: "(メール通知を無効化)",
                        Reason: "DPAPI 暗号化されたパスワードを復号できないため、認証なしの送信へ" +
                            "落とさずメール通知を無効化しました。原因は値の改ざん・破損、または他の" +
                            "マシンで暗号化された設定ファイルのコピーです（DPAPI machine スコープの" +
                            "暗号化データは当該マシンでのみ復号可能——configuration.md §2）。" +
                            "管理 UI でパスワードを再入力すると復旧します（ADR-0017 決定 3）"));

                    return null;
                }
            }
            else
            {
                // 平文の手編集は受理する（利用者のファイルを勝手に書き換えない——
                // configuration.md §2。Storage:SqlServer:ConnectionString と同じ判断）。
                password = rawPassword;

                warnings.Add(new ConfigurationWarning(
                    Key: "Notification:Email:Smtp:Password",
                    InvalidValue: "(平文のパスワード——値は記録しない)",
                    AppliedValue: "(平文のまま受理して継続)",
                    Reason: "パスワードが平文で保存されています（ADR-0004 決定 5「設定ファイルに" +
                        "平文で置かない」）。動作は継続しますが、管理 UI でパスワードを再入力すると" +
                        "DPAPI 暗号化表現（dpapi:）で保存し直せます。設定ファイルの自動書き換えは" +
                        "行いません（configuration.md §2）"));
            }
        }

        // 決定 3 の能動警告（Issue #385）: 資格情報あり + Security ≠ required は、設定保存時
        // （画面のライブバナー）だけでなく**起動時・再読み込み時にも**警告する——手編集で
        // Security を auto へ戻した場合に誰も気づけない状態を作らない。機能は無効化しない
        // （推奨からの逸脱であり不正値ではない）。
        if (password is not null && security != EmailTransportSecurity.Required)
        {
            warnings.Add(new ConfigurationWarning(
                Key: "Notification:Email:Smtp:Security",
                InvalidValue: security == EmailTransportSecurity.None ? "none" : "auto",
                AppliedValue: "(そのまま受理して継続——不正値ではなく推奨からの逸脱)",
                Reason: "パスワードが設定されていますが、暗号化（STARTTLS）が required ではありません。" +
                    "経路上で暗号化が剥がされた場合に漏れるのは通知の内容ではなく SMTP の資格情報です" +
                    "（多くの環境では AD のアカウントと同じもの）。required への変更を強く推奨します" +
                    "（ADR-0017 決定 3）"));
        }

        return new ResolvedEmailNotification(
            From: from,
            To: to,
            SmtpHost: host,
            SmtpPort: port,
            Security: security,
            Username: username,
            Password: password);
    }

    /// <summary>
    /// 送信元の途絶検知（ADR-0018。opt-in・既定無効）のウォッチリストを解決する。
    /// 監視すべきエントリが 1 件以上ある場合のみ <see cref="ResolvedSourceSilence"/> を返す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>縮退はエントリ単位</b>（ADR-0018 決定 1。configuration.md §1 の 3 分類に対する第 4 の
    /// 挙動）: アドレスの形式不正・閾値の範囲外は<b>当該エントリのみ</b>を無効化して警告し、
    /// リスト全体は生かす。1 エントリのタイポで他の監視まで止めるのは巻き添えが過剰である。
    /// </para>
    /// <para>
    /// <b>空配列は「正常な空リスト」</b>（警告なし）。configuration.md §1 の「空配列 = 不正値」
    /// 規定の例外——あの規定は「空 + 機能有効 = 誰も対象にならない」文脈のものであり、
    /// 本キーは空 = 意図的な無効が自然な意味論である。
    /// </para>
    /// <para>
    /// <b>上限超過はファイル順で先頭から採用し、超過分を列挙して警告する</b>。「監視されている
    /// つもりで監視されていない」検知ギャップを黙らせないため、無効化した対象アドレスを
    /// 警告に載せる。<b>先頭への追記は末尾の既存監視を押し出す</b>（新規追加が失敗するのではなく
    /// 既存の監視が止まる向き）ため、利用者向けドキュメントでは末尾追記を推奨する（申し送り D-2）。
    /// </para>
    /// </remarks>
    private static ResolvedSourceSilence? ResolveSourceSilence(
        YaguraConfigurationOptions options, List<ConfigurationWarning> warnings, ILogger logger)
    {
        var sourceSilence = options.Notification?.SourceSilence;
        var rawWatchlist = sourceSilence?.Watchlist;

        // 未設定・空配列はいずれも「機能無効」。空配列は正常な意思表示であり警告しない。
        if (rawWatchlist is null || rawWatchlist.Count == 0)
        {
            return null;
        }

        var defaultThreshold = ResolveDefaultSilenceThreshold(sourceSilence, warnings);

        var entries = new List<SourceSilenceWatchEntry>();
        var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var droppedByCap = new List<string>();

        for (var index = 0; index < rawWatchlist.Count; index++)
        {
            var raw = rawWatchlist[index];
            var rawAddress = raw?.Address?.Trim();

            // --- 上限（ファイル順で先頭から採用する。ADR-0018 決定 1） ---
            if (entries.Count >= SourceSilenceConstants.MaxWatchlistEntries)
            {
                droppedByCap.Add(rawAddress ?? $"[{index}]");
                continue;
            }

            // --- アドレス（必須・形式検証・正規化） ---
            if (string.IsNullOrWhiteSpace(rawAddress) || !IPAddress.TryParse(rawAddress, out var parsedAddress))
            {
                warnings.Add(new ConfigurationWarning(
                    Key: $"{SourceSilenceWatchlistKey}[{index}]:Address",
                    InvalidValue: rawAddress ?? "(未設定)",
                    AppliedValue: "(当該エントリのみ無効化)",
                    Reason: "IP アドレスとして解釈できないため、このエントリのみ監視対象から外しました" +
                        "（他のエントリの監視は継続します）"));
                continue;
            }

            // IPv4-mapped IPv6 は IPv4 へ畳む（流量制御・Top talkers と同じ既存規約）。
            // 同一装置が 2 エントリに割れ、片方だけが更新されて他方が途絶に見える事故を防ぐ。
            if (parsedAddress.IsIPv4MappedToIPv6)
            {
                parsedAddress = parsedAddress.MapToIPv4();
            }

            var normalizedAddress = parsedAddress.ToString();

            if (!seenAddresses.Add(normalizedAddress))
            {
                warnings.Add(new ConfigurationWarning(
                    Key: $"{SourceSilenceWatchlistKey}[{index}]:Address",
                    InvalidValue: rawAddress,
                    AppliedValue: "(当該エントリのみ無効化)",
                    Reason: "同じ送信元アドレスが既に登録されているため、後から現れたエントリを外しました" +
                        "（先に現れたエントリの閾値・表示名が有効です）"));
                continue;
            }

            // --- 閾値（任意。範囲外は当該エントリのみ無効化） ---
            var threshold = defaultThreshold;
            var thresholdIsDefaulted = true;
            var rawThreshold = raw?.ThresholdMinutes?.Trim();

            if (!string.IsNullOrWhiteSpace(rawThreshold))
            {
                if (!int.TryParse(rawThreshold, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
                    || minutes < SourceSilenceConstants.MinThresholdMinutes
                    || minutes > SourceSilenceConstants.MaxThresholdMinutes)
                {
                    warnings.Add(new ConfigurationWarning(
                        Key: $"{SourceSilenceWatchlistKey}[{index}]:ThresholdMinutes",
                        InvalidValue: rawThreshold,
                        AppliedValue: "(当該エントリのみ無効化)",
                        Reason: $"{SourceSilenceConstants.MinThresholdMinutes}〜" +
                            $"{SourceSilenceConstants.MaxThresholdMinutes} 分の整数ではないため、" +
                            "このエントリのみ監視対象から外しました。既定の閾値へ黙って倒すと" +
                            "「指定したつもりの閾値で監視されていない」状態になるため、" +
                            "既定値へのフォールバックはしません"));

                    seenAddresses.Remove(normalizedAddress);
                    continue;
                }

                threshold = TimeSpan.FromMinutes(minutes);
                thresholdIsDefaulted = false;
            }

            entries.Add(new SourceSilenceWatchEntry(
                parsedAddress,
                string.IsNullOrWhiteSpace(raw?.Label) ? null : raw.Label.Trim(),
                threshold,
                thresholdIsDefaulted));
        }

        if (droppedByCap.Count > 0)
        {
            warnings.Add(new ConfigurationWarning(
                Key: SourceSilenceWatchlistKey,
                InvalidValue: $"{rawWatchlist.Count} 件（上限 {SourceSilenceConstants.MaxWatchlistEntries} 件）",
                AppliedValue: $"ファイル順で先頭 {SourceSilenceConstants.MaxWatchlistEntries} 件のみ有効",
                Reason: "登録上限を超えたため、超過分を監視対象から外しました。外したアドレス: " +
                    string.Join(", ", droppedByCap) +
                    "。（先頭への追記は末尾の既存監視を押し出します——新規追加が失敗するのではなく" +
                    "既存の監視が止まる向きです。追記は末尾に行ってください）"));
        }

        if (entries.Count == 0)
        {
            return null;
        }

        // 既定値で補完したエントリは情報レベルで残す（ADR-0018 決定 1——手編集の大量投入で
        // 閾値の省略が起きやすく、「登録した = すぐ気づける」という期待と 24 時間既定のズレが
        // 黙って生じるのを防ぐ）。警告ではない——省略自体は正当な使い方である。
        var defaulted = entries.Where(entry => entry.ThresholdIsDefaulted).ToList();
        if (defaulted.Count > 0)
        {
            logger.LogInformation(
                "途絶検知のウォッチリスト {TotalCount} 件のうち {DefaultedCount} 件は閾値が未指定のため既定値 " +
                "{DefaultThreshold} を適用しました（対象: {DefaultedAddresses}）。",
                entries.Count,
                defaulted.Count,
                defaultThreshold,
                string.Join(", ", defaulted.Select(entry => entry.Address.ToString())));
        }

        return new ResolvedSourceSilence(entries);
    }

    /// <summary>
    /// 閾値を省略したエントリの補完値を解決する（既定 1440 分 = 24 時間）。
    /// </summary>
    /// <remarks>
    /// エントリ個別の閾値と違い、こちらは<b>既定値へフォールバックする</b>——本キーが不正でも
    /// 「補完値が決まらない」だけであり、監視自体を止める理由にはならない（エントリ個別の
    /// 閾値は「指定したつもりの値で監視されていない」を作るため無効化に倒す。非対称は意図的）。
    /// </remarks>
    private static TimeSpan ResolveDefaultSilenceThreshold(
        YaguraConfigurationOptions.NotificationOptions.SourceSilenceOptions? options,
        List<ConfigurationWarning> warnings)
    {
        var fallback = TimeSpan.FromMinutes(SourceSilenceConstants.DefaultThresholdMinutes);
        var raw = options?.DefaultThresholdMinutes?.Trim();

        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            && minutes >= SourceSilenceConstants.MinThresholdMinutes
            && minutes <= SourceSilenceConstants.MaxThresholdMinutes)
        {
            return TimeSpan.FromMinutes(minutes);
        }

        warnings.Add(new ConfigurationWarning(
            Key: SourceSilenceDefaultThresholdKey,
            InvalidValue: raw,
            AppliedValue: $"{SourceSilenceConstants.DefaultThresholdMinutes} 分",
            Reason: $"{SourceSilenceConstants.MinThresholdMinutes}〜" +
                $"{SourceSilenceConstants.MaxThresholdMinutes} 分の整数ではないため既定値を適用" +
                "（本キーは閾値を省略したエントリの補完値であり、不正でも監視自体を止める理由に" +
                "ならないため既定へフォールバックします）"));

        return fallback;
    }

    private const string SourceSilenceWatchlistKey = "Notification:SourceSilence:Watchlist";
    private const string SourceSilenceDefaultThresholdKey = "Notification:SourceSilence:DefaultThresholdMinutes";

    /// <summary>
    /// メールアドレスとして最低限の体裁を満たすかを判定する（構成の解決段の受け皿）。
    /// </summary>
    /// <remarks>
    /// <b>ここは RFC 5321/5322 の完全な検証ではない</b>。目的は「空文字・ホスト名だけ・
    /// 記号の打ち間違い」といった明らかな誤りを設定保存の時点で拾うことであり、最終的な
    /// 可否は送信時にサーバが決める（ADR-0017 が SMTP を外部境界としている以上、
    /// 構成層で厳密に判定しても偽陰性——正当なアドレスを拒む——を作るだけになる）。
    /// </remarks>
    private static bool IsPlausibleEmailAddress(string value) =>
        System.Net.Mail.MailAddress.TryCreate(value, out _);

    /// <summary>
    /// 保持期間削除の定期実行時刻を解決する（database.md §3。§1「既定値で継続」）。
    /// </summary>
    private static TimeOnly ResolveRetentionExecutionTimeOfDay(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        var defaultTimeOfDay = Yagura.Host.Retention.RetentionSchedulerOptions.DefaultExecutionTimeOfDay;

        var raw = options.Retention?.ExecutionTimeOfDay;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultTimeOfDay;
        }

        if (TimeOnly.TryParseExact(raw, "HH\\:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timeOfDay))
        {
            return timeOfDay;
        }

        warnings.Add(new ConfigurationWarning(
            Key: "Retention:ExecutionTimeOfDay",
            InvalidValue: raw,
            AppliedValue: defaultTimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture),
            Reason: "HH:mm 形式の時刻として不正なため既定値を適用"));

        return defaultTimeOfDay;
    }

    private static int ParsePortOrThrow(string raw, string key, string source)
    {
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && IsValidPort(port))
        {
            return port;
        }

        throw new ConfigurationValidationException(
            $"{source} で指定された {key} の値 '{raw}' はポート番号として不正です（0〜65535 の整数、" +
            "またはテスト用の OS 採番指定 0 のみ有効）。受信の成立に不可欠なキーのため起動を中止します。");
    }

    /// <summary>
    /// ポート番号として妥当かどうか（0 は OS 採番指定として許容する。テスト用途。
    /// <see cref="UdpSyslogListenerOptions.Port"/> のドキュメント参照）。
    /// </summary>
    private static bool IsValidPort(int port) => port is >= 0 and <= 65535;
}
