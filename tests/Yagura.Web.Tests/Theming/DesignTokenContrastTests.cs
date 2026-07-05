using System.Linq;
using MudBlazor;
using MudBlazor.Utilities;
using Yagura.Web.Theming;

namespace Yagura.Web.Tests.Theming;

/// <summary>
/// 配色トークンのコントラスト検証（ui.md §8・UI-1。M8-2。Issue #69）。
/// </summary>
/// <remarks>
/// <para>
/// <b>基準</b>: ライト・ダーク両テーマで、テキストと背景のコントラスト比が WCAG 2 の
/// AA 基準値（通常テキスト 4.5:1 以上）を満たすこと（ui.md §8——状態色のテキスト利用時も
/// 同様）。算式は WCAG 2 の相対輝度（sRGB の線形化 + 0.2126/0.7152/0.0722 加重）と
/// コントラスト比 (L1+0.05)/(L2+0.05)。
/// </para>
/// <para>
/// <b>検証対象はテーマ定義の実体</b>（<see cref="YaguraTheme.Instance"/>）であり、
/// 文書上の値ではない——トークン値を変更する PR は ui.md §2.1 の表の更新を同じ PR に
/// 含める同期規約（ui.md §2.1）があるため、本テストが green なら文書側の値も基準を
/// 満たしている。
/// </para>
/// <para>
/// <b>初回検証（2026-07-06）の記録</b>: ライトの state-warning 初期値 #B26A00
/// （対 surface 4.24 / 対 background 3.95）と state-info 初期値 #0277BD（対 background
/// 4.47）が未達だったため、UI-1 の手続き（未達の色はトークン側を調整する——ui.md §8）に
/// 従い #A05F00 / #0270B2 へ調整した。検証記録は ui.md §8 に転記済み。
/// </para>
/// </remarks>
public sealed class DesignTokenContrastTests
{
    private const double MinimumRatio = 4.5;

    [Fact]
    public void LightPalette_AllTextPairs_MeetWcagAa()
    {
        var palette = YaguraTheme.Instance.PaletteLight;
        AssertAllPairsMeetMinimum(BuildTextPairs(palette), "ライト");
    }

    [Fact]
    public void DarkPalette_AllTextPairs_MeetWcagAa()
    {
        var palette = YaguraTheme.Instance.PaletteDark;
        AssertAllPairsMeetMinimum(BuildTextPairs(palette), "ダーク");
    }

    /// <summary>
    /// 検証対象のテキスト/背景ペア（トークンの用途——ui.md §2.1——から導いた実利用の組合せ）。
    /// </summary>
    private static IReadOnlyList<(string Name, MudColor Foreground, MudColor Background)> BuildTextPairs(
        Palette palette) =>
    [
        // 本文・補助テキスト（背景・面の両方の上に現れる）
        ("text-primary / background", palette.TextPrimary, palette.Background),
        ("text-primary / surface", palette.TextPrimary, palette.Surface),
        ("text-secondary / background", palette.TextSecondary, palette.Background),
        ("text-secondary / surface", palette.TextSecondary, palette.Surface),

        // primary はリンク・選択状態のテキスト色として使われる
        ("primary / background", palette.Primary, palette.Background),
        ("primary / surface", palette.Primary, palette.Surface),

        // 状態色のテキスト利用（ui.md §8「状態色のテキスト利用時も同様」——
        // アウトライン表示の通知・案内では状態色がテキスト色になる）
        ("state-ok / background", palette.Success, palette.Background),
        ("state-ok / surface", palette.Success, palette.Surface),
        ("state-warning / background", palette.Warning, palette.Background),
        ("state-warning / surface", palette.Warning, palette.Surface),
        ("state-error / background", palette.Error, palette.Background),
        ("state-error / surface", palette.Error, palette.Surface),
        ("state-info / background", palette.Info, palette.Background),
        ("state-info / surface", palette.Info, palette.Surface),

        // 塗り面上の文字色（状態帯・主ボタン・破壊的ボタン等の Filled 表示。ui.md §2.1.1 派生割当）
        ("contrast-text / primary 塗り", palette.PrimaryContrastText, palette.Primary),
        ("contrast-text / state-ok 塗り", palette.SuccessContrastText, palette.Success),
        ("contrast-text / state-warning 塗り", palette.WarningContrastText, palette.Warning),
        ("contrast-text / state-error 塗り", palette.ErrorContrastText, palette.Error),
        ("contrast-text / state-info 塗り", palette.InfoContrastText, palette.Info),

        // 共通骨格の面（アプリバー・ドロワー。surface / text-primary の流用——ui.md §2.1.1）
        ("appbar-text / appbar-background", palette.AppbarText, palette.AppbarBackground),
        ("drawer-text / drawer-background", palette.DrawerText, palette.DrawerBackground),
    ];

    private static void AssertAllPairsMeetMinimum(
        IReadOnlyList<(string Name, MudColor Foreground, MudColor Background)> pairs,
        string themeName)
    {
        var failures = pairs
            .Select(p => (p.Name, Ratio: ContrastRatio(p.Foreground, p.Background)))
            .Where(p => p.Ratio < MinimumRatio)
            .Select(p => $"{themeName}: {p.Name} = {p.Ratio:F2} (< {MinimumRatio})")
            .ToList();

        Assert.True(failures.Count == 0,
            "WCAG AA (4.5:1) 未達のトークンペアがある（ui.md §8。未達はトークン側を調整し、" +
            "ui.md §2.1 の表を同じ PR で更新する）:" + Environment.NewLine +
            string.Join(Environment.NewLine, failures));
    }

    /// <summary>WCAG 2 のコントラスト比 (L1 + 0.05) / (L2 + 0.05)。L1 は明るい方の相対輝度。</summary>
    private static double ContrastRatio(MudColor a, MudColor b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var (lighter, darker) = la >= lb ? (la, lb) : (lb, la);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>WCAG 2 の相対輝度（sRGB → 線形化 → 0.2126 R + 0.7152 G + 0.0722 B）。</summary>
    private static double RelativeLuminance(MudColor color)
    {
        return 0.2126 * Linearize(color.R) + 0.7152 * Linearize(color.G) + 0.0722 * Linearize(color.B);

        static double Linearize(byte channel)
        {
            var c = channel / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
    }
}
