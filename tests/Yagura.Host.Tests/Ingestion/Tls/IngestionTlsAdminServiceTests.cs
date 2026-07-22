using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Administration.Https;
using Yagura.Host.Configuration;
using Yagura.Host.Ingestion.Tls;

namespace Yagura.Host.Tests.Ingestion.Tls;

/// <summary>
/// <see cref="IngestionTlsAdminService"/> の保存前 fail-closed 検証・差分計算・監査記録の回帰テスト
/// （ADR-0019 決定 2・5。Issue #349）。
/// </summary>
/// <remarks>
/// 実ストアには触れず、証明書の解決・EKU 判定・秘密鍵の読取検証を注入して分岐を決定的に検証する
/// （ADR-0012 決定 5「実ストア接触は統合／E2E に限定する」を TLS 受信側でも踏襲）。
/// <para>
/// <b>本テストの主眼は「管理リモート HTTPS と割れる 2 点」を固定すること</b>——期限切れは
/// 拒否せず警告して通し、秘密鍵の読取不可は警告ではなく拒否する。どちらも security.md が確定した
/// 非対称であり、実装で逆に倒れると「UI では保存できないのに手編集なら動く」あるいは
/// 「保存できたのに受信が立たない」という乖離になる。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class IngestionTlsAdminServiceTests : IDisposable
{
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private const string ValidThumbprint = "A1B2C3D4E5F60718293A4B5C6D7E8F9012345678";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-tls-admin-{Guid.NewGuid():N}");
    private readonly RecordingAuditRecorder _audit = new();

    public IngestionTlsAdminServiceTests() => Directory.CreateDirectory(_dataRoot);

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // 管理 HTTPS と割れる 2 点（本 ADR の核心）
    // ------------------------------------------------------------------

    /// <summary>
    /// 期限切れ証明書は<b>拒否せず</b>警告フラグ付きで保存が通る（ADR-0019 決定 2）。
    /// 管理リモート HTTPS 側は同じ条件で <see cref="WizardValidationException"/> を投げる。
    /// </summary>
    [Fact]
    public async Task ConfigureAsync_ExpiredCertificate_SavesWithWarning()
    {
        var service = CreateService(
            loadCertificate: _ => CertificateLoadResult.Success(CreateCertificate("CN=yagura-tls-expired"), isExpired: true));

        var result = await service.ConfigureAsync(enabled: true, certificateThumbprint: ValidThumbprint, port: null);

        Assert.True(result.ExpiredWarning);
        Assert.Contains("Ingestion:Tls:Enabled", result.ChangedKeys);
        Assert.Equal(ConfigurationApplyEffect.RestartRequired, result.RequiredEffect);

        // 実際に永続化されている（警告は保存を妨げない）
        var status = await service.GetStatusAsync();
        Assert.True(status.Enabled);
        Assert.Equal(ValidThumbprint, status.CertificateThumbprint);
    }

    /// <summary>
    /// 秘密鍵を読めない証明書は<b>拒否する</b>（ADR-0019 決定 2）。
    /// 管理リモート HTTPS 側は同じ条件で警告フラグを立てて保存を通す。
    /// </summary>
    [Fact]
    public async Task ConfigureAsync_PrivateKeyUnreadable_Throws()
    {
        var service = CreateService(isPrivateKeyReadable: _ => false);

        var ex = await Assert.ThrowsAsync<WizardValidationException>(
            () => service.ConfigureAsync(enabled: true, certificateThumbprint: ValidThumbprint, port: null));

        Assert.Contains("秘密鍵", ex.Message);
        Assert.Contains("certlm.msc", ex.Message);
        Assert.False(File.Exists(ConfigurationFilePath));
        Assert.Empty(_audit.Recorded);
    }

    // ------------------------------------------------------------------
    // 拒否（ADR-0019 決定 2 の拒否 4 項目）
    // ------------------------------------------------------------------

    /// <summary>有効化するのに拇印が未設定なら拒否する。</summary>
    [Fact]
    public async Task ConfigureAsync_EnabledWithoutThumbprint_Throws()
    {
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<WizardValidationException>(
            () => service.ConfigureAsync(enabled: true, certificateThumbprint: null, port: null));

        Assert.Contains("証明書を選択", ex.Message);
        Assert.Empty(_audit.Recorded);
    }

    /// <summary>
    /// 証明書が解決できない場合は、起動時（1016 経路）と同一の失敗理由をそのまま提示して拒否する
    /// （決定 2「UI 事前検証と起動時検証の乖離ゼロ」）。
    /// </summary>
    [Fact]
    public async Task ConfigureAsync_CertificateNotFound_ThrowsWithStartupFailureReason()
    {
        const string Reason = "指定された拇印の証明書が LocalMachine\\My に見つかりません。";
        var service = CreateService(loadCertificate: _ => CertificateLoadResult.Failure(Reason));

        var ex = await Assert.ThrowsAsync<WizardValidationException>(
            () => service.ConfigureAsync(enabled: true, certificateThumbprint: ValidThumbprint, port: null));

        Assert.Contains(Reason, ex.Message);
        // 平文受信への影響がないことを案内に含める（TLS 受信は opt-in の付加チャネル）
        Assert.Contains("平文", ex.Message);
    }

    /// <summary>serverAuth EKU 不適合は拒否する（管理側と同一）。</summary>
    [Fact]
    public async Task ConfigureAsync_ServerAuthEkuMismatch_Throws()
    {
        var service = CreateService(hasServerAuthEku: _ => false);

        var ex = await Assert.ThrowsAsync<WizardValidationException>(
            () => service.ConfigureAsync(enabled: true, certificateThumbprint: ValidThumbprint, port: null));

        Assert.Contains("serverAuth", ex.Message);
        Assert.Empty(_audit.Recorded);
    }

    /// <summary>拇印・ポートの形式不正は保存前に拒否する。</summary>
    [Theory]
    [InlineData("not-a-thumbprint", null)]
    [InlineData(ValidThumbprint, "0")]
    [InlineData(ValidThumbprint, "65536")]
    [InlineData(ValidThumbprint, "abc")]
    public async Task ConfigureAsync_MalformedInput_Throws(string? thumbprint, string? port)
    {
        var service = CreateService();

        await Assert.ThrowsAsync<WizardValidationException>(
            () => service.ConfigureAsync(enabled: false, certificateThumbprint: thumbprint, port: port));

        Assert.Empty(_audit.Recorded);
    }

    // ------------------------------------------------------------------
    // 差分計算・監査
    // ------------------------------------------------------------------

    /// <summary>変更ゼロの保存は no-op（保存も監査もしない）。表記ゆれを変更と数えない。</summary>
    [Fact]
    public async Task ConfigureAsync_NoChange_IsNoOpEvenWithDifferentLiteralCasing()
    {
        Seed(enabled: "True", thumbprint: ValidThumbprint.ToLowerInvariant(), port: "6514");
        var service = CreateService();

        var result = await service.ConfigureAsync(enabled: true, certificateThumbprint: ValidThumbprint, port: "6514");

        Assert.Empty(result.ChangedKeys);
        Assert.Equal(ConfigurationApplyEffect.Immediate, result.RequiredEffect);
        Assert.Empty(_audit.Recorded);
    }

    /// <summary>
    /// 変更ありなら監査 2020 を 1 件記録する（ADR-0019 決定 5）。Detail に 3 キーと変更キー一覧、
    /// 操作者が載る。拇印は公開識別子なので値を残す。
    /// </summary>
    [Fact]
    public async Task ConfigureAsync_WithChanges_RecordsAudit2020WithOperator()
    {
        var service = CreateService();

        await service.ConfigureAsync(
            enabled: true,
            certificateThumbprint: ValidThumbprint,
            port: "7514",
            operatorAddress: "127.0.0.1",
            operatorScheme: "windows",
            operatorPrincipal: "YAGURA\\admin");

        var recorded = Assert.Single(_audit.Recorded);
        Assert.Equal(AuditEventKind.IngestionTlsCertificateConfigured, recorded.Kind);
        Assert.Equal("127.0.0.1", recorded.RemoteAddress);
        Assert.Equal("windows", recorded.AuthenticationScheme);
        Assert.Equal("YAGURA\\admin", recorded.AuthenticatedPrincipal);
        Assert.Contains(ValidThumbprint, recorded.Detail);
        Assert.Contains("Ingestion:Tls:Port=7514", recorded.Detail);
        Assert.Contains("changedKeys=", recorded.Detail);
    }

    /// <summary>ポート未指定は「既定 6514」として記録され、差分計算でも未設定と同一に扱われる。</summary>
    [Fact]
    public async Task ConfigureAsync_PortOmitted_RecordsDefaultPortMarker()
    {
        var service = CreateService();

        await service.ConfigureAsync(enabled: true, certificateThumbprint: ValidThumbprint, port: null);

        var recorded = Assert.Single(_audit.Recorded);
        Assert.Contains("(既定 6514)", recorded.Detail);
    }

    /// <summary>
    /// 対象外キー（<c>Ingestion:Tls:BindAddress</c>）は保存で消えない（ADR-0019 決定 1 で意図的に対象外）。
    /// </summary>
    [Fact]
    public async Task ConfigureAsync_DoesNotClearBindAddress()
    {
        Seed(bindAddress: "0.0.0.0");
        var service = CreateService();

        await service.ConfigureAsync(enabled: true, certificateThumbprint: ValidThumbprint, port: null);

        var after = YaguraConfigurationWriter.Read(_dataRoot).Options;
        Assert.Equal("0.0.0.0", after.Ingestion?.Tls?.BindAddress);
    }

    /// <summary>
    /// 拇印が指定されていれば <c>Enabled=false</c> でも証明書検証を行う——「証明書だけ先に
    /// 設定しておく」段階的な準備で、使えない証明書を無警告で保存させないため。
    /// </summary>
    [Fact]
    public async Task ConfigureAsync_ValidatesCertificateEvenWhenDisabled()
    {
        var service = CreateService(hasServerAuthEku: _ => false);

        await Assert.ThrowsAsync<WizardValidationException>(
            () => service.ConfigureAsync(enabled: false, certificateThumbprint: ValidThumbprint, port: null));
    }

    /// <summary>拇印を指定しない無効化は証明書検証を要さず通る（TLS 受信をやめる操作）。</summary>
    [Fact]
    public async Task ConfigureAsync_DisableWithoutThumbprint_Succeeds()
    {
        Seed(enabled: "true", thumbprint: ValidThumbprint);
        var service = CreateService(loadCertificate: _ => throw new InvalidOperationException("証明書解決は呼ばれないはず。"));

        var result = await service.ConfigureAsync(enabled: false, certificateThumbprint: null, port: null);

        Assert.Contains("Ingestion:Tls:Enabled", result.ChangedKeys);
        Assert.False(result.ExpiredWarning);
    }

    private string ConfigurationFilePath => Path.Combine(_dataRoot, YaguraConfigurationLoader.ConfigurationFileName);

    private IngestionTlsAdminService CreateService(
        Func<string, CertificateLoadResult>? loadCertificate = null,
        Func<X509Certificate2, bool>? hasServerAuthEku = null,
        Func<X509Certificate2, bool>? isPrivateKeyReadable = null)
    {
        return new IngestionTlsAdminService(
            _dataRoot,
            _audit,
            loadCertificate ?? (_ => CertificateLoadResult.Success(CreateCertificate("CN=yagura-tls-test"), isExpired: false)),
            hasServerAuthEku ?? (_ => true),
            isPrivateKeyReadable ?? (_ => true));
    }

    private void Seed(
        string? enabled = null,
        string? thumbprint = null,
        string? port = null,
        string? bindAddress = null)
    {
        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        var options = snapshot.Options;
        options.Ingestion ??= new YaguraConfigurationOptions.IngestionOptions();
        options.Ingestion.Tls ??= new YaguraConfigurationOptions.IngestionOptions.TlsOptions();

        if (enabled is not null)
        {
            options.Ingestion.Tls.Enabled = enabled;
        }

        if (thumbprint is not null)
        {
            options.Ingestion.Tls.CertificateThumbprint = thumbprint;
        }

        if (port is not null)
        {
            options.Ingestion.Tls.Port = port;
        }

        if (bindAddress is not null)
        {
            options.Ingestion.Tls.BindAddress = bindAddress;
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
