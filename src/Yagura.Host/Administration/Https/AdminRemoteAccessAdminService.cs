using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Configuration;

namespace Yagura.Host.Administration.Https;

/// <summary>
/// <see cref="IAdminRemoteAccessAdminService"/> の実体（ADR-0012 決定 1・4・7。B2 増分）。
/// 管理リスナのリモートバインド + HTTPS の既存 4 キーを、保存前 fail-closed 検証つきで
/// <c>yagura.json</c> へ保存し、監査記録（イベント ID 2011/2012）する。
/// </summary>
/// <remarks>
/// <para>
/// <b>保存前 fail-closed 検証は永続値に対して行う（ADR-0012 決定 4）</b>: 認証の有効状態は
/// 保存要求の引数ではなく <see cref="YaguraConfigurationWriter.Read"/> の現在値から判定する。
/// 画面分割によって「認証画面から認証を切ると、本画面で有効化済みの RemoteBinding と組んで
/// fail-closed をすり抜ける」実装ミスを防ぐ（認証側の対称検証は
/// <see cref="AdminAuthentication.AdminAuthenticationAdminService"/> の Phase 2 分岐が担う）。
/// 拒否は「UI 層で親切に拒否（<see cref="WizardValidationException"/>）→ 起動時検証
/// （<see cref="YaguraConfigurationLoader"/> = イベント ID 1012）が最終防衛線」の二段構え。
/// </para>
/// <para>
/// <b>証明書検証の乖離ゼロ（ADR-0012 決定 4 = D-6）</b>: 拇印の実ストア解決は起動時
/// （<c>Program.cs</c> の管理リスナ bind 前評価 = イベント ID 1013 経路）と同一コード
/// <see cref="AdminCertificateProvider.Load"/> へ寄せる。<b>期限切れは拒否する</b>——起動時評価は
/// <see cref="AdminCertificateLoadResult.Succeeded"/> でも <see cref="AdminCertificateLoadResult.IsExpired"/>
/// なら証明書を「未解決」として扱い、リモート HTTPS の bind エントリを開かずに縮小継続（1013）する
/// （<c>Program.cs</c> の <c>adminHttpsCertificateUnavailableReason</c> への分岐——TLS 受信の
/// 「期限切れでも受け入れる」非対称とは異なる）ため、期限切れを警告付きで通すと「事前検証が緑なのに
/// 再起動後に 1013 が起きる」乖離になる。加えて serverAuth EKU 不適合を拒否する（列挙 UI
/// = <see cref="StoreAdminCertificateStoreReader"/> と同じ最小化。起動時検証は EKU を見ないため
/// こちらが厳しい側であり、D-6「緑なら縮小継続が起きない」は保たれる）。
/// </para>
/// <para>
/// <b>秘密鍵の読取不可は拒否ではなく警告（ADR-0012 決定 3 = (b)）</b>: サービスアカウントが
/// 秘密鍵を読めない場合も保存自体は成功させ、結果 DTO の警告フラグで返す——起動時も縮小継続で
/// あってサービス全体は止まらず、付与（<c>certlm.msc</c> の CF-D2 手動手順）後に再起動すれば
/// そのまま有効になるため。UI（B3）は警告時に付与手順へ誘導する。
/// </para>
/// <para>
/// <b>ADR-0012 決定 7 後半（2009/2010 への操作者 additive 追加）は本増分では行わない</b>:
/// 決定 3 が (b)（UI からの ACL 付与なし・起動時 best-effort のみ）へ縮退し、2009/2010 の発火点は
/// 対話操作者が存在しない起動時自動処理のままのため、操作者フィールドを追加しても常に空になる。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AdminRemoteAccessAdminService : IAdminRemoteAccessAdminService
{
    private const string RemoteBindingEnabledKey = "Admin:RemoteBinding:Enabled";
    private const string HttpsEnabledKey = "Admin:Https:Enabled";
    private const string CertificateThumbprintKey = "Admin:Https:CertificateThumbprint";
    private const string HttpsPortKey = "Admin:Https:Port";

    private readonly string _dataRoot;
    private readonly IAuditRecorder _auditRecorder;
    private readonly Func<string, AdminCertificateLoadResult> _loadCertificate;
    private readonly Func<X509Certificate2, bool> _hasServerAuthEku;
    private readonly Func<X509Certificate2, bool> _isPrivateKeyReadable;
    private readonly TimeProvider _timeProvider;

    public AdminRemoteAccessAdminService(
        string dataRoot,
        IAuditRecorder auditRecorder,
        TimeProvider? timeProvider = null)
        : this(
            dataRoot,
            auditRecorder,
            AdminCertificateProvider.Load,
            StoreAdminCertificateStoreReader.HasServerAuthEku,
            StoreAdminCertificateStoreReader.IsPrivateKeyReadable,
            timeProvider)
    {
    }

    /// <summary>
    /// 証明書検証部分を差し替え可能にするテスト用コンストラクタ（ADR-0012 決定 5:
    /// 実マシンの証明書ストアに依存する部分を偽実装で決定的に検証する。実ストア接触は
    /// 統合／E2E に限定する）。公開コンストラクタは起動時検証と同一の実物
    /// （<see cref="AdminCertificateProvider.Load"/> ほか）を既定として束ねる。
    /// </summary>
    internal AdminRemoteAccessAdminService(
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

    public Task<AdminRemoteAccessStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        return Task.FromResult(ToStatus(snapshot.Options));
    }

    public async Task<AdminRemoteAccessConfigureResult> ConfigureAsync(
        bool remoteBindingEnabled,
        bool httpsEnabled,
        string? certificateThumbprint,
        string? httpsPort,
        string? operatorAddress = null,
        string? operatorScheme = null,
        string? operatorPrincipal = null,
        CancellationToken cancellationToken = default)
    {
        // 入力の正規化（起動時検証 YaguraConfigurationLoader と同一の規則へ寄せる）。
        // 不正形式は起動時なら「縮小側で継続」（未構成扱い）だが、保存時は利用者が目の前に
        // いるため親切に拒否する（不正値を黙って落とすより是正を促す）。
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
        if (!string.IsNullOrWhiteSpace(httpsPort))
        {
            if (!int.TryParse(httpsPort.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                || port < 1
                || port > 65535)
            {
                throw new WizardValidationException(
                    "リモート HTTPS のポートは 1〜65535 の範囲の数値で指定してください" +
                    "（未指定の場合は既定の 8516 が使われます）。");
            }

            normalizedPort = port.ToString(CultureInfo.InvariantCulture);
        }

        // fail-closed 不変条件（ADR-0012 決定 4）: 認証の有効状態は保存要求の引数ではなく
        // 永続化された現在値から判定する（クラス remarks 参照）。起動時 fail-closed 検証
        // （イベント ID 1012 = 起動拒否）と対称の判定・文言で、保存時点で先に拒否する。
        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        var before = snapshot.Options;

        if (remoteBindingEnabled)
        {
            var missing = new List<string>();

            var authenticationConfigured =
                ParseBool(before.Admin?.Authentication?.Windows?.Enabled) ||
                ParseBool(before.Admin?.Authentication?.App?.Enabled);
            if (!authenticationConfigured)
            {
                missing.Add("認証方式（Windows 統合認証またはアプリ独自認証。管理画面の認証設定から先に有効化してください）");
            }

            if (!httpsEnabled)
            {
                missing.Add("HTTPS の有効化（Admin:Https:Enabled）");
            }

            if (normalizedThumbprint is null)
            {
                missing.Add("証明書の選択（Admin:Https:CertificateThumbprint）");
            }

            if (missing.Count > 0)
            {
                throw new WizardValidationException(
                    "管理リスナのリモートバインド（Admin:RemoteBinding:Enabled）を有効にするには、" +
                    "次の前提条件をすべて満たす必要があります: " + string.Join(" / ", missing) + "。" +
                    "認証または通信保護を欠いたリモート公開を防ぐため、この構成のまま再起動すると" +
                    "起動時の fail-closed 検証がサービスの起動を拒否し、syslog 受信も停止します" +
                    "（ADR-0010 Phase 2 決定 1・4 の不変条件）。");
            }
        }

        // 証明書の保存前検証（ADR-0012 決定 4 = D-6。拇印が指定されている限り、RemoteBinding の
        // 有効/無効にかかわらず実施する——「HTTPS や拇印だけ先に設定する」段階的な準備でも、
        // 使えない証明書を無警告で保存させない）。
        var privateKeyUnreadableWarning = false;
        if (normalizedThumbprint is not null)
        {
            privateKeyUnreadableWarning = ValidateCertificate(normalizedThumbprint);
        }

        // 変更差分の計算（実効値の比較——永続値の生文字列表記ゆれ（true/True 等）を変更と
        // 数えない。起動時の解釈（ResolveSecurityFlag・拇印/ポートの正規化）と同じ土俵で比べる）。
        var changedKeys = new List<string>();

        if (ParseBool(before.Admin?.RemoteBinding?.Enabled) != remoteBindingEnabled)
        {
            changedKeys.Add(RemoteBindingEnabledKey);
        }

        if (ParseBool(before.Admin?.Https?.Enabled) != httpsEnabled)
        {
            changedKeys.Add(HttpsEnabledKey);
        }

        var beforeThumbprint = YaguraConfigurationLoader.TryNormalizeCertificateThumbprint(
            before.Admin?.Https?.CertificateThumbprint);
        if (!string.Equals(beforeThumbprint, normalizedThumbprint, StringComparison.Ordinal))
        {
            changedKeys.Add(CertificateThumbprintKey);
        }

        if (!string.Equals(NormalizePortOrNull(before.Admin?.Https?.Port), normalizedPort, StringComparison.Ordinal))
        {
            changedKeys.Add(HttpsPortKey);
        }

        if (changedKeys.Count == 0)
        {
            // no-op: 保存も監査もしない（同値保存の反復で監査証跡を希釈しない）。
            return new AdminRemoteAccessConfigureResult(
                ChangedKeys: [],
                RequiredEffect: ConfigurationApplyEffect.Immediate,
                PrivateKeyUnreadableWarning: privateKeyUnreadableWarning,
                Status: ToStatus(before));
        }

        var after = YaguraConfigurationOptionsCloner.Clone(before);
        after.Admin ??= new YaguraConfigurationOptions.AdminOptions();
        after.Admin.RemoteBinding ??= new YaguraConfigurationOptions.AdminOptions.RemoteBindingOptions();
        after.Admin.Https ??= new YaguraConfigurationOptions.AdminOptions.HttpsOptions();

        after.Admin.RemoteBinding.Enabled = remoteBindingEnabled.ToString();
        after.Admin.Https.Enabled = httpsEnabled.ToString();
        after.Admin.Https.CertificateThumbprint = normalizedThumbprint;
        after.Admin.Https.Port = normalizedPort;

        // 楽観競合（configuration.md §3）は AdminAuthenticationAdminService と同じく
        // ConfigurationConflictException をそのまま伝播する（上書きせず再読み込みを促す）。
        YaguraConfigurationWriter.Save(_dataRoot, after, snapshot.VersionToken);

        // 監査（ADR-0012 決定 7・SEC-5）: 「機の公開」（RemoteBinding）と「証明書系の変更」
        // （Https:*）を独立 ID（2011/2012）で記録する——2006（認証設定変更）へ畳み込まない。
        // 実際に値が変わったキー群に対応する ID のみを記録する（変更ゼロは上で return 済み）。
        var now = _timeProvider.GetUtcNow();

        if (changedKeys.Contains(RemoteBindingEnabledKey))
        {
            await _auditRecorder.RecordAsync(
                new AuditEvent(
                    OccurredAt: now,
                    Kind: AuditEventKind.AdminRemoteBindingConfigured,
                    RemoteAddress: operatorAddress,
                    RemotePort: null,
                    Detail: $"{RemoteBindingEnabledKey}={remoteBindingEnabled}",
                    AuthenticationScheme: operatorScheme,
                    AuthenticatedPrincipal: operatorPrincipal),
                CancellationToken.None).ConfigureAwait(false);
        }

        var changedHttpsKeys = changedKeys.Where(key => key != RemoteBindingEnabledKey).ToList();
        if (changedHttpsKeys.Count > 0)
        {
            // 拇印は公開情報（証明書の識別子）であり秘密ではないため新値をそのまま残す
            // （ADR-0012 決定 7。security.md §4.1 の「秘密情報キーは値を載せない」の対象外）。
            await _auditRecorder.RecordAsync(
                new AuditEvent(
                    OccurredAt: now,
                    Kind: AuditEventKind.AdminHttpsCertificateConfigured,
                    RemoteAddress: operatorAddress,
                    RemotePort: null,
                    Detail:
                        $"{HttpsEnabledKey}={httpsEnabled} " +
                        $"{CertificateThumbprintKey}={normalizedThumbprint ?? "(未設定)"} " +
                        $"{HttpsPortKey}={normalizedPort ?? "(既定 8516)"} " +
                        $"changedKeys={string.Join(",", changedHttpsKeys)}",
                    AuthenticationScheme: operatorScheme,
                    AuthenticatedPrincipal: operatorPrincipal),
                CancellationToken.None).ConfigureAwait(false);
        }

        return new AdminRemoteAccessConfigureResult(
            ChangedKeys: changedKeys,
            RequiredEffect: ConfigurationApplyEffect.RestartRequired,
            PrivateKeyUnreadableWarning: privateKeyUnreadableWarning,
            Status: ToStatus(after));
    }

    /// <summary>
    /// 拇印の実ストア解決 + 保存前 fail-closed 検証（クラス remarks の D-6 節参照）。
    /// 使用不能（見つからない・秘密鍵なし・期限切れ・serverAuth EKU 不適合）は
    /// <see cref="WizardValidationException"/> で拒否し、秘密鍵の読取不可のみ警告として返す。
    /// </summary>
    /// <returns>秘密鍵の読取検証が不可なら <see langword="true"/>（警告。保存は継続する）。</returns>
    private bool ValidateCertificate(string normalizedThumbprint)
    {
        var loadResult = _loadCertificate(normalizedThumbprint);

        if (!loadResult.Succeeded)
        {
            // 起動時（1013 経路）と同一コードの失敗理由をそのまま表示する（D-6 乖離ゼロ）。
            throw new WizardValidationException(
                loadResult.FailureReason! +
                " この構成のまま再起動すると、リモート HTTPS の待ち受けが開かれない縮小継続になります。");
        }

        var certificate = loadResult.Certificate!;
        try
        {
            if (loadResult.IsExpired)
            {
                // 起動時評価（Program.cs）は IsExpired を「未解決」として bind をスキップし
                // 縮小継続（1013）するため、保存時も同じ側 = 拒否に揃える（クラス remarks 参照）。
                throw new WizardValidationException(
                    $"選択された証明書（拇印 {normalizedThumbprint}）は有効期間外です" +
                    $"（NotBefore={certificate.NotBefore:O}, NotAfter={certificate.NotAfter:O}）。" +
                    "起動時の証明書解決は有効期間外の証明書を受け付けず、リモート HTTPS の待ち受けが" +
                    "開かれない縮小継続になるため保存を中止しました。有効期間内の証明書を選択してください。");
            }

            if (!_hasServerAuthEku(certificate))
            {
                throw new WizardValidationException(
                    $"選択された証明書（拇印 {normalizedThumbprint}）はサーバー認証の拡張キー使用法" +
                    $"（serverAuth EKU {StoreAdminCertificateStoreReader.ServerAuthEkuOid}）を持たないため、" +
                    "管理リスナのリモート HTTPS には使用できません。サーバー認証用途の証明書を選択してください。");
            }

            return !_isPrivateKeyReadable(certificate);
        }
        finally
        {
            certificate.Dispose();
        }
    }

    private static AdminRemoteAccessStatus ToStatus(YaguraConfigurationOptions options)
    {
        var rawThumbprint = options.Admin?.Https?.CertificateThumbprint;
        var thumbprint = string.IsNullOrWhiteSpace(rawThumbprint)
            ? null
            : YaguraConfigurationLoader.TryNormalizeCertificateThumbprint(rawThumbprint) ?? rawThumbprint;

        return new AdminRemoteAccessStatus(
            RemoteBindingEnabled: ParseBool(options.Admin?.RemoteBinding?.Enabled),
            HttpsEnabled: ParseBool(options.Admin?.Https?.Enabled),
            CertificateThumbprint: thumbprint,
            HttpsPort: options.Admin?.Https?.Port,
            WindowsAuthEnabled: ParseBool(options.Admin?.Authentication?.Windows?.Enabled),
            AppAuthEnabled: ParseBool(options.Admin?.Authentication?.App?.Enabled));
    }

    /// <summary>
    /// 永続値のポート表記を比較用の正規形（10 進・前後空白なし）へ写像する。ポートとして
    /// 解釈できない値は未設定（= 既定 8516）と同じ扱い——起動時の「不正値は既定値で継続」
    /// （<see cref="YaguraConfigurationLoader"/>）と同じ解釈で差分を数える。
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
