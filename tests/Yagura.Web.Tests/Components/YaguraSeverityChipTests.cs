using Yagura.Web.Components.Common;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// 重大度チップ（ui.md §4「調査動線と重大度の可読化」。M8-3 追補）の表示検証:
/// 短形ラベル + 状態色の対応（0〜3 = error / 4 = warning / 5〜7 = 通常）と、
/// 範囲外・欠損値の非偽装表示。
/// </summary>
public sealed class YaguraSeverityChipTests
{
    private static Task<string> RenderAsync(int? severity) =>
        CommonComponentRenderHarness.RenderAsync<YaguraSeverityChip>(
            new Dictionary<string, object?> { [nameof(YaguraSeverityChip.Severity)] = severity });

    [Theory]
    [InlineData(0, "0: 緊急")]
    [InlineData(3, "3: エラー")]
    [InlineData(4, "4: 警告")]
    [InlineData(6, "6: 情報")]
    [InlineData(7, "7: デバッグ")]
    public async Task Render_KnownSeverity_ShowsShortLabelAsChip(int severity, string expectedLabel)
    {
        var html = await RenderAsync(severity);

        Assert.Contains(expectedLabel, html, StringComparison.Ordinal);
        Assert.Contains("mud-chip", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Render_ErrorBand_UsesErrorColor(int severity)
    {
        // 0〜3 = state-error(ui.md §4 の色対応)。MudChip の Color.Error は
        // CSS クラスに "error" を含む形で描画される(実描画で確認)。
        var html = await RenderAsync(severity);

        Assert.Contains("error", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Render_WarningSeverity_UsesWarningColor()
    {
        var html = await RenderAsync(4);

        Assert.Contains("warning", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Render_InfoSeverity_DoesNotUseStateColors()
    {
        // 5〜7 は通常表示——state-error/state-warning の誤用(全部に色が付くと
        // 本当に見るべき行が沈む)を防ぐ側の固定化。
        var html = await RenderAsync(6);

        Assert.DoesNotContain("mud-chip-color-error", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mud-chip-color-warning", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Render_OutOfRange_ShowsRawValueWithoutChip()
    {
        // 範囲外(解析失敗レコード等)は解釈を偽装せず生値のまま。
        var html = await RenderAsync(9);

        Assert.Contains(">9<", html, StringComparison.Ordinal);
        Assert.DoesNotContain("mud-chip", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_Null_ShowsPlaceholder()
    {
        var html = await RenderAsync(null);

        Assert.Contains("—", html, StringComparison.Ordinal);
        Assert.DoesNotContain("mud-chip", html, StringComparison.Ordinal);
    }
}
