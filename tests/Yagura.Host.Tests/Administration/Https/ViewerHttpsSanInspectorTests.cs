using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Yagura.Host.Administration.Https;

namespace Yagura.Host.Tests.Administration.Https;

/// <summary>
/// <see cref="ViewerHttpsSanInspector"/>（ADR-0022 決定 4——SAN ホスト名整合の助言検査）の
/// 単体テスト。dNSName 照合規則（大文字小文字非区別・ワイルドカードは単一ラベル展開のみ）と
/// SAN 拡張なしの扱いを固定する。
/// </summary>
public sealed class ViewerHttpsSanInspectorTests
{
    // ------------------------------------------------------------------
    // MatchesDnsName（照合規則の核）
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("yagura01.example.com", "yagura01.example.com", true)]
    [InlineData("YAGURA01.EXAMPLE.COM", "yagura01.example.com", true)] // 大文字小文字を区別しない
    [InlineData("yagura01", "YAGURA01", true)]                          // 短名も同様
    [InlineData("yagura01.example.com", "yagura01", false)]             // FQDN の SAN は短名アクセスをカバーしない（典型事故）
    [InlineData("*.example.com", "yagura01.example.com", true)]         // ワイルドカード: 単一ラベル展開
    [InlineData("*.example.com", "a.b.example.com", false)]             // 複数ラベルは不一致（RFC 6125 の一般実装）
    [InlineData("*.example.com", "example.com", false)]                 // 裸のドメインは不一致
    [InlineData("*.example.com", "yagura01", false)]                    // ワイルドカードは短名をカバーしない
    [InlineData("", "yagura01", false)]
    public void MatchesDnsName_FollowsBrowserConventions(string dnsName, string hostName, bool expected)
    {
        Assert.Equal(expected, ViewerHttpsSanInspector.MatchesDnsName(dnsName, hostName));
    }

    // ------------------------------------------------------------------
    // Inspect（証明書からの読み出しを含む統合）
    // ------------------------------------------------------------------

    [Fact]
    public void Inspect_SanCoversAllNames_IsSatisfied()
    {
        using var certificate = CreateCertificate(sanDnsNames: ["yagura01", "yagura01.example.local"]);

        var inspection = ViewerHttpsSanInspector.Inspect(
            certificate, ["YAGURA01", "yagura01.example.local"]);

        Assert.True(inspection.IsSatisfied);
        Assert.True(inspection.HasSanExtension);
        Assert.Equal(2, inspection.CertificateDnsNames.Count);
    }

    [Fact]
    public void Inspect_FqdnOnlySan_ReportsShortNameMissing()
    {
        // 「短名アクセス × FQDN のみの SAN」の典型事故（決定 4 の主目的）を検出できること。
        using var certificate = CreateCertificate(sanDnsNames: ["yagura01.example.local"]);

        var inspection = ViewerHttpsSanInspector.Inspect(
            certificate, ["YAGURA01", "yagura01.example.local"]);

        Assert.False(inspection.IsSatisfied);
        var missing = Assert.Single(inspection.MissingServerNames);
        Assert.Equal("YAGURA01", missing);
    }

    [Fact]
    public void Inspect_WildcardSan_CoversFqdnButNotShortName()
    {
        using var certificate = CreateCertificate(sanDnsNames: ["*.example.local"]);

        var inspection = ViewerHttpsSanInspector.Inspect(
            certificate, ["YAGURA01", "yagura01.example.local"]);

        Assert.False(inspection.IsSatisfied);
        var missing = Assert.Single(inspection.MissingServerNames);
        Assert.Equal("YAGURA01", missing);
    }

    [Fact]
    public void Inspect_NoSanExtension_ReportsAllMissing()
    {
        // SAN 拡張なし: 現代のブラウザは CN を見ないため、全名が不一致になる（画面は差し替えを推奨）。
        using var certificate = CreateCertificate(sanDnsNames: null);

        var inspection = ViewerHttpsSanInspector.Inspect(
            certificate, ["YAGURA01", "yagura01.example.local"]);

        Assert.False(inspection.HasSanExtension);
        Assert.False(inspection.IsSatisfied);
        Assert.Equal(2, inspection.MissingServerNames.Count);
        Assert.Empty(inspection.CertificateDnsNames);
    }

    [Fact]
    public void GetLocalServerNames_ReturnsAtLeastMachineName()
    {
        // 実マシン依存の薄い煙テスト: 短名（Environment.MachineName）は必ず含まれる。
        var names = ViewerHttpsSanInspector.GetLocalServerNames();

        Assert.Contains(Environment.MachineName, names);
    }

    private static X509Certificate2 CreateCertificate(string[]? sanDnsNames)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=san-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

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
}
