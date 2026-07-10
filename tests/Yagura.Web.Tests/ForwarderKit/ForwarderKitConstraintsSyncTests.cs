using System.Text.RegularExpressions;
using Yagura.Web.ForwarderKit;

namespace Yagura.Web.Tests.ForwarderKit;

/// <summary>
/// <see cref="ForwarderKitConstraints"/> の置換値検証パターンが、リポジトリの
/// <c>forwarder/fluent-bit/install.ps1</c> の <c>ValidatePattern</c> 定義と一致することを
/// 固定する（ADR-0008 設計条件 5・委任 #2。<c>Yagura.Bench.Tests.BaselineComparatorTests</c> と
/// 同じ「リポジトリルートを遡って実ファイルを読む」パターンを踏襲する）。
/// </summary>
public sealed class ForwarderKitConstraintsSyncTests
{
    [Fact]
    public void HostPattern_MatchesInstallScriptValidatePattern()
    {
        var installScript = ReadInstallScript();

        var hostPattern = ExtractValidatePattern(installScript, "YaguraHost");

        Assert.Equal(ForwarderKitConstraints.HostPattern, hostPattern);
    }

    [Fact]
    public void ChannelsPattern_MatchesInstallScriptValidatePattern()
    {
        var installScript = ReadInstallScript();

        var channelsPattern = ExtractValidatePattern(installScript, "Channels");

        Assert.Equal(ForwarderKitConstraints.ChannelsPattern, channelsPattern);
    }

    [Fact]
    public void PortRange_MatchesInstallScriptValidateRange()
    {
        var installScript = ReadInstallScript();

        // install.ps1: [ValidateRange(1, 65535)] の直後に [int]$YaguraPort が続く。
        var match = Regex.Match(installScript, @"\[ValidateRange\((\d+),\s*(\d+)\)\]\s*\r?\n\s*\[int\]\$YaguraPort");
        Assert.True(match.Success, "install.ps1 に $YaguraPort の ValidateRange が見つからない。");

        Assert.Equal(ForwarderKitConstraints.MinPort, int.Parse(match.Groups[1].Value));
        Assert.Equal(ForwarderKitConstraints.MaxPort, int.Parse(match.Groups[2].Value));
    }

    [Fact]
    public void DefaultPort_MatchesInstallScriptDefault()
    {
        var installScript = ReadInstallScript();

        var match = Regex.Match(installScript, @"\[int\]\$YaguraPort\s*=\s*(\d+)");
        Assert.True(match.Success, "install.ps1 に $YaguraPort の既定値が見つからない。");

        Assert.Equal(ForwarderKitConstraints.DefaultPort, int.Parse(match.Groups[1].Value));
    }

    /// <summary>
    /// install.ps1 の <c>Get-LocalMsiFilenamePattern</c>（ADR-0009 決定7・委任 #4）が、
    /// <see cref="ForwarderMsiConstraints.FileNamePattern"/> / <see cref="ForwarderMsiConstraints.FileNamePatternArm64"/>
    /// と同一のファイル名パターン文字列を使っていることを固定する。
    /// </summary>
    [Fact]
    public void MsiArchitectureFileNamePatterns_MatchInstallScriptLocalArchDetection()
    {
        var installScript = ReadInstallScript();

        Assert.Contains(
            "\"" + ForwarderMsiConstraints.FileNamePattern + "\"",
            installScript);
        Assert.Contains(
            "\"" + ForwarderMsiConstraints.FileNamePatternArm64 + "\"",
            installScript);
    }

    /// <summary>
    /// 指定パラメータ名の直前にある <c>[ValidatePattern('...')]</c> の正規表現文字列を抽出する。
    /// </summary>
    private static string ExtractValidatePattern(string script, string parameterName)
    {
        var match = Regex.Match(
            script,
            @"\[ValidatePattern\('([^']*)'\)\]\s*\r?\n(?:\s*\[[^\]]*\]\s*\r?\n)*\s*\[string\]\$" + Regex.Escape(parameterName));

        Assert.True(match.Success, $"install.ps1 に ${parameterName} の ValidatePattern が見つからない。");
        return match.Groups[1].Value;
    }

    private static string ReadInstallScript()
    {
        var repoRoot = FindRepositoryRoot();
        var path = Path.Combine(repoRoot, "forwarder", "fluent-bit", "install.ps1");

        Assert.True(File.Exists(path), $"install.ps1 が見つからない: {path}");
        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagura.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Yagura.sln を含むリポジトリルートが見つからない。");
    }
}
