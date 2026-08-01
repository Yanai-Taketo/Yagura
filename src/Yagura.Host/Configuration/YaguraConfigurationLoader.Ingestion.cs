using System.Globalization;
using System.Net;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Tcp;
using Yagura.Ingestion.Tls;
using Yagura.Ingestion.Udp;

namespace Yagura.Host.Configuration;

public static partial class YaguraConfigurationLoader
{
    /// <summary>
    /// UDP bind アドレスを解決する。環境変数による上書きは現時点で提供しない
    /// （既存 3 環境変数のみ維持。依頼範囲外）。不正値は §1「縮小側で継続」により
    /// loopback（127.0.0.1）へ縮小する。
    /// </summary>
    /// <returns>
    /// 解決済みアドレスと、キーが明示指定されていたか（IPv6 不可の
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
        // ワイルドカードの意味づけ（configuration.md §4.1）: 既定の "::" は
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

        // ワイルドカードの意味づけは UDP 側と同一（ResolveUdpBindAddress のコメント参照）。
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
    /// TLS 受信（RFC 5425。opt-in）の bind アドレスを解決する。TCP と同じ分類
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
    /// が最優先）。§1「既定値で継続」——TLS 受信は opt-in であり、平文受信の成立には
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
    /// RFC 3164 TIMESTAMP の既定タイムゾーンを解決する（§1「既定値で継続」——
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
    /// スプールの有効/無効を解決する（既定 <c>true</c>。opt-out。configuration.md §8
    /// 「スプール」区分。§1「既定値で継続」——受信の成立に不可欠なキーではない）。
    /// </summary>
    /// <summary>
    /// 送信元単位の流量制御の有効/無効を解決する（ADR-0002 決定 2「既定有効」。opt-out。
    /// §1「既定値で継続」——真偽値として不正なら既定（有効）へフォールバックし
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
}
