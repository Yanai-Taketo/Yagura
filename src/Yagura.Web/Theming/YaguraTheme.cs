using MudBlazor;

namespace Yagura.Web.Theming;

/// <summary>
/// ui.md §2 のデザイントークンを MudBlazor テーマ機構へ割り当てた、実装側の唯一のテーマ定義
/// （M8-1。Issue #68。UI-8）。
/// </summary>
/// <remarks>
/// <para>
/// <b>トークン値は ui.md §2.1〜§2.3 を正とする</b>（同期規約: 本定義を変更する PR は
/// ui.md §2.1 の表の更新を同じ PR に含める。configuration.md §8 と同型）。
/// 割当表（どのトークンがどのパレット項目に対応するか）は ui.md §2.1.1 に記録済み。
/// </para>
/// <para>
/// 本クラスで割り当てていない MudBlazor パレット項目（Secondary・Tertiary 等）は
/// MudBlazor 既定値のまま残るが、<b>トークンに存在しない色のため画面・共通コンポーネントから
/// 使用してはならない</b>（ui.md §1 の直接指定禁止と同じ扱い。ui.md §2.1.1）。
/// </para>
/// </remarks>
public static class YaguraTheme
{
    /// <summary>
    /// フォントスタック（ui.md §2.2。OS 提供フォントのみ。Web フォントを同梱・参照しない）。
    /// 非 Windows クライアントでは末尾の総称ファミリ sans-serif へフォールバックする。
    /// MudBlazor 9.6.0 の同梱 CSS はフォント名をハードコードせず全てテーマ由来の CSS 変数
    /// （--mud-typography-*-family）を参照するため（ui.md §10 ①〜③の実体確認済み）、
    /// ここでの設定が全域に効く。
    /// </summary>
    private static readonly string[] FontStack = ["Yu Gothic UI", "Segoe UI", "Meiryo", "sans-serif"];

    /// <summary>唯一のテーマインスタンス（MudThemeProvider の Theme に渡す）。</summary>
    public static MudTheme Instance { get; } = new()
    {
        // 配色トークン（ui.md §2.1）。ライト・ダークは同格のテーマであり、
        // 全トークンが両テーマの値を持つ（ADR-0003 決定 4——機械変換で作らない）。
        PaletteLight = new PaletteLight
        {
            // 基本トークン
            Primary = "#1565C0",
            Background = "#F5F7FA",
            Surface = "#FFFFFF",
            TextPrimary = "#1A1A1A",
            TextSecondary = "#5F6B7A",
            Divider = "#D7DDE4",

            // divider は罫線系 3 項目へ同値割当（ui.md §2.1.1）
            LinesDefault = "#D7DDE4",
            TableLines = "#D7DDE4",

            // 状態色トークン（意味固定。ui.md §2.1）。
            // Warning / Info は UI-1 のコントラスト検証（M8-2。DesignTokenContrastTests）で
            // 初期値（#B26A00 / #0277BD）が WCAG AA 4.5:1 未達と判明したため調整済み
            // （ui.md §2.1 の表と同期。§8 に検証記録あり）。
            Success = "#2E7D32",
            Warning = "#A05F00",
            Error = "#C62828",
            Info = "#0270B2",

            // 派生割当（独立トークンを増やさない。ui.md §2.1.1）:
            // 共通骨格の面（アプリバー・ドロワー）は surface / text-primary を流用する。
            AppbarBackground = "#FFFFFF",
            AppbarText = "#1A1A1A",
            DrawerBackground = "#FFFFFF",
            DrawerText = "#1A1A1A",

            // 塗り面上の文字色（コントラスト検証は UI-1 の対象）。
            PrimaryContrastText = "#FFFFFF",
            SuccessContrastText = "#FFFFFF",
            WarningContrastText = "#FFFFFF",
            ErrorContrastText = "#FFFFFF",
            InfoContrastText = "#FFFFFF",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#90CAF9",
            Background = "#121212",
            Surface = "#1E1E1E",
            TextPrimary = "#E0E0E0",
            TextSecondary = "#A0A0A0",
            Divider = "#3A3A3A",

            LinesDefault = "#3A3A3A",
            TableLines = "#3A3A3A",

            Success = "#81C784",
            Warning = "#FFB74D",
            Error = "#E57373",
            Info = "#4FC3F7",

            AppbarBackground = "#1E1E1E",
            AppbarText = "#E0E0E0",
            DrawerBackground = "#1E1E1E",
            DrawerText = "#E0E0E0",

            // ダークの状態色は明色のため、塗り面上の文字は暗色
            // （text-primary のライト値を流用。ui.md §2.1.1）。
            PrimaryContrastText = "#1A1A1A",
            SuccessContrastText = "#1A1A1A",
            WarningContrastText = "#1A1A1A",
            ErrorContrastText = "#1A1A1A",
            InfoContrastText = "#1A1A1A",
        },

        // タイポグラフィ（ui.md §2.2）。サイズ段階: 12（注釈）/ 14（本文・既定）/
        // 16（強調・小見出し）/ 20（画面見出し）/ 28（数値の大表示）。行間 1.5 基準。
        // h1〜h3 は h5（画面見出し）と同値に固定し、トークン外サイズの混入経路を塞ぐ。
        // 等幅スタック（ログ本文用）は MudBlazor Typography に対応枠がないため、
        // ログ表示の共通コンポーネント実装内部で適用する（ui.md §2.2）。
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = FontStack, FontSize = ".875rem", LineHeight = "1.5" },
            Body1 = new Body1Typography { FontFamily = FontStack, FontSize = ".875rem", LineHeight = "1.5" },
            Body2 = new Body2Typography { FontFamily = FontStack, FontSize = ".875rem", LineHeight = "1.5" },
            Button = new ButtonTypography { FontFamily = FontStack, FontSize = ".875rem" },
            Caption = new CaptionTypography { FontFamily = FontStack, FontSize = ".75rem", LineHeight = "1.5" },
            Overline = new OverlineTypography { FontFamily = FontStack, FontSize = ".75rem" },
            Subtitle1 = new Subtitle1Typography { FontFamily = FontStack, FontSize = "1rem", LineHeight = "1.5" },
            Subtitle2 = new Subtitle2Typography { FontFamily = FontStack, FontSize = ".875rem", LineHeight = "1.5" },
            H1 = new H1Typography { FontFamily = FontStack, FontSize = "1.25rem", LineHeight = "1.5" },
            H2 = new H2Typography { FontFamily = FontStack, FontSize = "1.25rem", LineHeight = "1.5" },
            H3 = new H3Typography { FontFamily = FontStack, FontSize = "1.25rem", LineHeight = "1.5" },
            H4 = new H4Typography { FontFamily = FontStack, FontSize = "1.75rem", LineHeight = "1.5" },
            H5 = new H5Typography { FontFamily = FontStack, FontSize = "1.25rem", LineHeight = "1.5" },
            H6 = new H6Typography { FontFamily = FontStack, FontSize = "1rem", LineHeight = "1.5" },
        },

        // 形状（ui.md §2.3）。テーマの角丸は 1 値のみのため既定 4px（入力・ボタン）を割当て、
        // カード・ダイアログの 8px は共通コンポーネント実装内部で適用する（UI-9 の検証結果。
        // ui.md §2.3）。影は Shadows 配列を MudBlazor 既定（Material 26 段）のまま使い、
        // 利用を「なし=0 / カード=1 / ダイアログ=内蔵影」の 3 段に限定する（同節）。
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "4px",
        },
    };
}
