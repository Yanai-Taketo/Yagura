using Yagura.Web.Components.Common;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// ファシリティ整形（<see cref="UiText.FormatFacility"/>。ui.md §4。2026-07-06 オーナー指示）の
/// 検証: 標準名の併記・local0〜7 の枠名・対応表外の非偽装表示・null。
/// </summary>
public sealed class UiTextFacilityTests
{
    [Theory]
    [InlineData(0, "0: カーネル")]
    [InlineData(3, "3: デーモン")]
    [InlineData(4, "4: 認証")]
    [InlineData(16, "16: local0")]
    [InlineData(23, "23: local7")]
    public void FormatFacility_KnownNumber_AppendsLabel(int facility, string expected)
    {
        Assert.Equal(expected, UiText.FormatFacility(facility));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(99)]
    public void FormatFacility_OutOfRange_ReturnsBareNumber(int facility)
    {
        // 対応表に無い番号は名前を付けず番号のみ（解釈を偽装しない——番号本質）。
        Assert.Equal(facility.ToString(System.Globalization.CultureInfo.InvariantCulture), UiText.FormatFacility(facility));
    }

    [Fact]
    public void FormatFacility_Null_ReturnsPlaceholder()
    {
        Assert.Equal("—", UiText.FormatFacility(null));
    }
}
