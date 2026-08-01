using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

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
public static partial class YaguraConfigurationLoader
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
        "Viewer:Https:Enabled",
        "Viewer:Https:CertificateThumbprint",
        "Admin:HttpPort",
        "Admin:Authentication:Windows:Enabled",
        "Admin:Authentication:Windows:KerberosOnly",
        "Admin:Authentication:App:Enabled",
        "Admin:Authentication:RequireForLoopback",
        "Admin:RemoteBinding:Enabled",
        "Admin:Https:Enabled",
        "Admin:Https:CertificateThumbprint",
        "Admin:Https:Port",
        "Admin:ForwarderKit:MsiUpload:Enabled",
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
    /// <see cref="KnownArrayKeys"/>（スカラーの配列）とは平坦化の形が違う。実測結果:
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

        // 型の読み替え検出: 平坦化後の値からはトークン型を復元できないため、
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

        // --- 受信: TLS 受信 opt-in（RFC 5425。security.md §6）。§1「縮小側で継続」——
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

        // --- 受信: RFC 3164 TIMESTAMP の既定タイムゾーン（§1「既定値で継続」） ---
        var defaultRfc3164TimeZone = ResolveDefaultRfc3164TimeZone(options, warnings);

        // --- 流量制御: 有効/無効・送信元別閾値（§1「既定値で継続」。ADR-0002 決定 2） ---
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

        // --- UI: 閲覧 UI の HTTPS（ADR-0022 決定 1。opt-in。不正時挙動は条件分岐——拇印の設定
        //     有無 = 暗号化意図の証跡の有無で、平文（無効）へ倒すか閲覧リスナを開かない縮小継続へ
        //     倒すかを分ける。「緩い側へ倒せば暗号化の意図が黙って外れる」を無音にしない） ---
        var (viewerHttpsMode, viewerHttpsCertificateThumbprint, viewerHttpsSuppressedReason) =
            ResolveViewerHttps(options, warnings);

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
        //     loopback にも認証を要求してしまい、「最終復旧経路」を誤設定 1 つで自壊させるため、
        //     既定値へのフォールバック
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

        // --- UI: フォワーダ MSI の管理画面アップロード（ADR-0020 決定 1。opt-in。§1「縮小側で継続」——
        //     書き込み系の管理機能は不正値で有効側へ落とさない） ---
        var adminForwarderMsiUploadEnabled = ResolveSecurityFlag(
            options.Admin?.ForwarderKit?.MsiUpload?.Enabled, "Admin:ForwarderKit:MsiUpload:Enabled", warnings);

        // --- fail-closed 不変条件（ADR-0021 決定 1。前提条件は「認証方式が最低 1 つ有効」のみ。
        //     1011/1012 と同型の「起動失敗」分類）。
        //     無認証 loopback からの到達遮断はリスナ全体（RequireForLoopback）ではなく
        //     アップロード操作単位の専用認可ポリシー（実際にサインインした管理セッションのみ。
        //     ForwarderMsiUploadPolicyName）が担う。サインインの手段が存在しない構成で
        //     有効化すると誰も操作を通過できず、かつ書き込み口の存在だけが残るため、
        //     縮小継続ではなく起動そのものを止める。エラーメッセージには復旧に必要な
        //     具体の設定キーと値を明記する（手編集復旧の場面では UI の誘導が使えない） ---
        if (adminForwarderMsiUploadEnabled)
        {
            var authenticationConfigured = adminWindowsAuthEnabled || adminAppAuthEnabled;
            if (!authenticationConfigured)
            {
                throw new ConfigurationValidationException(
                    "Admin:ForwarderKit:MsiUpload:Enabled が有効ですが、前提条件が満たされていません: " +
                    "認証方式（Admin:Authentication:Windows:Enabled または Admin:Authentication:App:Enabled）" +
                    "の少なくとも一方を有効にしてください。" +
                    "アップロード・削除の操作は実際にサインインした管理者に限定されるため" +
                    "（ADR-0021 決定 1）、サインインの手段が構成されていない構成では機能を有効化できません。" +
                    "認証方式を有効化するか、Admin:ForwarderKit:MsiUpload:Enabled を false に戻してから" +
                    "再起動してください。",
                    ConfigurationEventIds.ForwarderMsiUploadFailClosedStartupRejected);
            }
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

        // --- 監査: 保持期間（SEC-2。security.md §4.2） ---
        var auditRetentionDays = ResolveAuditRetentionDays(options, warnings);

        // --- 能動通知: メール（ADR-0017。opt-in・既定無効） ---
        var emailNotification = ResolveEmailNotification(options, warnings);

        // --- 能動通知: 送信元の途絶検知（ADR-0018。opt-in・既定無効） ---
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
            // bind アドレスの明示指定フラグ（IPv6 不可の環境での
            // 「既定は IPv4 縮小 / 明示は fail-fast」の分岐の入力——受信段へ引き渡す）。
            UdpBindAddressIsExplicit = udpBindAddressIsExplicit,
            TcpBindAddressIsExplicit = tcpBindAddressIsExplicit,
            IngestionTlsBindAddressIsExplicit = ingestionTlsBindAddressIsExplicit,
            ViewerHttpsMode = viewerHttpsMode,
            ViewerHttpsCertificateThumbprint = viewerHttpsCertificateThumbprint,
            ViewerHttpsSuppressedReason = viewerHttpsSuppressedReason,
            EmailNotification = emailNotification,
            SourceSilence = sourceSilence,
            AdminForwarderMsiUploadEnabled = adminForwarderMsiUploadEnabled,
        };

        // 型の読み替えの報告対象: 不正値の警告・未知キーの警告が既に出るキーは
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
    /// トークンが現れたキー（型の読み替え。configuration.md §1）を収集する。
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
            // 同じ機構）。バイト列を直接 JsonDocument.Parse へ渡すと、両読み手が
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
                // 構成システムの平坦化結果と同じ表記（"True" / "False"）。
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
            // 配列キー自身も既知として扱う（グループ一覧も同じ性質を持つ）。
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
    /// Windows 証明書ストア拇印（SHA-1・16 進 40 桁）を正規化する共通処理（configuration.md §6 と
    /// 同型の形式検証）。空白・コロン・ハイフン区切りは正規化して受理する（証明書 MMC スナップイン
    /// 等の一般的な表示形式に合わせるため）。<see cref="ResolveAdminHttpsCertificateThumbprint"/>
    /// （管理リスナのリモート HTTPS）と TLS 受信証明書（<c>Ingestion:Tls:CertificateThumbprint</c>）
    /// の両方から呼ばれる——参照方式（拇印の形式検証）は共有し、重複実装しない
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
    /// ポート番号として妥当かどうか（0 は OS 採番指定として許容する。テスト用途。
    /// <see cref="UdpSyslogListenerOptions.Port"/> のドキュメント参照）。
    /// </summary>
    private static bool IsValidPort(int port) => port is >= 0 and <= 65535;
}
