using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Yagura.Host.Administration.Https;

namespace Yagura.Host.Tests.Administration.Https;

/// <summary>
/// <see cref="StoreAdminCertificateStoreReader"/> の純粋ロジック（serverAuth EKU 判定・DTO 写像）の
/// 単体テスト（ADR-0012 決定 2・5）。実ストア（<c>LocalMachine\My</c>）への接触はプラットフォーム／
/// 権限依存のため、ここではメモリ内生成証明書（<see cref="CertificateRequest"/>）で決定的に検証し、
/// 実ストア列挙の疎通は統合／E2E に委ねる（ADR-0012 決定 5「実ストア接触は統合／E2E に限定」）。
/// </summary>
/// <remarks>
/// EKU フィルタ（<see cref="StoreAdminCertificateStoreReader.HasServerAuthEku"/>）は既存コードに
/// 前例がなく本増分で新規に書いたロジックであり、用途違いの証明書を列挙に混ぜない受け入れ基準の
/// 中核のため、重点的に固定する。
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class StoreAdminCertificateStoreReaderTests
{
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    [Fact]
    public void HasServerAuthEku_WithServerAuthEku_ReturnsTrue()
    {
        using var cert = CreateCertificate("CN=yagura-serverauth", ekuOids: [ServerAuthOid]);

        Assert.True(StoreAdminCertificateStoreReader.HasServerAuthEku(cert));
    }

    [Fact]
    public void HasServerAuthEku_WithServerAuthAmongMultipleEkus_ReturnsTrue()
    {
        using var cert = CreateCertificate("CN=yagura-multi", ekuOids: [ClientAuthOid, ServerAuthOid]);

        Assert.True(StoreAdminCertificateStoreReader.HasServerAuthEku(cert));
    }

    [Fact]
    public void HasServerAuthEku_WithClientAuthOnly_ReturnsFalse()
    {
        // 用途違い（クライアント認証専用）は列挙に混ぜない（ワンクリック誤選択の抑止）。
        using var cert = CreateCertificate("CN=yagura-clientauth", ekuOids: [ClientAuthOid]);

        Assert.False(StoreAdminCertificateStoreReader.HasServerAuthEku(cert));
    }

    [Fact]
    public void HasServerAuthEku_WithNoEkuExtension_ReturnsFalse()
    {
        // EKU 拡張なしは RFC 5280 上「全用途有効」だが、ADR-0012 決定 2 は serverAuth を明示する
        // 証明書へ最小化するため対象外（用途が確認できないものを勧めない）。
        using var cert = CreateCertificate("CN=yagura-noeku", ekuOids: null);

        Assert.False(StoreAdminCertificateStoreReader.HasServerAuthEku(cert));
    }

    [Fact]
    public void ToCandidate_MapsMinimalFields_AndReadableInMemoryKey()
    {
        using var cert = CreateCertificate("CN=yagura.example.test", ekuOids: [ServerAuthOid]);
        var now = DateTimeOffset.Now;

        var candidate = StoreAdminCertificateStoreReader.ToCandidate(cert, now);

        Assert.Equal(cert.Thumbprint, candidate.Thumbprint);
        Assert.Equal("yagura.example.test", candidate.SubjectCommonName);
        Assert.Equal(cert.Issuer, candidate.Issuer);
        Assert.False(candidate.IsExpired);
        // メモリ内生成鍵は現在のプロセスから読めるため読取検証は真になる。
        Assert.True(candidate.IsPrivateKeyReadable);
    }

    [Fact]
    public void ToCandidate_ExpiredCertificate_FlagsIsExpired()
    {
        using var cert = CreateCertificate(
            "CN=yagura-expired",
            ekuOids: [ServerAuthOid],
            notBefore: DateTimeOffset.Now.AddDays(-30),
            notAfter: DateTimeOffset.Now.AddDays(-1));
        var now = DateTimeOffset.Now;

        var candidate = StoreAdminCertificateStoreReader.ToCandidate(cert, now);

        // 期限切れは除外せず警告フラグで返す（「なぜ使えないか」を UI で説明する——受け入れ基準）。
        Assert.True(candidate.IsExpired);
    }

    [Fact]
    public void ToCandidate_NotYetValidCertificate_FlagsIsExpired()
    {
        using var cert = CreateCertificate(
            "CN=yagura-future",
            ekuOids: [ServerAuthOid],
            notBefore: DateTimeOffset.Now.AddDays(1),
            notAfter: DateTimeOffset.Now.AddDays(30));
        var now = DateTimeOffset.Now;

        var candidate = StoreAdminCertificateStoreReader.ToCandidate(cert, now);

        // 有効期間の前（未来証明書）も期間外として扱う。
        Assert.True(candidate.IsExpired);
    }

    private static X509Certificate2 CreateCertificate(
        string subjectName,
        string[]? ekuOids,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        if (ekuOids is not null)
        {
            var oids = new OidCollection();
            foreach (var oid in ekuOids)
            {
                oids.Add(new Oid(oid));
            }

            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, critical: false));
        }

        var from = notBefore ?? DateTimeOffset.Now.AddDays(-1);
        var to = notAfter ?? DateTimeOffset.Now.AddYears(1);
        return request.CreateSelfSigned(from, to);
    }
}
