using Yagura.Web.Export;

namespace Yagura.Web.Tests.Export;

/// <summary>
/// <see cref="CsvField"/> の RFC 4180 エスケープ + CSV インジェクション対策の単体テスト（Issue #157）。
/// </summary>
public sealed class CsvFieldTests
{
    [Fact]
    public void Escape_PlainValue_ReturnsUnchanged()
    {
        Assert.Equal("plain", CsvField.Escape("plain"));
    }

    [Fact]
    public void Escape_Null_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, CsvField.Escape(null));
    }

    [Fact]
    public void Escape_EmptyString_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, CsvField.Escape(string.Empty));
    }

    [Fact]
    public void Escape_JapaneseText_ReturnsUnchanged()
    {
        // 日本語（マルチバイト文字）はカンマ・引用符・改行を含まない限りエスケープ不要。
        Assert.Equal("認証エラーが発生しました", CsvField.Escape("認証エラーが発生しました"));
    }

    [Theory]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("a\nb", "\"a\nb\"")]
    [InlineData("a\rb", "\"a\rb\"")]
    [InlineData("a\r\nb", "\"a\r\nb\"")]
    public void Escape_ContainsRfc4180SpecialChar_IsQuoted(string input, string expected)
    {
        Assert.Equal(expected, CsvField.Escape(input));
    }

    [Fact]
    public void Escape_ContainsOnlyDoubleQuotes_DoublesEachQuote()
    {
        Assert.Equal("\"\"\"\"\"\"", CsvField.Escape("\"\""));
    }

    [Theory]
    [InlineData("=SUM(A1:A2)", "'=SUM(A1:A2)")]
    [InlineData("+1+1", "'+1+1")]
    [InlineData("-1-1", "'-1-1")]
    [InlineData("@SUM(A1:A2)", "'@SUM(A1:A2)")]
    public void Escape_LeadingInjectionChar_IsPrefixedWithApostrophe(string input, string expected)
    {
        // 先頭が = + - @ のいずれかの場合、Excel 等が数式として解釈しないよう ' を付与する
        // （CSV インジェクション対策。他に引用が必要な文字を含まないため引用符では囲まれない）。
        Assert.Equal(expected, CsvField.Escape(input));
    }

    [Fact]
    public void Escape_LeadingTab_IsPrefixedWithApostrophe()
    {
        // タブで始まる値も対策対象に含める（OWASP CSV Injection Cheat Sheet）。
        // タブ自体は RFC 4180 の引用対象文字ではないため、付与後も引用符では囲まれない。
        Assert.Equal("'\t=cmd", CsvField.Escape("\t=cmd"));
    }

    [Fact]
    public void Escape_LeadingCarriageReturn_IsPrefixedAndQuoted()
    {
        // CR で始まる値も対策対象に含める。CR は RFC 4180 の引用対象文字でもあるため、
        // ' 付与後の値全体が引用符で囲まれる。
        Assert.Equal("\"'\r=cmd\"", CsvField.Escape("\r=cmd"));
    }

    [Fact]
    public void Escape_LeadingInjectionCharAndComma_IsPrefixedAndQuoted()
    {
        // インジェクション対策の ' 付与が先、RFC 4180 の引用要否判定はその後——
        // 付与後の値にカンマが含まれるため全体を引用符で囲む。
        Assert.Equal("\"'=1,2\"", CsvField.Escape("=1,2"));
    }

    [Fact]
    public void Escape_ApostropheItselfIsNotAnInjectionTrigger_ButStaysUnquoted()
    {
        // アポストロフィ自体は RFC 4180 の引用対象でも先頭注入文字でもない。
        Assert.Equal("'already text", CsvField.Escape("'already text"));
    }

    [Fact]
    public void Escape_NonLeadingInjectionChar_IsNotPrefixed()
    {
        // 先頭以外に = + - @ を含んでいても対策の対象外（先頭セルの解釈のみが表計算ソフトのリスク）。
        Assert.Equal("value=1", CsvField.Escape("value=1"));
    }
}
