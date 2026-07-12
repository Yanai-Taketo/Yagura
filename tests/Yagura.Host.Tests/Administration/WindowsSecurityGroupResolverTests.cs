using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Testing;
using Yagura.Host.Administration.AdminAuthentication;

namespace Yagura.Host.Tests.Administration;

/// <summary>
/// <see cref="WindowsSecurityGroupResolver"/>（SEC-9。ADR-0010 決定 5・7・委任事項 8）の単体テスト。
/// </summary>
/// <remarks>
/// 名 → SID 変換（<c>NTAccount.Translate</c>）は Windows 専用のため、名前解決を伴うケースは
/// <see cref="OperatingSystem.IsWindows"/> で実行時ガードする（本 lab 機は Windows——実機で通る）。
/// SID 形式の受理・正規化・不正指定のスキップはプラットフォームに依存しない。
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsSecurityGroupResolverTests
{
    [Fact]
    public void ResolveToSids_EmptyInput_ReturnsEmpty()
    {
        var result = WindowsSecurityGroupResolver.ResolveToSids(Array.Empty<string>(), "Test:Key", new FakeLogger());
        Assert.Empty(result);
    }

    [Fact]
    public void ResolveToSids_SidForm_PassesThroughNormalizedUppercase()
    {
        // 既に SID 形式で与えられた指定は変換問い合わせを発さずに正規化して受理する。
        var result = WindowsSecurityGroupResolver.ResolveToSids(
            new[] { "s-1-5-32-544" }, "Test:Key", new FakeLogger());

        Assert.Contains("S-1-5-32-544", result);
        Assert.Single(result);
    }

    [Fact]
    public void ResolveToSids_MalformedSid_IsSkippedWithWarning()
    {
        var logger = new FakeLogger();
        var result = WindowsSecurityGroupResolver.ResolveToSids(
            new[] { "S-1-not-a-valid-sid" }, "Test:Key", logger);

        Assert.Empty(result);
        Assert.Contains(logger.Collector.GetSnapshot(), r => r.Message.Contains("sec9-group-unresolved"));
    }

    [Fact]
    public void ResolveToSids_NonexistentAccountName_IsSkippedWithWarning_NotThrow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var logger = new FakeLogger();
        // 存在しないアカウント名は解決できず、起動を止めずにスキップされる（認可を付与しない安全側）。
        var result = WindowsSecurityGroupResolver.ResolveToSids(
            new[] { $"YAGURA\\NoSuchGroup-{Guid.NewGuid():N}" }, "Test:Key", logger);

        Assert.Empty(result);
        Assert.Contains(logger.Collector.GetSnapshot(), r => r.Message.Contains("sec9-group-unresolved"));
    }

    [Fact]
    public void ResolveToSids_WellKnownAccountName_ResolvesToSid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // BUILTIN\Administrators は全 Windows 機に存在し、well-known SID S-1-5-32-544 へ解決される。
        var result = WindowsSecurityGroupResolver.ResolveToSids(
            new[] { "BUILTIN\\Administrators" }, "Test:Key", new FakeLogger());

        Assert.Contains("S-1-5-32-544", result);
    }
}
