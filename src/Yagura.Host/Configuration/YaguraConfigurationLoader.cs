using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Yagura.Ingestion.Tcp;
using Yagura.Ingestion.Udp;

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
    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ingestion:Udp:BindAddress",
        "Ingestion:Udp:Port",
        "Ingestion:Tcp:BindAddress",
        "Ingestion:Tcp:Port",
        "Viewer:HttpPort",
        "Storage:SqliteFileName",
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

        // --- 永続化: SQLite ファイル名（§1「既定値で継続」） ---
        var sqliteFileName = ResolveSqliteFileName(options, warnings);

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
            SqliteFileName: sqliteFileName);

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
