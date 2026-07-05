using Yagura.Host.Configuration;

namespace Yagura.Host.Tests.Configuration;

/// <summary>
/// <see cref="SqlServerConnectionStringCredentialGuard"/> のテスト（Issue #47。DPAPI 暗号化の
/// 挿入点——平文検出の枠のみ。暗号化自体は未実装）。
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
        // 将来の DPAPI 暗号化表現（未実装）の予約プレフィックス。実装前でも
        // 「これは既に保護済みとして扱う」という規約だけは固定しておく。
        Assert.False(SqlServerConnectionStringCredentialGuard.ContainsPlaintextCredential(
            SqlServerConnectionStringCredentialGuard.EncryptedValuePrefix + "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAA="));
    }

    [Fact]
    public void ContainsPlaintextCredential_MalformedConnectionString_ReturnsFalse()
    {
        Assert.False(SqlServerConnectionStringCredentialGuard.ContainsPlaintextCredential("not a connection string ;;;==="));
    }
}
