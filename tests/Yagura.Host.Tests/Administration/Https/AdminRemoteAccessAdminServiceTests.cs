using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Administration.Https;
using Yagura.Host.Configuration;

namespace Yagura.Host.Tests.Administration.Https;

/// <summary>
/// <see cref="AdminRemoteAccessAdminService"/> の単体テスト（ADR-0012 決定 4・7。B2 増分）。
/// </summary>
/// <remarks>
/// 証明書検証は注入した偽 Func（<see cref="CertificateRequest"/> によるメモリ内生成証明書）で
/// 決定的に検証し、実ストア（<c>LocalMachine\My</c>）には接触しない（ADR-0012 決定 5。
/// <see cref="WindowsCertificateStoreReaderTests"/> と同じ方針）。fail-closed 検証・差分計算・
/// 監査の分岐（2011/2012）・楽観競合という保存経路の意味論を固定する。
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AdminRemoteAccessAdminServiceTests : IDisposable
{
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private const string ValidThumbprint = "0123456789ABCDEF0123456789ABCDEF01234567";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-remoteaccess-test-{Guid.NewGuid():N}");
    private readonly RecordingAuditRecorder _audit = new();

    public AdminRemoteAccessAdminServiceTests()
    {
        Directory.CreateDirectory(_dataRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            try
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
            catch (IOException)
            {
                // ベストエフォート（他テストと同じ判断）。
            }
        }
    }

    [Fact]
    public async Task GetStatusAsync_NoConfigurationYet_ReturnsAllDisabled()
    {
        var service = CreateService();

        var status = await service.GetStatusAsync();

        Assert.False(status.RemoteBindingEnabled);
        Assert.False(status.HttpsEnabled);
        Assert.Null(status.CertificateThumbprint);
        Assert.Null(status.HttpsPort);
        Assert.False(status.WindowsAuthEnabled);
        Assert.False(status.AppAuthEnabled);
    }

    [Fact]
    public async Task GetStatusAsync_ReturnsNormalizedThumbprintAndAuthFlagsFromPersistedValues()
    {
        // 永続値は区切り文字入り小文字でも、返却は正規化済み（列挙 DTO の Thumbprint と直接照合可能）。
        Seed(remoteBinding: "true", httpsEnabled: "true",
            thumbprint: "01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67",
            port: "18516", windowsAuth: "true");
        var service = CreateService();

        var status = await service.GetStatusAsync();

        Assert.True(status.RemoteBindingEnabled);
        Assert.True(status.HttpsEnabled);
        Assert.Equal(ValidThumbprint, status.CertificateThumbprint);
        Assert.Equal("18516", status.HttpsPort);
        Assert.True(status.WindowsAuthEnabled);
        Assert.False(status.AppAuthEnabled);
    }

    [Fact]
    public async Task ConfigureAsync_EnableRemoteBindingWithoutPersistedAuth_ThrowsWithGuidance()
    {
        // fail-closed（ADR-0012 決定 4）: 認証の有効状態は永続値で判定する。認証未構成のまま
        // リモートバインドを有効化する保存は、起動時 1012 と対称の文言（誘導つき）で拒否する。
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<WizardValidationException>(() =>
            service.ConfigureAsync(
                remoteBindingEnabled: true,
                httpsEnabled: true,
                certificateThumbprint: ValidThumbprint,
                httpsPort: "18516"));

        Assert.Contains("認証方式", exception.Message);
        Assert.Contains("認証設定", exception.Message);
        Assert.Contains("Admin:RemoteBinding:Enabled", exception.Message);
        Assert.Empty(_audit.Recorded);
    }

    [Fact]
    public async Task ConfigureAsync_EnableRemoteBindingWithoutHttpsOrThumbprint_Throws()
    {
        Seed(windowsAuth: "true");
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<WizardValidationException>(() =>
            service.ConfigureAsync(
                remoteBindingEnabled: true,
                httpsEnabled: false,
                certificateThumbprint: null,
                httpsPort: null));

        Assert.Contains("Admin:Https:Enabled", exception.Message);
        Assert.Contains("Admin:Https:CertificateThumbprint", exception.Message);
    }

    [Fact]
    public async Task ConfigureAsync_EnableRemoteBindingWithAuthAndCertificate_SavesAllKeysAndAudits2011And2012()
    {
        Seed(windowsAuth: "true");
        var service = CreateService();

        var result = await service.ConfigureAsync(
            remoteBindingEnabled: true,
            httpsEnabled: true,
            certificateThumbprint: ValidThumbprint,
            httpsPort: "18516",
            operatorAddress: "127.0.0.1",
            operatorScheme: "windows",
            operatorPrincipal: "YAGURA\\jdoe");

        Assert.Equal(
            new[]
            {
                "Admin:RemoteBinding:Enabled",
                "Admin:Https:Enabled",
                "Admin:Https:CertificateThumbprint",
                "Admin:Https:Port",
            },
            result.ChangedKeys);
        Assert.Equal(ConfigurationApplyEffect.RestartRequired, result.RequiredEffect);
        Assert.False(result.PrivateKeyUnreadableWarning);
        Assert.True(result.Status.RemoteBindingEnabled);

        // yagura.json に 4 キーが永続化されていること。
        var saved = YaguraConfigurationWriter.Read(_dataRoot).Options;
        Assert.Equal("True", saved.Admin?.RemoteBinding?.Enabled);
        Assert.Equal("True", saved.Admin?.Https?.Enabled);
        Assert.Equal(ValidThumbprint, saved.Admin?.Https?.CertificateThumbprint);
        Assert.Equal("18516", saved.Admin?.Https?.Port);

        // 監査（ADR-0012 決定 7）: RemoteBinding の切替 = 2011、Https 系の変更 = 2012 の 2 件。
        // 操作者（2006 と同じ AuthenticationScheme/AuthenticatedPrincipal）が両方へ伝播する。
        Assert.Equal(2, _audit.Recorded.Count);

        var remoteBinding = Assert.Single(_audit.Recorded, e => e.Kind == AuditEventKind.AdminRemoteBindingConfigured);
        Assert.Contains("Admin:RemoteBinding:Enabled=True", remoteBinding.Detail);
        Assert.Equal("windows", remoteBinding.AuthenticationScheme);
        Assert.Equal("YAGURA\\jdoe", remoteBinding.AuthenticatedPrincipal);
        Assert.Equal("127.0.0.1", remoteBinding.RemoteAddress);

        var https = Assert.Single(_audit.Recorded, e => e.Kind == AuditEventKind.AdminHttpsCertificateConfigured);
        Assert.Contains(ValidThumbprint, https.Detail);
        Assert.Contains("Admin:Https:Port=18516", https.Detail);
        Assert.Equal("windows", https.AuthenticationScheme);
        Assert.Equal("YAGURA\\jdoe", https.AuthenticatedPrincipal);
    }

    [Fact]
    public async Task ConfigureAsync_CertificateLoadFailure_ThrowsWithLoadFailureReason()
    {
        // 起動時 1013 と同一コード（AdminCertificateProvider.Load 相当）の失敗理由をそのまま表示する（D-6）。
        var service = CreateService(loadCertificate: _ =>
            AdminCertificateLoadResult.Failure($"拇印 {ValidThumbprint} の証明書が LocalMachine\\My ストアに見つかりません。"));

        var exception = await Assert.ThrowsAsync<WizardValidationException>(() =>
            service.ConfigureAsync(
                remoteBindingEnabled: false,
                httpsEnabled: true,
                certificateThumbprint: ValidThumbprint,
                httpsPort: null));

        Assert.Contains("見つかりません", exception.Message);
        Assert.Empty(_audit.Recorded);
    }

    [Fact]
    public async Task ConfigureAsync_ExpiredCertificate_ThrowsMatchingStartupReducedContinuation()
    {
        // 期限切れの扱い = 拒否（D-6 乖離ゼロ）: 起動時評価（Program.cs）は Load 成功でも
        // IsExpired なら「未解決」として bind をスキップし縮小継続（1013）するため、事前検証が
        // 緑のまま通すと「保存は成功したのに再起動後に 1013」の乖離になる。
        var service = CreateService(loadCertificate: _ =>
            AdminCertificateLoadResult.Success(CreateCertificate("CN=yagura-expired"), isExpired: true));

        var exception = await Assert.ThrowsAsync<WizardValidationException>(() =>
            service.ConfigureAsync(
                remoteBindingEnabled: false,
                httpsEnabled: true,
                certificateThumbprint: ValidThumbprint,
                httpsPort: null));

        Assert.Contains("有効期間外", exception.Message);
        Assert.Contains("縮小継続", exception.Message);
    }

    [Fact]
    public async Task ConfigureAsync_ServerAuthEkuMismatch_Throws()
    {
        // 列挙 UI（WindowsCertificateStoreReader）と同じ最小化: serverAuth EKU 不適合は保存でも拒否。
        var service = CreateService(hasServerAuthEku: _ => false);

        var exception = await Assert.ThrowsAsync<WizardValidationException>(() =>
            service.ConfigureAsync(
                remoteBindingEnabled: false,
                httpsEnabled: true,
                certificateThumbprint: ValidThumbprint,
                httpsPort: null));

        Assert.Contains("serverAuth", exception.Message);
    }

    [Fact]
    public async Task ConfigureAsync_PrivateKeyUnreadable_SavesWithWarningFlag()
    {
        // ADR-0012 決定 3 = (b): 秘密鍵の読取不可は拒否ではなく警告（保存は成功。UI が
        // certlm.msc の付与手順へ誘導する）。起動時も縮小継続でありサービス全体は止まらない。
        var service = CreateService(isPrivateKeyReadable: _ => false);

        var result = await service.ConfigureAsync(
            remoteBindingEnabled: false,
            httpsEnabled: true,
            certificateThumbprint: ValidThumbprint,
            httpsPort: null);

        Assert.True(result.PrivateKeyUnreadableWarning);
        Assert.Contains("Admin:Https:CertificateThumbprint", result.ChangedKeys);
        Assert.Equal(ValidThumbprint, YaguraConfigurationWriter.Read(_dataRoot).Options.Admin?.Https?.CertificateThumbprint);
    }

    [Fact]
    public async Task ConfigureAsync_StagedHttpsPreparationWithoutRemoteBindingOrAuth_Succeeds()
    {
        // 段階的な準備（拇印・HTTPS だけ先に設定。RemoteBinding は off のまま）は認証なしでも許可
        // ——閉じたままの構成変更であり fail-closed 不変条件（1012）の対象外。証明書検証は実施される。
        var service = CreateService();

        var result = await service.ConfigureAsync(
            remoteBindingEnabled: false,
            httpsEnabled: true,
            certificateThumbprint: ValidThumbprint,
            httpsPort: "18516");

        Assert.DoesNotContain("Admin:RemoteBinding:Enabled", result.ChangedKeys);
        Assert.Contains("Admin:Https:Enabled", result.ChangedKeys);

        // RemoteBinding は変わっていないため 2011 は記録されず、2012 のみ。
        var recorded = Assert.Single(_audit.Recorded);
        Assert.Equal(AuditEventKind.AdminHttpsCertificateConfigured, recorded.Kind);
    }

    [Fact]
    public async Task ConfigureAsync_DisableRemoteBinding_SucceedsWithoutAuthAndAudits2011()
    {
        // 無効化（閉じる方向）は認証状態にかかわらず常に許可。
        Seed(remoteBinding: "true", httpsEnabled: "true", thumbprint: ValidThumbprint, port: "18516");
        var service = CreateService();

        var result = await service.ConfigureAsync(
            remoteBindingEnabled: false,
            httpsEnabled: true,
            certificateThumbprint: ValidThumbprint,
            httpsPort: "18516");

        Assert.Equal(new[] { "Admin:RemoteBinding:Enabled" }, result.ChangedKeys);

        var recorded = Assert.Single(_audit.Recorded);
        Assert.Equal(AuditEventKind.AdminRemoteBindingConfigured, recorded.Kind);
        Assert.Contains("Admin:RemoteBinding:Enabled=False", recorded.Detail);

        Assert.Equal("False", YaguraConfigurationWriter.Read(_dataRoot).Options.Admin?.RemoteBinding?.Enabled);
    }

    [Fact]
    public async Task ConfigureAsync_NoChanges_DoesNotSaveNorAudit()
    {
        Seed(remoteBinding: "false", httpsEnabled: "true", thumbprint: ValidThumbprint, port: "18516");
        var savedBefore = File.ReadAllText(ConfigurationFilePath);
        var service = CreateService();

        var result = await service.ConfigureAsync(
            remoteBindingEnabled: false,
            httpsEnabled: true,
            certificateThumbprint: ValidThumbprint,
            httpsPort: "18516");

        Assert.Empty(result.ChangedKeys);
        Assert.Equal(ConfigurationApplyEffect.Immediate, result.RequiredEffect);
        Assert.Empty(_audit.Recorded);
        Assert.Equal(savedBefore, File.ReadAllText(ConfigurationFilePath));
    }

    [Fact]
    public async Task ConfigureAsync_NoChangesWithRepresentationDifferences_IsStillNoOp()
    {
        // 実効値の比較: 生表記のゆれ（小文字 true・区切り文字入り拇印）は変更と数えない。
        Seed(remoteBinding: "false", httpsEnabled: "true",
            thumbprint: "01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67",
            port: "18516");
        var service = CreateService();

        var result = await service.ConfigureAsync(
            remoteBindingEnabled: false,
            httpsEnabled: true,
            certificateThumbprint: ValidThumbprint,
            httpsPort: "18516");

        Assert.Empty(result.ChangedKeys);
        Assert.Empty(_audit.Recorded);
    }

    [Fact]
    public async Task ConfigureAsync_InvalidThumbprintFormat_Throws()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<WizardValidationException>(() =>
            service.ConfigureAsync(
                remoteBindingEnabled: false,
                httpsEnabled: false,
                certificateThumbprint: "not-a-thumbprint",
                httpsPort: null));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("abc")]
    public async Task ConfigureAsync_InvalidPort_Throws(string port)
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<WizardValidationException>(() =>
            service.ConfigureAsync(
                remoteBindingEnabled: false,
                httpsEnabled: false,
                certificateThumbprint: null,
                httpsPort: port));

        Assert.Contains("1〜65535", exception.Message);
    }

    [Fact]
    public async Task ConfigureAsync_ExternalModificationBetweenReadAndSave_ThrowsConfigurationConflictException()
    {
        // 楽観競合（configuration.md §3）: AdminAuthenticationAdminService と同じく
        // ConfigurationConflictException をそのまま伝播する。読み込み（検証用スナップショット）後・
        // 保存前の外部変更を、証明書検証 Func の副作用として決定的に注入する。
        var service = CreateService(loadCertificate: thumbprint =>
        {
            Seed(windowsAuth: "true");
            return AdminCertificateLoadResult.Success(CreateCertificate("CN=yagura-conflict"), isExpired: false);
        });

        await Assert.ThrowsAsync<ConfigurationConflictException>(() =>
            service.ConfigureAsync(
                remoteBindingEnabled: false,
                httpsEnabled: true,
                certificateThumbprint: ValidThumbprint,
                httpsPort: null));

        Assert.Empty(_audit.Recorded);
    }

    private string ConfigurationFilePath => Path.Combine(_dataRoot, YaguraConfigurationLoader.ConfigurationFileName);

    private AdminRemoteAccessAdminService CreateService(
        Func<string, AdminCertificateLoadResult>? loadCertificate = null,
        Func<X509Certificate2, bool>? hasServerAuthEku = null,
        Func<X509Certificate2, bool>? isPrivateKeyReadable = null)
    {
        return new AdminRemoteAccessAdminService(
            _dataRoot,
            _audit,
            loadCertificate ?? (_ => AdminCertificateLoadResult.Success(CreateCertificate("CN=yagura-test"), isExpired: false)),
            hasServerAuthEku ?? (_ => true),
            isPrivateKeyReadable ?? (_ => true));
    }

    private void Seed(
        string? remoteBinding = null,
        string? httpsEnabled = null,
        string? thumbprint = null,
        string? port = null,
        string? windowsAuth = null,
        string? appAuth = null)
    {
        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        var options = snapshot.Options;
        options.Admin ??= new YaguraConfigurationOptions.AdminOptions();

        if (remoteBinding is not null || thumbprint is not null || httpsEnabled is not null || port is not null)
        {
            options.Admin.RemoteBinding ??= new YaguraConfigurationOptions.AdminOptions.RemoteBindingOptions();
            options.Admin.Https ??= new YaguraConfigurationOptions.AdminOptions.HttpsOptions();
            if (remoteBinding is not null)
            {
                options.Admin.RemoteBinding.Enabled = remoteBinding;
            }

            if (httpsEnabled is not null)
            {
                options.Admin.Https.Enabled = httpsEnabled;
            }

            if (thumbprint is not null)
            {
                options.Admin.Https.CertificateThumbprint = thumbprint;
            }

            if (port is not null)
            {
                options.Admin.Https.Port = port;
            }
        }

        if (windowsAuth is not null || appAuth is not null)
        {
            options.Admin.Authentication ??= new YaguraConfigurationOptions.AdminOptions.AuthenticationOptions();
            if (windowsAuth is not null)
            {
                options.Admin.Authentication.Windows ??= new YaguraConfigurationOptions.AdminOptions.AuthenticationOptions.WindowsOptions();
                options.Admin.Authentication.Windows.Enabled = windowsAuth;
            }

            if (appAuth is not null)
            {
                options.Admin.Authentication.App ??= new YaguraConfigurationOptions.AdminOptions.AuthenticationOptions.AppOptions();
                options.Admin.Authentication.App.Enabled = appAuth;
            }
        }

        YaguraConfigurationWriter.Save(_dataRoot, options, snapshot.VersionToken);
    }

    private static X509Certificate2 CreateCertificate(string subjectName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var oids = new OidCollection { new Oid(ServerAuthOid) };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, critical: false));
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
