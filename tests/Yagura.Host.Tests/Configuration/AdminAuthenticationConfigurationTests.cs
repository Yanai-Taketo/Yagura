using Microsoft.Extensions.Logging.Testing;
using Yagura.Host.Configuration;

namespace Yagura.Host.Tests.Configuration;

/// <summary>
/// 管理 UI 認証（ADR-0010 Phase 1）の設定解決・fail-closed 不変条件の単体テスト。
/// </summary>
/// <remarks>
/// Phase 1 受け入れ条件 (v)「loopback 認証 opt-in の fail-closed 不変条件が...CI 回帰テストで
/// 固定されている」の単体テスト側の固定（実プロセスを起動する E2E 側は
/// <c>tests/Yagura.E2E.Tests/AdminAuthenticationFailClosedRegressionTests.cs</c>）。
/// </remarks>
[Collection(ConfigurationEnvironmentVariableTestCollection.Name)]
public sealed class AdminAuthenticationConfigurationTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-authconfig-test-{Guid.NewGuid():N}");

    public AdminAuthenticationConfigurationTests()
    {
        Directory.CreateDirectory(_dataRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    private void WriteConfigurationFile(string json) =>
        File.WriteAllText(Path.Combine(_dataRoot, YaguraConfigurationLoader.ConfigurationFileName), json);

    [Fact]
    public void Load_ConfigurationFileMissing_AdminAuthenticationDefaultsToAllDisabled()
    {
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.False(result.Configuration.AdminWindowsAuthEnabled);
        Assert.False(result.Configuration.AdminWindowsAuthKerberosOnly);
        Assert.False(result.Configuration.AdminAppAuthEnabled);
        Assert.False(result.Configuration.AdminAuthRequireForLoopback);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public void Load_ValidAuthenticationFlags_ResolvesAsWritten(bool windows, bool kerberosOnly, bool app)
    {
        WriteConfigurationFile($$"""
            {
                "Admin": {
                    "Authentication": {
                        "Windows": { "Enabled": "{{windows}}", "KerberosOnly": "{{kerberosOnly}}" },
                        "App": { "Enabled": "{{app}}" }
                    }
                }
            }
            """);
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(windows, result.Configuration.AdminWindowsAuthEnabled);
        Assert.Equal(kerberosOnly, result.Configuration.AdminWindowsAuthKerberosOnly);
        Assert.Equal(app, result.Configuration.AdminAppAuthEnabled);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Load_InvalidAuthenticationFlagValue_ShrinksToDisabledWithWarning()
    {
        WriteConfigurationFile("""{ "Admin": { "Authentication": { "Windows": { "Enabled": "yes-please" } } } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.False(result.Configuration.AdminWindowsAuthEnabled);
        Assert.Single(result.Warnings);
        Assert.Equal("Admin:Authentication:Windows:Enabled", result.Warnings[0].Key);
    }

    [Fact]
    public void Load_RequireForLoopbackTrue_WithWindowsAuthEnabled_Succeeds()
    {
        WriteConfigurationFile("""
            {
                "Admin": {
                    "Authentication": {
                        "Windows": { "Enabled": "true" },
                        "RequireForLoopback": "true"
                    }
                }
            }
            """);
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.True(result.Configuration.AdminAuthRequireForLoopback);
        Assert.True(result.Configuration.AdminWindowsAuthEnabled);
    }

    [Fact]
    public void Load_RequireForLoopbackTrue_WithAppAuthEnabled_Succeeds()
    {
        WriteConfigurationFile("""
            {
                "Admin": {
                    "Authentication": {
                        "App": { "Enabled": "true" },
                        "RequireForLoopback": "true"
                    }
                }
            }
            """);
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.True(result.Configuration.AdminAuthRequireForLoopback);
        Assert.True(result.Configuration.AdminAppAuthEnabled);
    }

    [Fact]
    public void Load_RequireForLoopbackTrue_WithNoAuthMethodConfigured_ThrowsFailClosed()
    {
        WriteConfigurationFile("""{ "Admin": { "Authentication": { "RequireForLoopback": "true" } } }""");
        var logger = new FakeLogger();

        var exception = Assert.Throws<ConfigurationValidationException>(() => YaguraConfigurationLoader.Load(_dataRoot, logger));

        Assert.Equal(ConfigurationEventIds.AdminAuthenticationFailClosedStartupRejected.Id, exception.EventId?.Id);
        // 「なぜ起動しないか・何を直せばよいか」が一目で分かる誘導文言を含む
        // （ADR-0010 決定 1・委任事項 5）。
        Assert.Contains("Admin:Authentication:Windows:Enabled", exception.Message);
        Assert.Contains("Admin:Authentication:App:Enabled", exception.Message);
    }

    [Fact]
    public void Load_RequireForLoopbackTrue_WithBothAuthMethodsDisabledExplicitly_ThrowsFailClosed()
    {
        WriteConfigurationFile("""
            {
                "Admin": {
                    "Authentication": {
                        "Windows": { "Enabled": "false" },
                        "App": { "Enabled": "false" },
                        "RequireForLoopback": "true"
                    }
                }
            }
            """);
        var logger = new FakeLogger();

        Assert.Throws<ConfigurationValidationException>(() => YaguraConfigurationLoader.Load(_dataRoot, logger));
    }

    [Fact]
    public void Load_RequireForLoopbackFalse_WithNoAuthMethodConfigured_DoesNotThrow()
    {
        // 既定（loopback 認証 opt-in 無効）は現状維持——認証方式が一つも構成されていなくても
        // 起動は継続する（ADR-0010 決定 1「既定は現状維持」）。
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.False(result.Configuration.AdminAuthRequireForLoopback);
    }

    [Fact]
    public void KnownKeys_ContainAdminAuthenticationKeys()
    {
        Assert.Contains("Admin:Authentication:Windows:Enabled", YaguraConfigurationLoader.KnownKeys);
        Assert.Contains("Admin:Authentication:Windows:KerberosOnly", YaguraConfigurationLoader.KnownKeys);
        Assert.Contains("Admin:Authentication:App:Enabled", YaguraConfigurationLoader.KnownKeys);
        Assert.Contains("Admin:Authentication:RequireForLoopback", YaguraConfigurationLoader.KnownKeys);
    }

    [Fact]
    public void ConfigurationKeyMetadata_RegistersAllAdminAuthenticationKeys()
    {
        foreach (var key in new[]
        {
            "Admin:Authentication:Windows:Enabled",
            "Admin:Authentication:Windows:KerberosOnly",
            "Admin:Authentication:App:Enabled",
            "Admin:Authentication:RequireForLoopback",
        })
        {
            // 未登録なら KeyNotFoundException を送出する（ConfigurationKeyMetadata の契約）。
            ConfigurationKeyMetadata.GetReloadEffect(key);
        }
    }
}
