using Yagura.Web.ForwarderKit;

namespace Yagura.Web.Tests.ForwarderKit;

/// <summary>
/// <see cref="ForwarderMsiFilter"/> の境界テスト（ADR-0008 設計条件 9・委任 #7——ファイル名
/// パターン一致・版解決・公式ハッシュ照合の判定を列挙 I/O から分離して固定する）。
/// </summary>
public sealed class ForwarderMsiFilterTests
{
    [Theory]
    [InlineData("fluent-bit-4.0.14-win64.msi")]
    [InlineData("fluent-bit-5.0.0-win64.msi")]
    [InlineData("FLUENT-BIT-4.0.14-WIN64.MSI")]
    public void IsCandidateFileName_MatchingPattern_True(string fileName)
    {
        Assert.True(ForwarderMsiFilter.IsCandidateFileName(fileName));
    }

    [Theory]
    [InlineData("fluent-bit-4.0.14-win32.msi")]
    [InlineData("fluent-bit-win64.msi")]
    [InlineData("other-4.0.14-win64.msi")]
    [InlineData("fluent-bit-4.0.14-win64.exe")]
    [InlineData("")]
    public void IsCandidateFileName_NonMatchingPattern_False(string fileName)
    {
        Assert.False(ForwarderMsiFilter.IsCandidateFileName(fileName));
    }

    [Fact]
    public void ExtractVersionFromFileName_StandardName_ReturnsVersion()
    {
        Assert.Equal("4.0.14", ForwarderMsiFilter.ExtractVersionFromFileName("fluent-bit-4.0.14-win64.msi"));
    }

    [Fact]
    public void ExtractVersionFromFileName_NonMatchingName_ReturnsNull()
    {
        Assert.Null(ForwarderMsiFilter.ExtractVersionFromFileName("not-a-recognized-name.msi"));
    }

    [Fact]
    public void ResolveEffectiveVersion_ProductVersionPresent_PrefersProductVersion()
    {
        // ファイル名がリネームされて版が異なっていても、ProductVersion を優先する
        // （設計条件 9「ファイル名だけに依拠しない」）。
        var effective = ForwarderMsiFilter.ResolveEffectiveVersion("4.0.14", "fluent-bit-9.9.9-win64.msi");

        Assert.Equal("4.0.14", effective);
    }

    [Fact]
    public void ResolveEffectiveVersion_ProductVersionMissing_FallsBackToFileName()
    {
        var effective = ForwarderMsiFilter.ResolveEffectiveVersion(null, "fluent-bit-4.0.14-win64.msi");

        Assert.Equal("4.0.14", effective);
    }

    [Fact]
    public void ResolveEffectiveVersion_ProductVersionWhitespace_FallsBackToFileName()
    {
        var effective = ForwarderMsiFilter.ResolveEffectiveVersion("   ", "fluent-bit-4.0.14-win64.msi");

        Assert.Equal("4.0.14", effective);
    }

    [Fact]
    public void ResolveEffectiveVersion_BothMissing_ReturnsNull()
    {
        var effective = ForwarderMsiFilter.ResolveEffectiveVersion(null, "not-a-recognized-name.msi");

        Assert.Null(effective);
    }

    [Fact]
    public void MatchesVerifiedVersion_ExactMatch_True()
    {
        Assert.True(ForwarderMsiFilter.MatchesVerifiedVersion("4.0.14", "4.0.14"));
    }

    [Fact]
    public void MatchesVerifiedVersion_CaseInsensitiveMatch_True()
    {
        Assert.True(ForwarderMsiFilter.MatchesVerifiedVersion("4.0.14", "4.0.14"));
    }

    [Fact]
    public void MatchesVerifiedVersion_DifferentVersion_False()
    {
        Assert.False(ForwarderMsiFilter.MatchesVerifiedVersion("4.0.13", "4.0.14"));
    }

    [Fact]
    public void MatchesVerifiedVersion_NullEffectiveVersion_False()
    {
        Assert.False(ForwarderMsiFilter.MatchesVerifiedVersion(null, "4.0.14"));
    }

    [Fact]
    public void MatchesOfficialHash_OfficialHashNull_ReturnsUnverified()
    {
        var result = ForwarderMsiFilter.MatchesOfficialHash("abc123", null);

        Assert.Equal(OfficialHashMatchResult.Unverified, result);
    }

    [Fact]
    public void MatchesOfficialHash_OfficialHashWhitespace_ReturnsUnverified()
    {
        var result = ForwarderMsiFilter.MatchesOfficialHash("abc123", "   ");

        Assert.Equal(OfficialHashMatchResult.Unverified, result);
    }

    [Fact]
    public void MatchesOfficialHash_ExactMatch_ReturnsMatch()
    {
        var result = ForwarderMsiFilter.MatchesOfficialHash("abc123", "abc123");

        Assert.Equal(OfficialHashMatchResult.Match, result);
    }

    [Fact]
    public void MatchesOfficialHash_CaseInsensitiveMatch_ReturnsMatch()
    {
        var result = ForwarderMsiFilter.MatchesOfficialHash("ABC123", "abc123");

        Assert.Equal(OfficialHashMatchResult.Match, result);
    }

    [Fact]
    public void MatchesOfficialHash_Mismatch_ReturnsMismatch()
    {
        var result = ForwarderMsiFilter.MatchesOfficialHash("abc123", "def456");

        Assert.Equal(OfficialHashMatchResult.Mismatch, result);
    }
}
