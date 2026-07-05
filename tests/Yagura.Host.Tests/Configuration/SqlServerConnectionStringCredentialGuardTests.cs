using Yagura.Host.Configuration;

namespace Yagura.Host.Tests.Configuration;

/// <summary>
/// <see cref="SqlServerConnectionStringCredentialGuard"/> のテスト（Issue #47 で平文検出の
/// 枠組みを挿入。DPAPI 暗号化・復号自体のテストは <see cref="DpapiConnectionStringProtectorTests"/>）。
/// </summary>
public sealed class SqlServerConnectionStringCredentialGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ContainsPlaintextCredential_NullOrWhitespace_ReturnsFalse(string? connectionString)
    {
        Assert.False(SqlServerConnectionStringCredentialGuard.ContainsPlaintextCredential(connectionString));
    }

    [Fact]
    public void ContainsPlaintextCredential_IntegratedSecurity_ReturnsFalse()
    {
        Assert.False(SqlServerConnectionStringCredentialGuard.ContainsPlaintextCredential(
            "Server=.;Database=Yagura;Integrated Security=true;"));
    }

    [Fact]
    public void ContainsPlaintextCredential_SqlAuthenticationWithPassword_ReturnsTrue()
    {
        Assert.True(SqlServerConnectionStringCredentialGuard.ContainsPlaintextCredential(
            "Server=.;Database=Yagura;User Id=yagura_svc;Password=hunter2;"));
    }

    [Fact]
    public void ContainsPlaintextCredential_SqlAuthenticationWithEmptyPassword_ReturnsFalse()
    {
        Assert.False(SqlServerConnectionStringCredentialGuard.ContainsPlaintextCredential(
            "Server=.;Database=Yagura;User Id=yagura_svc;Password=;"));
    }

    [Fact]
    public void ContainsPlaintextCredential_EncryptedPrefix_ReturnsFalse()
    {
        // DPAPI 暗号化表現のプレフィックス付きの値は「既に保護済み」として
        // 平文検出の対象から除外される（復号可否はここでは問わない——復号は
        // YaguraConfigurationLoader → DpapiConnectionStringProtector の管轄）。
        Assert.False(SqlServerConnectionStringCredentialGuard.ContainsPlaintextCredential(
            SqlServerConnectionStringCredentialGuard.EncryptedValuePrefix + "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAA="));
    }

    [Fact]
    public void ContainsPlaintextCredential_MalformedConnectionString_ReturnsFalse()
    {
        Assert.False(SqlServerConnectionStringCredentialGuard.ContainsPlaintextCredential("not a connection string ;;;==="));
    }
}
