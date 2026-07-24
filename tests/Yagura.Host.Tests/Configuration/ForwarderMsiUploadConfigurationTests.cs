using Microsoft.Extensions.Logging.Testing;
using Yagura.Host.Configuration;

namespace Yagura.Host.Tests.Configuration;

/// <summary>
/// フォワーダ MSI アップロード（ADR-0020 決定 1）の設定解決・fail-closed 不変条件の単体テスト。
/// </summary>
/// <remarks>
/// ADR-0020 決定 5 ①「fail-closed 起動拒否——1011/1012 と同型の実プロセス E2E」の単体テスト側の
/// 固定（実プロセスを起動する E2E 側は
/// <c>tests/Yagura.E2E.Tests/ForwarderMsiUploadFailClosedRegressionTests.cs</c>。
/// <see cref="AdminAuthenticationConfigurationTests"/> と同じ二層構成）。
/// </remarks>
[Collection(ConfigurationEnvironmentVariableTestCollection.Name)]
public sealed class ForwarderMsiUploadConfigurationTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-msiupload-config-test-{Guid.NewGuid():N}");

    public ForwarderMsiUploadConfigurationTests()
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
    public void Load_ConfigurationFileMissing_MsiUploadDefaultsToDisabled()
    {
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.False(result.Configuration.AdminForwarderMsiUploadEnabled);
    }

    [Fact]
    public void Load_MsiUploadEnabled_WithAuthAndRequireForLoopback_Succeeds()
    {
        // 前提条件が揃った唯一の正当な有効化構成（ADR-0020 決定 1: (i) 認証有効 + (ii)
        // RequireForLoopback + (iii) 機能 opt-in）。
        WriteConfigurationFile("""
            {
                "Admin": {
                    "Authentication": {
                        "App": { "Enabled": "true" },
                        "RequireForLoopback": "true"
                    },
                    "ForwarderKit": { "MsiUpload": { "Enabled": "true" } }
                }
            }
            """);
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.True(result.Configuration.AdminForwarderMsiUploadEnabled);
        Assert.True(result.Configuration.AdminAuthRequireForLoopback);
        Assert.True(result.Configuration.AdminAppAuthEnabled);
    }

    [Fact]
    public void Load_MsiUploadEnabled_WithoutRequireForLoopback_ThrowsFailClosed()
    {
        // 認証は有効だが loopback 認証 opt-in が無い——管理リスナに無認証の到達経路が残る構成
        // での有効化は起動を拒否する（ADR-0020 決定 1。1032）。
        WriteConfigurationFile("""
            {
                "Admin": {
                    "Authentication": {
                        "App": { "Enabled": "true" }
                    },
                    "ForwarderKit": { "MsiUpload": { "Enabled": "true" } }
                }
            }
            """);
        var logger = new FakeLogger();

        var exception = Assert.Throws<ConfigurationValidationException>(() => YaguraConfigurationLoader.Load(_dataRoot, logger));

        Assert.Equal(ConfigurationEventIds.ForwarderMsiUploadFailClosedStartupRejected.Id, exception.EventId?.Id);
        Assert.Contains("Admin:Authentication:RequireForLoopback", exception.Message);
        // 復旧に必要な具体の設定キーと値を明記する（ADR-0020 委任 1——手編集復旧の場面では
        // UI の誘導が使えない。再レビュー鈴木指摘）。
        Assert.Contains("Admin:ForwarderKit:MsiUpload:Enabled を false に戻して", exception.Message);
    }

    [Fact]
    public void Load_MsiUploadEnabled_WithoutAnyAuthMethod_ThrowsFailClosed()
    {
        // 認証方式がひとつも無い + RequireForLoopback も無い——欠けている条件が両方列挙される。
        WriteConfigurationFile("""{ "Admin": { "ForwarderKit": { "MsiUpload": { "Enabled": "true" } } } }""");
        var logger = new FakeLogger();

        var exception = Assert.Throws<ConfigurationValidationException>(() => YaguraConfigurationLoader.Load(_dataRoot, logger));

        Assert.Equal(ConfigurationEventIds.ForwarderMsiUploadFailClosedStartupRejected.Id, exception.EventId?.Id);
        Assert.Contains("Admin:Authentication:Windows:Enabled", exception.Message);
        Assert.Contains("Admin:Authentication:App:Enabled", exception.Message);
        Assert.Contains("Admin:Authentication:RequireForLoopback", exception.Message);
    }

    [Fact]
    public void Load_MsiUploadInvalidFlagValue_ShrinksToDisabledWithWarning()
    {
        // 不正値は有効側へ落とさない（§1「縮小側で継続」——書き込み系の管理機能の縮小方向は無効）。
        WriteConfigurationFile("""{ "Admin": { "ForwarderKit": { "MsiUpload": { "Enabled": "yes-please" } } } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.False(result.Configuration.AdminForwarderMsiUploadEnabled);
        Assert.Contains(result.Warnings, w => w.Key == "Admin:ForwarderKit:MsiUpload:Enabled");
    }

    [Fact]
    public void Load_MsiUploadDisabled_WithoutPreconditions_DoesNotThrow()
    {
        // (iii) が無効なら前提条件の検証自体が発生しない（既定構成の非退行）。
        WriteConfigurationFile("""{ "Admin": { "ForwarderKit": { "MsiUpload": { "Enabled": "false" } } } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.False(result.Configuration.AdminForwarderMsiUploadEnabled);
    }
}
