using Yagura.TestSupport;
using Yagura.Web.ForwarderKit;

namespace Yagura.Web.Tests.ForwarderKit;

/// <summary>
/// <see cref="ForwarderKitConstraints.VerifiedFluentBitVersion"/> の表明が、利用者ガイドおよび
/// 生成 README テンプレートの版表記と一致することを固定する（ADR-0008 委任 #3——
/// 表明文字列と実体の乖離を CI で機械検知する）。
/// </summary>
public sealed class ForwarderKitVersionSyncTests
{
    [Fact]
    public void VerifiedVersion_MatchesForwardWindowsEventlogGuide()
    {
        var repoRoot = RepositoryPaths.FindRoot();
        var guidePath = Path.Combine(repoRoot, "docs", "guides", "forward-windows-eventlog.md");
        var guide = File.ReadAllText(guidePath);

        Assert.Contains(
            $"Fluent Bit **{ForwarderKitConstraints.VerifiedFluentBitVersion}**",
            guide);
        Assert.Contains(
            $"fluent-bit-{ForwarderKitConstraints.VerifiedFluentBitVersion}-win64.msi",
            guide);
    }

    [Fact]
    public void VerifiedVersion_MatchesGeneratedReadmeTemplatePlaceholderUsage()
    {
        // README.generated.md 自体は @@FLUENTBIT_VERSION@@ プレースホルダを使う（実体の版番号を
        // 直書きしない）ため、本テストはプレースホルダの存在と、ForwarderKitBuilder が
        // それを ForwarderKitConstraints.VerifiedFluentBitVersion で置換する契約が保たれている
        // ことを、ビルド結果（ForwarderKitBuilderTests）とあわせて確認する趣旨で、
        // まずテンプレート側にプレースホルダが存在することを固定する。
        var repoRoot = RepositoryPaths.FindRoot();
        var templatePath = Path.Combine(repoRoot, "forwarder", "fluent-bit", "README.generated.md");
        var template = File.ReadAllText(templatePath);

        Assert.Contains("@@FLUENTBIT_VERSION@@", template);
    }
}
