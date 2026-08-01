using System.Globalization;

namespace Yagura.Host.Configuration;

public static partial class YaguraConfigurationLoader
{
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
}
