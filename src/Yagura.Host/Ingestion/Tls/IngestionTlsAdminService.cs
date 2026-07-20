using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Administration;
using Yagura.Host.Administration.Https;
using Yagura.Host.Configuration;

namespace Yagura.Host.Ingestion.Tls;

/// <summary>
/// <see cref="IIngestionTlsAdminService"/> の実体（ADR-0019 決定 1・2・5。Issue #349）。
/// TLS 受信の既存 3 キーを、保存前 fail-closed 検証つきで <c>yagura.json</c> へ保存し、
/// 監査記録（イベント ID 2020）する。
/// </summary>
/// <remarks>
/// <para>
/// <b>管理リモート HTTPS 版（<see cref="AdminRemoteAccessAdminService"/>）の写しである</b>。
/// 証明書の実ストア解決は同一コード <see cref="AdminCertificateProvider.Load"/> を、EKU 判定と
/// 秘密鍵の読取検証は <see cref="WindowsCertificateStoreReader"/> の internal ヘルパを共有する
/// （ADR-0019 決定 1「列挙・検証の実装は共有し二重実装しない」）。
/// </para>
/// <para>
/// <b>管理 HTTPS と挙動が割れるのは 2 点だけで、いずれも仕様として確定した非対称である</b>
/// （実装の都合ではない。期限切れ側は security.md §6・§6.1、秘密鍵側の保存ポリシーは ADR-0019 決定 2 が正）:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>期限切れは拒否せず警告して通す</b>。TLS 受信は期限切れでも受信を止めない（security.md §6）
/// ——証明書を検証して接続を拒否するかは<b>送信側の判断</b>であり、受信側が先回りして止めると
/// 「送信元がサイレントに脱落する」事故を製品自ら起こす。起動時の TLS 証明書解決も
/// <see cref="AdminCertificateLoadResult.IsExpired"/> を判定に使っていない（<c>Program.cs</c>）ため、
/// 保存時に拒否すると<b>逆に</b>「UI では保存できないのに手編集なら動く」乖離になる
/// （決定 2「UI 事前検証と起動時検証の乖離ゼロ」）。
/// </description></item>
/// <item><description>
/// <b>秘密鍵の読取不可は警告ではなく拒否する</b>。管理 HTTPS は読めなくても loopback 経由で
/// 管理面へ逃げられる（縮小継続）が、TLS 受信は<b>秘密鍵がなければハンドシェイクが成立せず
/// 受信チャネルが丸ごと立たない</b>。「受信が止まる側」を拒否に倒す。
/// </description></item>
/// </list>
/// <para>
/// <b>証明書検証は拇印が指定されている限り <c>Enabled</c> の値によらず実施する</b>
/// （<see cref="AdminRemoteAccessAdminService"/> と同じ方針）——「証明書だけ先に設定しておく」
/// 段階的な準備でも、使えない証明書を無警告で保存させない。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class IngestionTlsAdminService : IIngestionTlsAdminService
{
    private const string EnabledKey = "Ingestion:Tls:Enabled";
    private const string CertificateThumbprintKey = "Ingestion:Tls:CertificateThumbprint";
    private const string PortKey = "Ingestion:Tls:Port";

    /// <summary>RFC 5425 の標準ポート（<c>Ingestion:Tls:Port</c> 未設定時の既定。configuration.md §8）。</summary>
    private const string DefaultPortDisplay = "6514";

    private readonly string _dataRoot;
    private readonly IAuditRecorder _auditRecorder;
    private readonly Func<string, AdminCertificateLoadResult> _loadCertificate;
    private readonly Func<X509Certificate2, bool> _hasServerAuthEku;
    private readonly Func<X509Certificate2, bool> _isPrivateKeyReadable;
    private readonly TimeProvider _timeProvider;

    public IngestionTlsAdminService(
        string dataRoot,
        IAuditRecorder auditRecorder,
        TimeProvider? timeProvider = null)
        : this(
            dataRoot,
            auditRecorder,
            AdminCertificateProvider.Load,
            WindowsCertificateStoreReader.HasServerAuthEku,
            WindowsCertificateStoreReader.IsPrivateKeyReadable,
            timeProvider)
    {
    }

    /// <summary>
    /// テスト用（ADR-0012 決定 5「実ストア接触は統合／E2E に限定する」）。証明書の解決・EKU 判定・
    /// 秘密鍵の読取検証を差し替えて、実ストアなしで保存前検証の分岐を決定的に検証できるようにする。
    /// </summary>
    internal IngestionTlsAdminService(
        string dataRoot,
        IAuditRecorder auditRecorder,
        Func<string, AdminCertificateLoadResult> loadCertificate,
        Func<X509Certificate2, bool> hasServerAuthEku,
        Func<X509Certificate2, bool> isPrivateKeyReadable,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(auditRecorder);
        ArgumentNullException.ThrowIfNull(loadCertificate);
        ArgumentNullException.ThrowIfNull(hasServerAuthEku);
        ArgumentNullException.ThrowIfNull(isPrivateKeyReadable);

        _dataRoot = dataRoot;
        _auditRecorder = auditRecorder;
        _loadCertificate = loadCertificate;
        _hasServerAuthEku = hasServerAuthEku;
        _isPrivateKeyReadable = isPrivateKeyReadable;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<IngestionTlsStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        return Task.FromResult(ToStatus(snapshot.Options));
    }

    public async Task<IngestionTlsConfigureResult> ConfigureAsync(
        bool enabled,
        string? certificateThumbprint,
        string? port,
        string? operatorAddress = null,
        string? operatorScheme = null,
        string? operatorPrincipal = null,
        CancellationToken cancellationToken = default)
    {
        // 入力の正規化（起動時検証 YaguraConfigurationLoader と同一の規則へ寄せる）。
        // 不正形式は起動時なら「縮小側で継続」だが、保存時は利用者が目の前にいるため親切に拒否する。
        string? normalizedThumbprint = null;
        if (!string.IsNullOrWhiteSpace(certificateThumbprint))
        {
            normalizedThumbprint = YaguraConfigurationLoader.TryNormalizeCertificateThumbprint(certificateThumbprint);
            if (normalizedThumbprint is null)
            {
                throw new WizardValidationException(
                    "証明書拇印は SHA-1 拇印（16 進 40 桁。空白・コロン・ハイフン区切りは可）として" +
                    "解釈できる形式で指定してください。証明書一覧から選択すると正しい拇印が自動で設定されます。");
            }
        }

        string? normalizedPort = null;
        if (!string.IsNullOrWhiteSpace(port))
        {
            if (!int.TryParse(port.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort)
                || parsedPort < 1
                || parsedPort > 65535)
            {
                throw new WizardValidationException(
                    "TLS 受信のポートは 1〜65535 の範囲の数値で指定してください" +
                    $"（未指定の場合は RFC 5425 の標準ポート {DefaultPortDisplay} が使われます）。");
            }

            normalizedPort = parsedPort.ToString(CultureInfo.InvariantCulture);
        }

        // 有効化するのに証明書が無い構成は拒否する（ADR-0019 決定 2 の拒否 1 項目目）。
        // 起動時はこの構成でも「拇印未設定」を理由に TLS リスナだけ開かず縮小継続（1016）するが、
        // 保存時点で気づける以上、無言で縮退する構成を保存させない。
        if (enabled && normalizedThumbprint is null)
        {
            throw new WizardValidationException(
                "TLS 受信を有効にするには証明書を選択してください。証明書が未設定のまま有効化すると、" +
                "起動時に TLS 受信の待ち受けだけが開かれない縮小継続になります" +
                "（平文 UDP/TCP 受信は影響を受けません）。");
        }

        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        var before = snapshot.Options;

        // 証明書の保存前検証（クラス remarks 参照）。拇印が指定されている限り Enabled によらず実施する。
        var expiredWarning = false;
        if (normalizedThumbprint is not null)
        {
            expiredWarning = ValidateCertificate(normalizedThumbprint);
        }

        // 変更差分の計算（実効値の比較——永続値の生文字列表記ゆれ（true/True 等）を変更と数えない）。
        var changedKeys = new List<string>();

        if (ParseBool(before.Ingestion?.Tls?.Enabled) != enabled)
        {
            changedKeys.Add(EnabledKey);
        }

        var beforeThumbprint = YaguraConfigurationLoader.TryNormalizeCertificateThumbprint(
            before.Ingestion?.Tls?.CertificateThumbprint);
        if (!string.Equals(beforeThumbprint, normalizedThumbprint, StringComparison.Ordinal))
        {
            changedKeys.Add(CertificateThumbprintKey);
        }

        if (!string.Equals(NormalizePortOrNull(before.Ingestion?.Tls?.Port), normalizedPort, StringComparison.Ordinal))
        {
            changedKeys.Add(PortKey);
        }

        if (changedKeys.Count == 0)
        {
            // no-op: 保存も監査もしない（同値保存の反復で監査証跡を希釈しない）。
            return new IngestionTlsConfigureResult(
                ChangedKeys: [],
                RequiredEffect: ConfigurationApplyEffect.Immediate,
                ExpiredWarning: expiredWarning,
                Status: ToStatus(before));
        }

        var after = YaguraConfigurationOptionsCloner.Clone(before);
        after.Ingestion ??= new YaguraConfigurationOptions.IngestionOptions();
        after.Ingestion.Tls ??= new YaguraConfigurationOptions.IngestionOptions.TlsOptions();

        after.Ingestion.Tls.Enabled = enabled.ToString();
        after.Ingestion.Tls.CertificateThumbprint = normalizedThumbprint;
        after.Ingestion.Tls.Port = normalizedPort;

        // 楽観競合（configuration.md §3）は ConfigurationConflictException をそのまま伝播する。
        YaguraConfigurationWriter.Save(_dataRoot, after, snapshot.VersionToken);

        // 監査（ADR-0019 決定 5・SEC-5）: 対象 3 キーの変更を独立 ID 2020 で記録する
        // （管理 HTTPS の 2012 と対になる TLS 受信版。Port も到達面の変更として対象に含める）。
        await _auditRecorder.RecordAsync(
            new AuditEvent(
                OccurredAt: _timeProvider.GetUtcNow(),
                Kind: AuditEventKind.IngestionTlsCertificateConfigured,
                RemoteAddress: operatorAddress,
                RemotePort: null,
                Detail:
                    $"{EnabledKey}={enabled} " +
                    $"{CertificateThumbprintKey}={normalizedThumbprint ?? "(未設定)"} " +
                    $"{PortKey}={normalizedPort ?? $"(既定 {DefaultPortDisplay})"} " +
                    $"changedKeys={string.Join(",", changedKeys)}",
                AuthenticationScheme: operatorScheme,
                AuthenticatedPrincipal: operatorPrincipal),
            CancellationToken.None).ConfigureAwait(false);

        return new IngestionTlsConfigureResult(
            ChangedKeys: changedKeys,
            RequiredEffect: ConfigurationApplyEffect.RestartRequired,
            ExpiredWarning: expiredWarning,
            Status: ToStatus(after));
    }

    /// <summary>
    /// 拇印の実ストア解決 + 保存前 fail-closed 検証（クラス remarks 参照）。
    /// 見つからない・秘密鍵なし・秘密鍵が読めない・serverAuth EKU 不適合は
    /// <see cref="WizardValidationException"/> で拒否し、<b>期限切れのみ警告として返す</b>。
    /// </summary>
    /// <returns>証明書が有効期間外なら <see langword="true"/>（警告。保存は継続する）。</returns>
    private bool ValidateCertificate(string normalizedThumbprint)
    {
        var loadResult = _loadCertificate(normalizedThumbprint);

        if (!loadResult.Succeeded)
        {
            // 起動時（1016 経路）と同一コードの失敗理由をそのまま表示する（乖離ゼロ）。
            throw new WizardValidationException(
                loadResult.FailureReason! +
                " この構成のまま再起動すると、TLS 受信の待ち受けが開かれない縮小継続になります" +
                "（平文 UDP/TCP 受信は影響を受けません）。");
        }

        var certificate = loadResult.Certificate!;
        try
        {
            if (!_hasServerAuthEku(certificate))
            {
                throw new WizardValidationException(
                    $"選択された証明書（拇印 {normalizedThumbprint}）はサーバー認証の拡張キー使用法" +
                    $"（serverAuth EKU {WindowsCertificateStoreReader.ServerAuthEkuOid}）を持たないため、" +
                    "TLS 受信には使用できません。サーバー認証用途の証明書を選択してください。");
            }

            // 管理 HTTPS では警告に留める分岐だが、TLS 受信では拒否する（クラス remarks の 2 点目）。
            if (!_isPrivateKeyReadable(certificate))
            {
                throw new WizardValidationException(
                    $"選択された証明書（拇印 {normalizedThumbprint}）の秘密鍵に、サービスアカウントが" +
                    "アクセスできません。秘密鍵を読めないと TLS ハンドシェイクが成立せず、TLS 受信は" +
                    "まったく機能しません。証明書スナップイン（certlm.msc）で対象の証明書を右クリックし" +
                    "「すべてのタスク」→「秘密キーの管理」から、サービスアカウントへ読み取り権限を" +
                    "付与してから選択し直してください（configuration.md §6 CF-D2）。");
            }

            // 期限切れは拒否しない（クラス remarks の 1 点目）。警告として呼び出し元へ返す。
            return loadResult.IsExpired;
        }
        finally
        {
            certificate.Dispose();
        }
    }

    private static IngestionTlsStatus ToStatus(YaguraConfigurationOptions options)
    {
        var rawThumbprint = options.Ingestion?.Tls?.CertificateThumbprint;
        var thumbprint = string.IsNullOrWhiteSpace(rawThumbprint)
            ? null
            : YaguraConfigurationLoader.TryNormalizeCertificateThumbprint(rawThumbprint) ?? rawThumbprint;

        return new IngestionTlsStatus(
            Enabled: ParseBool(options.Ingestion?.Tls?.Enabled),
            CertificateThumbprint: thumbprint,
            Port: options.Ingestion?.Tls?.Port);
    }

    /// <summary>
    /// 永続値のポート表記を比較用の正規形（10 進・前後空白なし）へ写像する。ポートとして
    /// 解釈できない値は未設定（= 既定 6514）と同じ扱い——起動時の「不正値は既定値で継続」
    /// （configuration.md §8）と同じ解釈で差分を数える。
    /// </summary>
    private static string? NormalizePortOrNull(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            && port is >= 1 and <= 65535
            ? port.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static bool ParseBool(string? raw) => bool.TryParse(raw, out var value) && value;
}
