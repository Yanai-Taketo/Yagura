using Yagura.Host.Administration.AdminAuthentication;

namespace Yagura.Host.Tests.Administration.AdminAuthentication;

/// <summary>
/// <see cref="AdminPasswordBlocklist"/>（ADR-0011 決定 7・委任事項 5）の単体テスト。
/// </summary>
/// <remarks>
/// 供給網（出典・ライセンス・加工内容）は
/// <c>src/Yagura.Host/Administration/AdminAuthentication/PasswordBlocklist/PROVENANCE.md</c> 参照。
/// </remarks>
public sealed class AdminPasswordBlocklistTests
{
    [Fact]
    public void Count_MeetsMinimumSizeTarget()
    {
        // ADR-0011 決定 7・10: 同梱辞書は少なくとも上位数万〜数十万語級を下限の目安とする
        // （数百語級の「やってる感」だけのリストにしない）。
        Assert.True(
            AdminPasswordBlocklist.Count >= 10_000,
            $"同梱辞書の件数が下限の目安を下回っている（実測: {AdminPasswordBlocklist.Count}）。");
    }

    [Theory]
    [InlineData("123456")]
    [InlineData("password")]
    [InlineData("qwerty")]
    [InlineData("000000000000")]
    public void IsBlocked_KnownCommonPassword_ReturnsTrue(string password)
    {
        Assert.True(AdminPasswordBlocklist.IsBlocked(password));
    }

    [Fact]
    public void IsBlocked_IsCaseInsensitive()
    {
        Assert.True(AdminPasswordBlocklist.IsBlocked("PASSWORD"));
        Assert.True(AdminPasswordBlocklist.IsBlocked("PaSsWoRd"));
    }

    [Fact]
    public void IsBlocked_RandomLongPassword_ReturnsFalse()
    {
        // 十分にランダムなパスワードは既知漏洩辞書に含まれない
        // （固定文字列だが、辞書突合の対象にならないことの確認が目的であり、乱数生成の
        // 実装検証ではない）。
        Assert.False(AdminPasswordBlocklist.IsBlocked("Xk7$mQ2!vLp9#Zt4wR8@nB3"));
    }
}
