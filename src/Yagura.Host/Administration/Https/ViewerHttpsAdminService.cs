using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Configuration;

namespace Yagura.Host.Administration.Https;

/// <summary>
/// <see cref="IViewerHttpsAdminService"/> の実体（ADR-0022 決定 3）。
/// 閲覧 HTTPS の 2 キーを、保存前 fail-closed 検証 + SAN 助言検査つきで <c>yagura.json</c> へ保存し、
/// 監査記録（イベント ID 2029）する。
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Yagura.Host.Ingestion.Tls.IngestionTlsAdminService"/>（ADR-0019）の写しであり、
/// 証明書設定サービスの 3 例目である</b>。証明書の実ストア解決は <see cref="CertificateProvider.Load"/>、
/// EKU 判定と秘密鍵の読取検証は <see cref="WindowsCertificateStoreReader"/> の internal ヘルパを
/// 共有する（決定 3「三重実装しない」）。
/// </para>
/// <para>
/// <b>検証ポリシー（決定 3——二分宣言）</b>: 拒否 = 拇印未設定（有効化時）・形式不正・証明書不在・
/// <b>期限切れ</b>（停止する面は拒否側——ADR-0019 決定 2 の非対称。管理 HTTPS と同じ側）・
/// <b>秘密鍵読取不可</b>（読めないまま再起動すると閲覧リスナが縮小継続で開かず LAN 閲覧が止まる——
/// 「止まる側を拒否に倒す」）・EKU 不適合。警告して通す = <b>SAN ホスト名不整合</b>（決定 4。
/// 利用者が閲覧に使う名前をサーバは断定できないため助言に留める）。
/// </para>
/// <para>
/// <b>起動時挙動との乖離について</b>: 起動時（<c>Program.cs</c>）は期限切れ・秘密鍵不可を
/// 「縮小継続」（1035——閲覧リスナを開かない）で扱い、保存時は拒否する。これは「UI 事前検証が
/// 緑なら起動後に想定外の縮小継続が起きない」（ADR-0012 決定 4 = D-6）の方向そのもの——UI が
/// 厳しい側であり、手編集は引き続き可能（縮小継続で受け止める）。
/// </para>
/// <para>
/// <b>証明書検証は拇印が指定されている限り <c>Enabled</c> の値によらず実施する</b>（既存 2 サービスと
/// 同じ方針）——「証明書だけ先に設定しておく」段階的な準備でも、使えない証明書を無警告で保存させない。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ViewerHttpsAdminService : IViewerHttpsAdminService
{
    private const string EnabledKey = "Viewer:Https:Enabled";
    private const string CertificateThumbprintKey = "Viewer:Https:CertificateThumbprint";

    private readonly string _dataRoot;
    private readonly IAuditRecorder _auditRecorder;
    private readonly Func<string, CertificateLoadResult> _loadCertificate;
    private readonly Func<X509Certificate2, bool> _hasServerAuthEku;
    private readonly Func<X509Certificate2, bool> _isPrivateKeyReadable;
    private readonly Func<IReadOnlyList<string>> _getLocalServerNames;
    private readonly TimeProvider _timeProvider;

    public ViewerHttpsAdminService(
        string dataRoot,
        IAuditRecorder auditRecorder,
        TimeProvider? timeProvider = null)
        : this(
            dataRoot,
            auditRecorder,
            CertificateProvider.Load,
            WindowsCertificateStoreReader.HasServerAuthEku,
            WindowsCertificateStoreReader.IsPrivateKeyReadable,
            ViewerHttpsSanInspector.GetLocalServerNames,
            timeProvider)
    {
    }

    /// <summary>
    /// テスト用（ADR-0012 決定 5「実ストア接触は統合／E2E に限定する」）。証明書の解決・EKU 判定・
    /// 秘密鍵の読取検証・サーバ名の取得を差し替えて、実ストア・実マシン名なしで保存前検証の分岐を
    /// 決定的に検証できるようにする。
    /// </summary>
    internal ViewerHttpsAdminService(
        string dataRoot,
        IAuditRecorder auditRecorder,
        Func<string, CertificateLoadResult> loadCertificate,
        Func<X509Certificate2, bool> hasServerAuthEku,
        Func<X509Certificate2, bool> isPrivateKeyReadable,
        Func<IReadOnlyList<string>> getLocalServerNames,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(auditRecorder);
        ArgumentNullException.ThrowIfNull(loadCertificate);
        ArgumentNullException.ThrowIfNull(hasServerAuthEku);
        ArgumentNullException.ThrowIfNull(isPrivateKeyReadable);
        ArgumentNullException.ThrowIfNull(getLocalServerNames);

        _dataRoot = dataRoot;
        _auditRecorder = auditRecorder;
        _loadCertificate = loadCertificate;
        _hasServerAuthEku = hasServerAuthEku;
        _isPrivateKeyReadable = isPrivateKeyReadable;
        _getLocalServerNames = getLocalServerNames;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<ViewerHttpsStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        return Task.FromResult(ToStatus(snapshot.Options));
    }

    public async Task<ViewerHttpsConfigureResult> ConfigureAsync(
        bool enabled,
        string? certificateThumbprint,
        string? operatorAddress = null,
        string? operatorScheme = null,
        string? operatorPrincipal = null,
        CancellationToken cancellationToken = default)
    {
        // 入力の正規化（起動時検証 YaguraConfigurationLoader と同一の規則へ寄せる）。
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

        // 有効化するのに証明書が無い構成は拒否する。起動時はこの構成でも縮小継続（1035——閲覧
        // リスナだけ開かない）で受け止めるが、保存時点で気づける以上、無言で縮退する構成を保存させない。
        if (enabled && normalizedThumbprint is null)
        {
            throw new WizardValidationException(
                "閲覧 UI の HTTPS を有効にするには証明書を選択してください。証明書が未設定のまま有効化すると、" +
                "再起動後に閲覧リスナが開かれない縮小継続になり、LAN からログを閲覧できなくなります" +
                "（syslog 受信・管理画面は影響を受けません）。");
        }

        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        var before = snapshot.Options;

        // 証明書の保存前検証 + SAN 助言検査（クラス remarks 参照）。
        ViewerHttpsSanInspection? sanInspection = null;
        if (normalizedThumbprint is not null)
        {
            sanInspection = ValidateCertificateAndInspectSan(normalizedThumbprint);
        }

        // 変更差分の計算（実効値の比較——永続値の生文字列表記ゆれを変更と数えない）。
        var changedKeys = new List<string>();

        if (ParseBool(before.Viewer?.Https?.Enabled) != enabled)
        {
            changedKeys.Add(EnabledKey);
        }

        var beforeThumbprint = YaguraConfigurationLoader.TryNormalizeCertificateThumbprint(
            before.Viewer?.Https?.CertificateThumbprint);
        if (!string.Equals(beforeThumbprint, normalizedThumbprint, StringComparison.Ordinal))
        {
            changedKeys.Add(CertificateThumbprintKey);
        }

        if (changedKeys.Count == 0)
        {
            return new ViewerHttpsConfigureResult(
                ChangedKeys: [],
                RequiredEffect: ConfigurationApplyEffect.Immediate,
                SanInspection: sanInspection,
                Status: ToStatus(before));
        }

        var after = YaguraConfigurationOptionsCloner.Clone(before);
        after.Viewer ??= new YaguraConfigurationOptions.ViewerOptions();
        after.Viewer.Https ??= new YaguraConfigurationOptions.ViewerOptions.HttpsOptions();

        after.Viewer.Https.Enabled = enabled.ToString();
        after.Viewer.Https.CertificateThumbprint = normalizedThumbprint;

        // 楽観競合（configuration.md §3）は ConfigurationConflictException をそのまま伝播する。
        YaguraConfigurationWriter.Save(_dataRoot, after, snapshot.VersionToken);

        // 監査（決定 10・SEC-5）: 対象 2 キーの変更を独立 ID 2029 で記録する（2012/2020 の閲覧版）。
        await _auditRecorder.RecordAsync(
            new AuditEvent(
                OccurredAt: _timeProvider.GetUtcNow(),
                Kind: AuditEventKind.ViewerHttpsConfigured,
                RemoteAddress: operatorAddress,
                RemotePort: null,
                Detail:
                    $"{EnabledKey}={enabled} " +
                    $"{CertificateThumbprintKey}={normalizedThumbprint ?? "(未設定)"} " +
                    $"changedKeys={string.Join(",", changedKeys)}",
                AuthenticationScheme: operatorScheme,
                AuthenticatedPrincipal: operatorPrincipal),
            CancellationToken.None).ConfigureAwait(false);

        return new ViewerHttpsConfigureResult(
            ChangedKeys: changedKeys,
            RequiredEffect: ConfigurationApplyEffect.RestartRequired,
            SanInspection: sanInspection,
            Status: ToStatus(after));
    }

    public Task<ViewerHttpsSanInspection?> InspectCertificateAsync(
        string certificateThumbprint, CancellationToken cancellationToken = default)
    {
        var normalized = YaguraConfigurationLoader.TryNormalizeCertificateThumbprint(certificateThumbprint);
        if (normalized is null)
        {
            return Task.FromResult<ViewerHttpsSanInspection?>(null);
        }

        var loadResult = _loadCertificate(normalized);
        if (!loadResult.Succeeded)
        {
            return Task.FromResult<ViewerHttpsSanInspection?>(null);
        }

        var certificate = loadResult.Certificate!;
        try
        {
            return Task.FromResult<ViewerHttpsSanInspection?>(
                ViewerHttpsSanInspector.Inspect(certificate, _getLocalServerNames()));
        }
        finally
        {
            certificate.Dispose();
        }
    }

    /// <summary>
    /// 拇印の実ストア解決 + 保存前 fail-closed 検証（クラス remarks の二分宣言）。拒否は
    /// <see cref="WizardValidationException"/>、SAN 不整合のみ検査結果として返す（警告して通す）。
    /// </summary>
    private ViewerHttpsSanInspection ValidateCertificateAndInspectSan(string normalizedThumbprint)
    {
        var loadResult = _loadCertificate(normalizedThumbprint);

        if (!loadResult.Succeeded)
        {
            // 起動時（1035 経路）と同一コードの失敗理由をそのまま表示する（乖離ゼロ）。
            throw new WizardValidationException(
                loadResult.FailureReason! +
                " この構成のまま再起動すると、閲覧リスナが開かれない縮小継続になり、LAN からログを" +
                "閲覧できなくなります（syslog 受信・管理画面は影響を受けません）。");
        }

        var certificate = loadResult.Certificate!;
        try
        {
            if (!_hasServerAuthEku(certificate))
            {
                throw new WizardValidationException(
                    $"選択された証明書（拇印 {normalizedThumbprint}）はサーバー認証の拡張キー使用法" +
                    $"（serverAuth EKU {WindowsCertificateStoreReader.ServerAuthEkuOid}）を持たないため、" +
                    "閲覧 UI の HTTPS には使用できません。サーバー認証用途の証明書を選択してください。");
            }

            // 期限切れは拒否する（管理 HTTPS と同じ拒否側。TLS 受信の「警告して通す」とは逆——
            // 期限切れで停止する面は保存を拒否し、継続する面は警告で通す。ADR-0019 決定 2 の非対称）。
            if (loadResult.IsExpired)
            {
                throw new WizardValidationException(
                    $"選択された証明書（拇印 {normalizedThumbprint}）は有効期間外です" +
                    $"（NotBefore={certificate.NotBefore:yyyy-MM-dd}, NotAfter={certificate.NotAfter:yyyy-MM-dd}）。" +
                    "閲覧 UI の HTTPS は期限切れ証明書では動作せず（HTTPS リスナは停止し、平文 HTTP へは" +
                    "落としません）、保存しても再起動後に閲覧リスナが開かれない縮小継続になります。" +
                    "有効期間内の証明書を選択してください。");
            }

            // 秘密鍵の読取不可も拒否する（ADR-0022 決定 4 の拒否側宣言——読めないまま再起動すると
            // LAN 閲覧が止まる。「止まる側を拒否に倒す」——TLS 受信と同じ側）。
            if (!_isPrivateKeyReadable(certificate))
            {
                throw new WizardValidationException(
                    $"選択された証明書（拇印 {normalizedThumbprint}）の秘密鍵に、サービスアカウントが" +
                    "アクセスできません。秘密鍵を読めないと TLS ハンドシェイクが成立せず、閲覧 UI に" +
                    "誰も接続できなくなります。証明書スナップイン（certlm.msc）で対象の証明書を右クリックし" +
                    "「すべてのタスク」→「秘密キーの管理」から、サービスアカウントへ読み取り権限を" +
                    "付与してから選択し直してください（configuration.md §6 CF-D2。起動時にも自動付与を" +
                    "試みますが、成功に依存しないでください）。");
            }

            // SAN ホスト名整合は「警告して通す」側（決定 4——助言に留める）。
            return ViewerHttpsSanInspector.Inspect(certificate, _getLocalServerNames());
        }
        finally
        {
            certificate.Dispose();
        }
    }

    private static ViewerHttpsStatus ToStatus(YaguraConfigurationOptions options)
    {
        var rawThumbprint = options.Viewer?.Https?.CertificateThumbprint;
        var thumbprint = string.IsNullOrWhiteSpace(rawThumbprint)
            ? null
            : YaguraConfigurationLoader.TryNormalizeCertificateThumbprint(rawThumbprint) ?? rawThumbprint;

        return new ViewerHttpsStatus(
            Enabled: ParseBool(options.Viewer?.Https?.Enabled),
            CertificateThumbprint: thumbprint,
            ViewerPort: string.IsNullOrWhiteSpace(options.Viewer?.HttpPort) ? null : options.Viewer.HttpPort,
            AdminHttpsCertificateThumbprint: YaguraConfigurationLoader.TryNormalizeCertificateThumbprint(
                options.Admin?.Https?.CertificateThumbprint),
            EmailNotificationEnabled: ParseBool(options.Notification?.Email?.Enabled));
    }

    private static bool ParseBool(string? raw) => bool.TryParse(raw, out var value) && value;
}
