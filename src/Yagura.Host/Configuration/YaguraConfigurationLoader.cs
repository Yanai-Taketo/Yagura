using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Yagura.Ingestion.Tcp;
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
        "Ingestion:Tcp:BindAddress",
        "Ingestion:Tcp:Port",
        "Viewer:HttpPort",
        "Viewer:PublicAccess",
        "Admin:HttpPort",
        "Storage:SqliteFileName",
        "Storage:Provider",
        "Storage:SqlServer:ConnectionString",
        "Spool:Enabled",
        "Spool:Directory",
        "Spool:QuotaBytes",
        "Retention:Days",
        "Retention:ExecutionTimeOfDay",
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

        var options = new YaguraConfigurationOptions();
        configurationRoot.Bind(options);

        var warnings = new List<ConfigurationWarning>();

        // --- 受信: UDP bind アドレス（§1「縮小側で継続」） ---
        var udpBindAddress = ResolveUdpBindAddress(options, warnings);

        // --- 受信: UDP ポート（§1「起動失敗」——受信の成立に不可欠なキー） ---
        var udpPort = ResolveUdpPort(options);

        // --- 受信: TCP bind アドレス（§1「縮小側で継続」。UDP と同じ分類。M4-1） ---
        var tcpBindAddress = ResolveTcpBindAddress(options, warnings);

        // --- 受信: TCP ポート（§1「起動失敗」——UDP と同じ分類。M4-1） ---
        var tcpPort = ResolveTcpPort(options);

        // --- UI: 閲覧 HTTP ポート（§1「既定値で継続」） ---
        var httpPort = ResolveHttpPort(options, warnings);

        // --- UI: 閲覧リスナの公開範囲（§1「縮小側で継続」。M6-1） ---
        var viewerPublicAccess = ResolveViewerPublicAccess(options, warnings);

        // --- UI: 管理 HTTP ポート（§1「既定値で継続」。bind 先は常に loopback 固定。M6-1） ---
        var adminHttpPort = ResolveAdminHttpPort(options, warnings);

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
            TcpBindAddress: tcpBindAddress,
            TcpPort: tcpPort,
            HttpPort: httpPort,
            ViewerPublicAccess: viewerPublicAccess,
            AdminHttpPort: adminHttpPort,
            SqliteFileName: sqliteFileName,
            SpoolEnabled: spoolEnabled,
            SpoolDirectory: spoolDirectory,
            SpoolQuotaBytes: spoolQuotaBytes,
            RetentionDays: retentionDays,
            RetentionExecutionTimeOfDay: retentionExecutionTimeOfDay,
            StorageProvider: storageProvider,
            SqlServerConnectionString: sqlServerConnectionString);

        return new ConfigurationLoadResult(resolved, warnings, unknownKeys);
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
    /// 設定ファイル内の JSON キーパスのうち <see cref="KnownKeys"/> に含まれないものを列挙する。
    /// </summary>
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

            if (!KnownKeys.Contains(entry.Key))
            {
                unknown.Add(entry.Key);
            }
        }

        return unknown;
    }

    /// <summary>
    /// UDP bind アドレスを解決する。環境変数による上書きは現時点で提供しない
    /// （既存 3 環境変数のみ維持。依頼範囲外）。不正値は §1「縮小側で継続」により
    /// loopback（127.0.0.1）へ縮小する。
    /// </summary>
    private static string ResolveUdpBindAddress(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        var raw = options.Ingestion?.Udp?.BindAddress;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return UdpSyslogListenerOptions.DefaultBindAddress;
        }

        // "0.0.0.0"（全インターフェース）は既定どおりの正当な値として受け入れる。
        // それ以外は IPAddress として解釈できることを確認する（形式不正の縮小対象）。
        if (raw == UdpSyslogListenerOptions.DefaultBindAddress || IPAddress.TryParse(raw, out _))
        {
            return raw;
        }

        warnings.Add(new ConfigurationWarning(
            Key: "Ingestion:Udp:BindAddress",
            InvalidValue: raw,
            AppliedValue: IPAddress.Loopback.ToString(),
            Reason: "bind 先アドレスの形式が不正なため安全側（loopback）へ縮小"));

        return IPAddress.Loopback.ToString();
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
    /// TCP bind アドレスを解決する（M4-1）。UDP と同じ分類（§1「縮小側で継続」）を適用する:
    /// 環境変数による上書きは現時点で提供しない（既存方針を踏襲）。不正値は loopback へ縮小する。
    /// </summary>
    private static string ResolveTcpBindAddress(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        var raw = options.Ingestion?.Tcp?.BindAddress;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TcpSyslogListenerOptions.DefaultBindAddress;
        }

        if (raw == TcpSyslogListenerOptions.DefaultBindAddress || IPAddress.TryParse(raw, out _))
        {
            return raw;
        }

        warnings.Add(new ConfigurationWarning(
            Key: "Ingestion:Tcp:BindAddress",
            InvalidValue: raw,
            AppliedValue: IPAddress.Loopback.ToString(),
            Reason: "bind 先アドレスの形式が不正なため安全側（loopback）へ縮小"));

        return IPAddress.Loopback.ToString();
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

        return (StorageProvider.SqlServer, connectionString);
    }

    /// <summary>
    /// スプールの有効/無効を解決する（既定 <c>true</c>。opt-out。configuration.md §8
    /// 「スプール」区分。§1「既定値で継続」——受信の成立に不可欠なキーではない）。
    /// </summary>
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
