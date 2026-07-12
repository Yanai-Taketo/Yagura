using Microsoft.Extensions.Logging.Testing;
using Yagura.Host.Configuration;

namespace Yagura.Host.Tests.Configuration;

/// <summary>
/// 閲覧 UI 認証（ADR-0010 Phase 4 決定 7）+ AD グループマッピング（SEC-9）の設定解決の単体テスト。
/// </summary>
[Collection(ConfigurationEnvironmentVariableTestCollection.Name)]
public sealed class ViewerAuthenticationConfigurationTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-viewerauth-test-{Guid.NewGuid():N}");

    public ViewerAuthenticationConfigurationTests() => Directory.CreateDirectory(_dataRoot);

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
    public void Load_ConfigurationFileMissing_ViewerAuthenticationDefaultsToDisabledWithEmptyGroups()
    {
        var result = YaguraConfigurationLoader.Load(_dataRoot, new FakeLogger());

        Assert.False(result.Configuration.ViewerWindowsAuthEnabled);
        Assert.False(result.Configuration.ViewerWindowsAuthKerberosOnly);
        Assert.Empty(result.Configuration.ViewerWindowsViewerGroups);
        Assert.Empty(result.Configuration.ViewerWindowsAdminGroups);
        Assert.Empty(result.Configuration.AdminWindowsAdminGroups);
    }

    [Fact]
    public void Load_ViewerAuthEnabledWithGroups_ResolvesFlagsAndGroupSpecs()
    {
        WriteConfigurationFile("""
            {
                "Viewer": {
                    "Authentication": {
                        "Windows": {
                            "Enabled": "true",
                            "KerberosOnly": "true",
                            "ViewerGroups": [ "YAGURA\\LogViewers", "S-1-5-21-1-2-3-4001" ],
                            "AdminGroups": [ "YAGURA\\LogAdmins" ]
                        }
                    }
                }
            }
            """);

        var result = YaguraConfigurationLoader.Load(_dataRoot, new FakeLogger());

        Assert.True(result.Configuration.ViewerWindowsAuthEnabled);
        Assert.True(result.Configuration.ViewerWindowsAuthKerberosOnly);
        Assert.Equal(new[] { "YAGURA\\LogViewers", "S-1-5-21-1-2-3-4001" }, result.Configuration.ViewerWindowsViewerGroups);
        Assert.Equal(new[] { "YAGURA\\LogAdmins" }, result.Configuration.ViewerWindowsAdminGroups);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Load_AdminGroupsArray_ResolvesForSec9()
    {
        WriteConfigurationFile("""
            {
                "Admin": {
                    "Authentication": {
                        "Windows": { "Enabled": "true", "AdminGroups": [ "YAGURA\\Domain Admins", "S-1-5-21-9-9-9-512" ] }
                    }
                }
            }
            """);

        var result = YaguraConfigurationLoader.Load(_dataRoot, new FakeLogger());

        Assert.Equal(new[] { "YAGURA\\Domain Admins", "S-1-5-21-9-9-9-512" }, result.Configuration.AdminWindowsAdminGroups);
    }

    [Fact]
    public void Load_GroupSpecs_TrimAndDeduplicateCaseInsensitively()
    {
        WriteConfigurationFile("""
            {
                "Viewer": {
                    "Authentication": {
                        "Windows": {
                            "Enabled": "true",
                            "ViewerGroups": [ "  YAGURA\\Viewers  ", "yagura\\viewers", "", "   " ]
                        }
                    }
                }
            }
            """);

        var result = YaguraConfigurationLoader.Load(_dataRoot, new FakeLogger());

        // 空白 trim・空要素除去・大文字小文字無視の重複排除で 1 件に畳まれる（順序保持で先勝ち）。
        Assert.Equal(new[] { "YAGURA\\Viewers" }, result.Configuration.ViewerWindowsViewerGroups);
    }

    [Fact]
    public void Load_GroupArrayIndexedChildren_AreNotFlaggedAsUnknownKeys()
    {
        // 配列キーはインデックス付きリーフ（...:0・...:1）に展開されるが、KnownArrayKeys により
        // 未知キー扱いにならない（DetectUnknownKeys の IsKnownArrayElement）。
        WriteConfigurationFile("""
            {
                "Viewer": { "Authentication": { "Windows": { "ViewerGroups": [ "A", "B" ], "AdminGroups": [ "C" ] } } },
                "Admin": { "Authentication": { "Windows": { "AdminGroups": [ "D" ] } } }
            }
            """);

        var result = YaguraConfigurationLoader.Load(_dataRoot, new FakeLogger());

        Assert.Empty(result.UnknownKeys);
    }

    [Fact]
    public void KnownKeys_And_KnownArrayKeys_ContainViewerAuthAndGroupKeys()
    {
        Assert.Contains("Viewer:Authentication:Windows:Enabled", YaguraConfigurationLoader.KnownKeys);
        Assert.Contains("Viewer:Authentication:Windows:KerberosOnly", YaguraConfigurationLoader.KnownKeys);

        Assert.Contains("Admin:Authentication:Windows:AdminGroups", YaguraConfigurationLoader.KnownArrayKeys);
        Assert.Contains("Viewer:Authentication:Windows:ViewerGroups", YaguraConfigurationLoader.KnownArrayKeys);
        Assert.Contains("Viewer:Authentication:Windows:AdminGroups", YaguraConfigurationLoader.KnownArrayKeys);
    }
}
