using Microsoft.Extensions.Logging.Testing;
using Yagura.Host.Configuration;
using Yagura.TestSupport;

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
    private readonly TestTempDirectory _tempDir = new("msiupload-config-test");
    private string _dataRoot => _tempDir.Path;

    public ForwarderMsiUploadConfigurationTests()
    {
        Directory.CreateDirectory(_dataRoot);
    }

    public void Dispose() => _tempDir.Dispose();

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
        // 認証有効 + RequireForLoopback + opt-in——ADR-0020 当時の正当構成は改訂後も当然に有効
        // （RequireForLoopback 自体の機能は不変。ADR-0021 決定 2）。
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
    public void Load_MsiUploadEnabled_WithoutRequireForLoopback_Succeeds()
    {
        // ADR-0021 決定 2: 条件 (ii)（RequireForLoopback 必須）は撤廃——認証方式が構成済みなら
        // RequireForLoopback = false（既定）でも有効化できる。無認証 loopback からの到達遮断は
        // アップロード操作単位の専用認可ポリシーが担う（構成レベルの検証対象ではない）。
        // 旧仕様（ADR-0020 決定 1 (ii)）ではこの構成は 1032 起動拒否だった——仕様変更の回帰固定。
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

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.True(result.Configuration.AdminForwarderMsiUploadEnabled);
        Assert.False(result.Configuration.AdminAuthRequireForLoopback);
        Assert.True(result.Configuration.AdminAppAuthEnabled);
    }

    [Fact]
    public void Load_MsiUploadEnabled_WithoutAnyAuthMethod_ThrowsFailClosed()
    {
        // 認証方式がひとつも無い——サインインの手段が存在しなければ操作単位認可を誰も通過
        // できないため、引き続き起動拒否（ADR-0021 決定 2。1032）。
        WriteConfigurationFile("""{ "Admin": { "ForwarderKit": { "MsiUpload": { "Enabled": "true" } } } }""");
        var logger = new FakeLogger();

        var exception = Assert.Throws<ConfigurationValidationException>(() => YaguraConfigurationLoader.Load(_dataRoot, logger));

        Assert.Equal(ConfigurationEventIds.ForwarderMsiUploadFailClosedStartupRejected.Id, exception.EventId?.Id);
        Assert.Contains("Admin:Authentication:Windows:Enabled", exception.Message);
        Assert.Contains("Admin:Authentication:App:Enabled", exception.Message);
        // RequireForLoopback は前提条件ではなくなった（ADR-0021）——メッセージにも現れない。
        Assert.DoesNotContain("Admin:Authentication:RequireForLoopback", exception.Message);
        // 復旧に必要な具体の設定キーと値を明記する（ADR-0020 委任 1——手編集復旧の場面では
        // UI の誘導が使えない）。
        Assert.Contains("Admin:ForwarderKit:MsiUpload:Enabled を false に戻して", exception.Message);
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
    public void Load_MsiUploadEnabledKey_IsKnown_NotReportedAsUnknown()
    {
        // Issue #439: 導入 PR #431 で KnownKeys への登録が漏れ、「未知のキーとして無視します」
        // 警告と 1032 fail-closed 検証が同一起動で共存する矛盾メッセージになっていた
        // （「無視した」と警告したキーで起動拒否する）。登録漏れの回帰検知
        // （Load_ViewerReverseDnsEnabledKey_IsKnown と同じパターン）。
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

        Assert.Empty(result.UnknownKeys);
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
