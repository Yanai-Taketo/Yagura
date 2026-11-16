using Yagura.TestSupport;
using Yagura.Web.Administration;
using Yagura.Web.Components.Common;

namespace Yagura.Web.Tests.Administration;

/// <summary>
/// <see cref="FirewallRuleGuidance"/>（受信許可規則の作成コマンドの組み立て。configuration.md §4.3）の検証と、
/// 画面の案内・運用ガイドの手順が同じ形のコマンドを示していることの固定。
/// </summary>
public sealed class FirewallRuleGuidanceTests
{
    private const string DefaultInstallPath = @"C:\Program Files\Yagura\Yagura.Host.exe";

    [Theory]
    [InlineData(null, 6514)]
    [InlineData("", 6514)]
    [InlineData("   ", 6514)]
    [InlineData("7514", 7514)]
    [InlineData(" 7514 ", 7514)]
    [InlineData("0", 6514)]
    [InlineData("65536", 6514)]
    [InlineData("abc", 6514)]
    public void ResolveEffectivePort_FollowsLoaderFallbackToDefault(string? persisted, int expected)
    {
        Assert.Equal(expected, FirewallRuleGuidance.ResolveEffectivePort(persisted, FirewallRuleGuidance.DefaultIngestionTlsPort));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Yagura\Yagura.Host.exe", @"C:\Program Files\Yagura\Yagura.Host.exe")]
    [InlineData(@"D:\Apps\Yagura\YAGURA.HOST.EXE", @"D:\Apps\Yagura\YAGURA.HOST.EXE")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", null)]
    [InlineData("/usr/share/dotnet/dotnet", null)]
    [InlineData(@"C:\Yagura.Host.exe.bak\testhost.exe", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ResolveHostExecutablePath_AcceptsOnlyTheServiceExecutable(string? processPath, string? expected)
    {
        Assert.Equal(expected, FirewallRuleGuidance.ResolveHostExecutablePath(processPath));
    }

    [Fact]
    public void BuildCommand_WithProgram_MatchesInstallerRuleConditions()
    {
        var command = FirewallRuleGuidance.BuildNewNetFirewallRuleCommand("Yagura Syslog (TLS 7514)", 7514, DefaultInstallPath);

        Assert.Equal(
            "New-NetFirewallRule -DisplayName 'Yagura Syslog (TLS 7514)' `\n" +
            "  -Direction Inbound -Action Allow -Protocol TCP -LocalPort 7514 `\n" +
            "  -Profile Domain,Private `\n" +
            @"  -Program 'C:\Program Files\Yagura\Yagura.Host.exe'",
            command);
    }

    [Fact]
    public void BuildCommand_WithoutProgram_EndsWithoutDanglingContinuation()
    {
        // 最終行に行継続（バッククォート）が残ると、貼り付けたシェルが続きの入力を待ち続ける。
        var command = FirewallRuleGuidance.BuildNewNetFirewallRuleCommand("Yagura Syslog (TLS 6514)", 6514, programPath: null);

        Assert.DoesNotContain("-Program", command, StringComparison.Ordinal);
        Assert.EndsWith("  -Profile Domain,Private", command, StringComparison.Ordinal);
        Assert.All(command.Split('\n'), line => Assert.False(line.EndsWith(' '), $"行末に空白がある（行継続が切れる）: [{line}]"));
    }

    [Fact]
    public void BuildCommand_PathWithSingleQuote_IsEscapedForPowerShellLiteral()
    {
        var command = FirewallRuleGuidance.BuildNewNetFirewallRuleCommand("Yagura Syslog (TLS 6514)", 6514, @"C:\O'Brien\Yagura.Host.exe");

        Assert.Contains(@"-Program 'C:\O''Brien\Yagura.Host.exe'", command, StringComparison.Ordinal);
    }

    [Fact]
    public void ForIngestionTls_And_ForAdminRemoteHttps_UseYaguraNamespaceRuleNames()
    {
        // 規則名が「Yagura」で始まらないと、Host の起動時突合（Yagura 名前空間のみが対象）から見えない。
        var tls = FirewallRuleGuidance.ForIngestionTls("7514", DefaultInstallPath);
        var admin = FirewallRuleGuidance.ForAdminRemoteHttps(null, DefaultInstallPath);

        Assert.Equal(7514, tls.Port);
        Assert.Equal(DefaultInstallPath, tls.ProgramPath);
        Assert.StartsWith("New-NetFirewallRule -DisplayName 'Yagura ", tls.Command, StringComparison.Ordinal);
        Assert.Equal(8516, admin.Port);
        Assert.StartsWith("New-NetFirewallRule -DisplayName 'Yagura ", admin.Command, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationsGuide_ShowsTheSameCommandsAsTheScreens()
    {
        // 画面の案内と運用ガイドの手順で、コマンドの形（規則名・条件）が食い違わないこと。
        var guide = ReadOperationsGuide();

        Assert.Contains(FirewallRuleGuidance.ForIngestionTls(null, DefaultInstallPath).Command, guide, StringComparison.Ordinal);
        Assert.Contains(FirewallRuleGuidance.ForAdminRemoteHttps(null, DefaultInstallPath).Command, guide, StringComparison.Ordinal);
    }

    [Fact]
    public void ScreenFollowUp_PointsToAnExistingOperationsGuideHeading()
    {
        // 画面が名指しする節が運用ガイドに実在すること（節の付け替えで参照が宙に浮くのを防ぐ）。
        Assert.Contains("docs/operations/firewall.md", UiText.FirewallRuleFollowUp, StringComparison.Ordinal);
        Assert.Contains("「ファイアウォール規則を自分で足す」", UiText.FirewallRuleFollowUp, StringComparison.Ordinal);
        Assert.StartsWith("# ファイアウォール規則を自分で足す\n", ReadOperationsGuide(), StringComparison.Ordinal);
    }

    private static string ReadOperationsGuide() =>
        File.ReadAllText(Path.Combine(RepositoryPaths.FindRoot(), "docs", "operations", "firewall.md")).ReplaceLineEndings("\n");
}
