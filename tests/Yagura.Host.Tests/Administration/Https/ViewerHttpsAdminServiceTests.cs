using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Administration.Https;
using Yagura.Host.Configuration;

namespace Yagura.Host.Tests.Administration.Https;

/// <summary>
/// <see cref="ViewerHttpsAdminService"/> の保存前 fail-closed 検証・SAN 助言検査・差分計算・
/// 監査記録の回帰テスト（ADR-0022 決定 3・4・9・10。Issue #455 段階 ②）。
/// </summary>
/// <remarks>
/// 実ストア・実マシン名には触れず、証明書の解決・EKU 判定・秘密鍵の読取検証・サーバ名の取得を
/// 注入して分岐を決定的に検証する（ADR-0012 決定 5 の踏襲）。
/// <para>
/// <b>本テストの主眼は閲覧 HTTPS の検証ポリシーの固定</b>——期限切れ・秘密鍵読取不可は
/// <b>拒否</b>（管理 HTTPS = 期限切れ拒否・TLS 受信 = 秘密鍵拒否、の「停止する面」側の組み合わせ）、
/// SAN 不整合のみ<b>警告して通す</b>（決定 4——助言に留める）。実装で逆に倒れると
/// 「保存できたのに再起動後に LAN 閲覧が止まる」乖離、または「正しい証明書なのに保存できない」
/// 過剰拒否になる。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ViewerHttpsAdminServiceTests : IDisposable
{
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private const string ValidThumbprint = "A1B2C3D4E5F60718293A4B5C6D7E8F9012345678";
    private static readonly string[] ServerNames = ["YAGURA01", "yagura01.example.local"];

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-viewer-https-admin-{Guid.NewGuid():N}");
    private readonly RecordingAuditRecorder _audit = new();

    public ViewerHttpsAdminServiceTests() => Directory.CreateDirectory(_dataRoot);

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // 検証ポリシー（決定 3・4 の二分宣言）の固定
    // ------------------------------------------------------------------

    /// <summary>
    /// 期限切れ証明書は<b>拒否する</b>（管理 HTTPS と同じ停止面の扱い。TLS 受信の
    /// 「警告して通す」とは逆——ADR-0019 決定 2 の非対称の閲覧側適用）。
    /// </summary>
    [Fact]
    public async Task ConfigureAsync_ExpiredCertificate_Throws()
    {
        var service = CreateService(
            loadCertificate: _ => CertificateLoadResult.Success(CreateCertificate("CN=yagura-viewer-expired"), isExpired: true));

        var ex = await Assert.ThrowsAsync<WizardValidationException>(
            () => service.ConfigureAsync(enabled: true, certificateThumbprint: ValidThumbprint));

        Assert.Contains("有効期間外", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_audit.Recorded);
    }

    /// <summary>秘密鍵を読めない証明書は<b>拒否する</b>（決定 4 の拒否側宣言——LAN 閲覧が止まる側）。</summary>
    [Fact]
    public async Task ConfigureAsync_PrivateKeyUnreadable_Throws()
    {
        var service = CreateService(isPrivateKeyReadable: _ => false);

        var ex = await Assert.ThrowsAsync<WizardValidationException>(
            () => service.ConfigureAsync(enabled: true, certificateThumbprint: ValidThumbprint));

        Assert.Contains("certlm.msc", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>serverAuth EKU 不適合は拒否する（ADR-0012 流用のまま）。</summary>
    [Fact]
    public async Task ConfigureAsync_MissingServerAuthEku_Throws()
    {
        var service = CreateService(hasServerAuthEku: _ => false);

        await Assert.ThrowsAsync<WizardValidationException>(
            () => service.ConfigureAsync(enabled: true, certificateThumbprint: ValidThumbprint));
    }

    /// <summary>有効化するのに拇印が無い構成は拒否する（縮小継続を無言で保存させない）。</summary>
    [Fact]
    public async Task ConfigureAsync_EnabledWithoutThumbprint_Throws()
    {
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<WizardValidationException>(
            () => service.ConfigureAsync(enabled: true, certificateThumbprint: null));

        Assert.Contains("証明書を選択", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>証明書がストアで解決できない場合は拒否し、起動時（1035 経路）と同じ失敗理由を表示する。</summary>
    [Fact]
    public async Task ConfigureAsync_CertificateNotFound_ThrowsWithLoadFailureReason()
    {
        var service = CreateService(
            loadCertificate: _ => CertificateLoadResult.Failure("見つかりませんでした（テスト理由）。"));

        var ex = await Assert.ThrowsAsync<WizardValidationException>(
            () => service.ConfigureAsync(enabled: true, certificateThumbprint: ValidThumbprint));

        Assert.Contains("見つかりませんでした（テスト理由）。", ex.Message, StringComparison.Ordinal);
        Assert.Contains("縮小継続", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// SAN にサーバ名が無い証明書は<b>警告して通す</b>（決定 4——助言に留める。拒否側へ倒される
    /// 事故を防ぐのが本テストの核心）。検査結果には不足名と SAN の実値が入る。
    /// </summary>
    [Fact]
    public async Task ConfigureAsync_SanMismatch_SavesAndReturnsInspection()
    {
        var service = CreateService(
            loadCertificate: _ => CertificateLoadResult.Success(
                CreateCertificate("CN=other", sanDnsNames: ["other.example.com"]), isExpired: false));

        var result = await service.ConfigureAsync(enabled: true, certificateThumbprint: ValidThumbprint);

        Assert.NotNull(result.SanInspection);
        Assert.False(result.SanInspection!.IsSatisfied);
        Assert.Equal(ServerNames, result.SanInspection.MissingServerNames);
        Assert.Contains("other.example.com", result.SanInspection.CertificateDnsNames);

        // 保存は成立している（警告は保存を妨げない）。
        var status = await service.GetStatusAsync();
        Assert.True(status.Enabled);
        Assert.Equal(ValidThumbprint, status.CertificateThumbprint);
    }

    /// <summary>SAN が短名 + FQDN を含む証明書は整合（IsSatisfied）として通る。</summary>
    [Fact]
    public async Task ConfigureAsync_SanCoversServerNames_InspectionSatisfied()
    {
        var service = CreateService(
            loadCertificate: _ => CertificateLoadResult.Success(
                CreateCertificate("CN=yagura01", sanDnsNames: ["yagura01", "yagura01.example.local"]), isExpired: false));

        var result = await service.ConfigureAsync(enabled: true, certificateThumbprint: ValidThumbprint);

        Assert.NotNull(result.SanInspection);
        Assert.True(result.SanInspection!.IsSatisfied);
    }

    // ------------------------------------------------------------------
    // 差分計算・監査（決定 10）・コピー動線（決定 9）
    // ------------------------------------------------------------------

    /// <summary>変更ありの保存は監査 2029（ViewerHttpsConfigured）を操作者つきで記録する。</summary>
    [Fact]
    public async Task ConfigureAsync_Changed_RecordsAudit()
    {
        var service = CreateService();

        var result = await service.ConfigureAsync(
            enabled: true,
            certificateThumbprint: ValidThumbprint,
            operatorAddress: "127.0.0.1",
            operatorScheme: "Negotiate",
            operatorPrincipal: "EXAMPLE\\admin");

        Assert.Equal(
            new[] { "Viewer:Https:Enabled", "Viewer:Https:CertificateThumbprint" },
            result.ChangedKeys);
        Assert.Equal(ConfigurationApplyEffect.RestartRequired, result.RequiredEffect);

        var audit = Assert.Single(_audit.Recorded);
        Assert.Equal(AuditEventKind.ViewerHttpsConfigured, audit.Kind);
        Assert.Contains(ValidThumbprint, audit.Detail, StringComparison.Ordinal);
        Assert.Equal("Negotiate", audit.AuthenticationScheme);
        Assert.Equal("EXAMPLE\\admin", audit.AuthenticatedPrincipal);
    }

    /// <summary>同値保存は no-op（保存も監査もしない——監査証跡を希釈しない）。</summary>
    [Fact]
    public async Task ConfigureAsync_NoChanges_IsNoOp()
    {
        var service = CreateService();
        await service.ConfigureAsync(enabled: true, certificateThumbprint: ValidThumbprint);
        _audit.Recorded.Clear();

        var result = await service.ConfigureAsync(enabled: true, certificateThumbprint: ValidThumbprint);

        Assert.Empty(result.ChangedKeys);
        Assert.Equal(ConfigurationApplyEffect.Immediate, result.RequiredEffect);
        Assert.Empty(_audit.Recorded);
    }

    /// <summary>
    /// 拇印は MMC 表示形式（コロン区切り・小文字）を正規化して受理する。決定 9 のコピー動線で
    /// 転写された拇印も本経路を通る——検証（この場合は期限切れ拒否）のバイパスにならないことを、
    /// 転写を模した「管理 HTTPS 用に選ばれていた拇印」で固定する。
    /// </summary>
    [Fact]
    public async Task ConfigureAsync_CopiedThumbprint_StillValidated()
    {
        var copiedFromAdmin = "ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01";
        var service = CreateService(
            loadCertificate: _ => CertificateLoadResult.Success(CreateCertificate("CN=admin-cert"), isExpired: true));

        await Assert.ThrowsAsync<WizardValidationException>(
            () => service.ConfigureAsync(enabled: false, certificateThumbprint: copiedFromAdmin));
    }

    /// <summary>不正形式の拇印は保存前に親切に拒否する（起動時の縮小側継続とは非対称——利用者が目の前にいる）。</summary>
    [Fact]
    public async Task ConfigureAsync_MalformedThumbprint_Throws()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<WizardValidationException>(
            () => service.ConfigureAsync(enabled: false, certificateThumbprint: "not-a-thumbprint"));
    }

    /// <summary>
    /// GetStatus は決定 9・6 の画面情報（管理 HTTPS 拇印・メール通知の有効状態）も返す。
    /// </summary>
    [Fact]
    public async Task GetStatusAsync_ExposesAdminThumbprintAndEmailState()
    {
        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        var options = snapshot.Options;
        options.Admin ??= new YaguraConfigurationOptions.AdminOptions();
        options.Admin.Https = new YaguraConfigurationOptions.AdminOptions.HttpsOptions
        {
            CertificateThumbprint = "ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01",
        };
        options.Notification = new YaguraConfigurationOptions.NotificationOptions
        {
            Email = new YaguraConfigurationOptions.NotificationOptions.EmailOptions { Enabled = "true" },
        };
        YaguraConfigurationWriter.Save(_dataRoot, options, snapshot.VersionToken);

        var status = await CreateService().GetStatusAsync();

        Assert.Equal("ABCDEF0123456789ABCDEF0123456789ABCDEF01", status.AdminHttpsCertificateThumbprint);
        Assert.True(status.EmailNotificationEnabled);
        Assert.False(status.Enabled);
    }

    /// <summary>InspectCertificate は保存せず検査結果のみ返す（決定 4——保存後の再確認用）。解決不能なら null。</summary>
    [Fact]
    public async Task InspectCertificateAsync_ReturnsInspectionWithoutSaving()
    {
        var service = CreateService(
            loadCertificate: _ => CertificateLoadResult.Success(
                CreateCertificate("CN=x", sanDnsNames: ["yagura01"]), isExpired: false));

        var inspection = await service.InspectCertificateAsync(ValidThumbprint);

        Assert.NotNull(inspection);
        Assert.Contains("yagura01.example.local", inspection!.MissingServerNames);
        Assert.Empty(_audit.Recorded);

        var unresolvable = await CreateService(
            loadCertificate: _ => CertificateLoadResult.Failure("なし")).InspectCertificateAsync(ValidThumbprint);
        Assert.Null(unresolvable);
    }

    // ------------------------------------------------------------------
    // ヘルパ
    // ------------------------------------------------------------------

    private ViewerHttpsAdminService CreateService(
        Func<string, CertificateLoadResult>? loadCertificate = null,
        Func<X509Certificate2, bool>? hasServerAuthEku = null,
        Func<X509Certificate2, bool>? isPrivateKeyReadable = null)
    {
        return new ViewerHttpsAdminService(
            _dataRoot,
            _audit,
            loadCertificate ?? (_ => CertificateLoadResult.Success(
                CreateCertificate("CN=yagura-viewer-test", sanDnsNames: ServerNames), isExpired: false)),
            hasServerAuthEku ?? (_ => true),
            isPrivateKeyReadable ?? (_ => true),
            () => ServerNames);
    }

    private static X509Certificate2 CreateCertificate(string subjectName, string[]? sanDnsNames = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var oids = new OidCollection { new Oid(ServerAuthOid) };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, critical: false));

        if (sanDnsNames is { Length: > 0 })
        {
            var sanBuilder = new SubjectAlternativeNameBuilder();
            foreach (var name in sanDnsNames)
            {
                sanBuilder.AddDnsName(name);
            }

            request.CertificateExtensions.Add(sanBuilder.Build());
        }

        return request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
    }

    private sealed class RecordingAuditRecorder : IAuditRecorder
    {
        public List<AuditEvent> Recorded { get; } = [];

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Recorded.Add(auditEvent);
            return Task.CompletedTask;
        }
    }
}
