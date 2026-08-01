using System.Globalization;

namespace Yagura.Host.Configuration;

public static partial class YaguraConfigurationLoader
{
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
    /// 閲覧 UI の HTTPS（ADR-0022 決定 1）を解決する。不正時挙動は条件分岐:
    /// <list type="bullet">
    /// <item><c>Enabled = true</c> + 形式上有効な拇印 → <see cref="ViewerHttpsMode.Enabled"/>
    /// （実際のストア解決は Program 側）。</item>
    /// <item><c>Enabled = true</c> + 拇印が未設定・形式不正 → <see cref="ViewerHttpsMode.SuppressListener"/>
    /// （閲覧リスナを開かない縮小継続——平文では開かない。決定 2）。</item>
    /// <item><c>Enabled</c> が真偽値として不正 + 拇印が設定済み（形式不正含む——HTTPS を意図した
    /// 証跡がある）→ <see cref="ViewerHttpsMode.SuppressListener"/> + 警告。</item>
    /// <item><c>Enabled</c> が真偽値として不正 + 拇印も未設定（HTTPS が構成された形跡がない）→
    /// <see cref="ViewerHttpsMode.Disabled"/> + 警告（閲覧全停止は釣り合わない）。</item>
    /// </list>
    /// <c>Admin:Https:Enabled</c>（不正値は無効へ）と縮退の向きが異なるのは意図的——管理側は
    /// RemoteBinding との fail-closed 組み合わせ検証（1012）が最終防衛線として控えるため無効化が
    /// 平文露出に直結しないが、閲覧側にその防衛線はない（ADR-0022 決定 1）。
    /// </summary>
    private static (ViewerHttpsMode Mode, string? Thumbprint, string? SuppressedReason) ResolveViewerHttps(
        YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        var rawEnabled = options.Viewer?.Https?.Enabled;
        var rawThumbprint = options.Viewer?.Https?.CertificateThumbprint;
        var thumbprint = NormalizeCertificateThumbprintOrNull(
            rawThumbprint,
            "Viewer:Https:CertificateThumbprint",
            warnings,
            "閲覧 HTTPS の証明書未設定として扱います（Viewer:Https:Enabled が有効な場合、閲覧リスナは" +
                "平文では開かず縮小継続します——ADR-0022 決定 2。受信・管理リスナは影響を受けません）");

        if (string.IsNullOrWhiteSpace(rawEnabled))
        {
            return (ViewerHttpsMode.Disabled, thumbprint, null);
        }

        if (bool.TryParse(rawEnabled, out var enabled))
        {
            if (!enabled)
            {
                return (ViewerHttpsMode.Disabled, thumbprint, null);
            }

            if (thumbprint is null)
            {
                return (ViewerHttpsMode.SuppressListener, null,
                    "閲覧 HTTPS（Viewer:Https:Enabled）が有効ですが、証明書拇印" +
                    "（Viewer:Https:CertificateThumbprint）が未設定、または SHA-1 拇印（16 進 40 桁）として" +
                    "解釈できない形式です。");
            }

            return (ViewerHttpsMode.Enabled, thumbprint, null);
        }

        // Enabled が真偽値として不正。拇印の「設定有無」は生値の有無で判定する（形式不正の拇印も
        // 「HTTPS を構成しようとした証跡」に数える——ADR-0022 決定 1 の趣旨）。
        if (!string.IsNullOrWhiteSpace(rawThumbprint))
        {
            warnings.Add(new ConfigurationWarning(
                Key: "Viewer:Https:Enabled",
                InvalidValue: rawEnabled,
                AppliedValue: "(閲覧リスナを開かない縮小継続)",
                Reason: "真偽値として不正で、かつ証明書拇印（Viewer:Https:CertificateThumbprint）が設定済み" +
                    "（HTTPS を意図した証跡がある）ため、平文（無効）へは倒さず閲覧リスナを開かない縮小継続と" +
                    "します（ADR-0022 決定 1——暗号化の意図を黙って外さない。Notification:Email:Smtp:Security の" +
                    "前例と同じ判断）"));

            return (ViewerHttpsMode.SuppressListener, thumbprint,
                "閲覧 HTTPS の有効/無効（Viewer:Https:Enabled）が真偽値として解釈できず、証明書拇印が" +
                "設定済み（HTTPS を意図した証跡がある）のため、平文では開かず閲覧リスナを停止しています。");
        }

        warnings.Add(new ConfigurationWarning(
            Key: "Viewer:Https:Enabled",
            InvalidValue: rawEnabled,
            AppliedValue: bool.FalseString,
            Reason: "真偽値として不正なため無効（平文 HTTP）を適用（証明書拇印も未設定で HTTPS が構成された" +
                "形跡がなく、閲覧リスナの全停止は釣り合わない——ADR-0022 決定 1）"));

        return (ViewerHttpsMode.Disabled, null, null);
    }
}
