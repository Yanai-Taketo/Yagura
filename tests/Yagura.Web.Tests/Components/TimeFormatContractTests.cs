using System.Text.RegularExpressions;
using Yagura.TestSupport;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// 時刻表示の契約（ui.md §6）——「整形は <c>YaguraTimeFormatter</c> の 1 箇所に集約し、
/// 画面が独自の書式・変換を持つことを禁止する」——を <c>.razor</c> ソースの横断走査で
/// 機械検証する（Issue #462 の回帰防止）。
/// </summary>
/// <remarks>
/// <para>
/// <b>なぜ走査で強制するのか</b>: §6 は「禁止する」と明示しており、**構造で強制できる規約**である。
/// 実際に Issue #462 では 7 画面が契約の外にドリフトしていた。最も重かったのはフォワーダキット
/// 画面のアカウント点検で、最終ログインを **UTC の生表記**で出していた——JST の管理者には
/// 9 時間前の出来事に見え、「心当たりのないアカウントか」を判断する材料が判断を誤らせていた。
/// </para>
/// <para>
/// <b>ドリフトは一度に起きたのではない</b>: 画面が増えるたびに 1 つずつ独自整形が足され、
/// レビューでは個別には小さく見えて通ってきた。人手のレビューで捕まえ続ける前提を置かず、
/// 追加された瞬間に落ちるようにする（<c>DestructiveButtonContractTests</c> と同じ考え方）。
/// </para>
/// <para>
/// <b>覆域の限界</b>: 本走査が見るのは <c>.razor</c> のみである。ホスト側（<c>Yagura.Host</c>）の
/// ログ・イベント文言にも時刻整形はあるが、あれは画面ではないため §6 の直接の対象ではない
/// （Issue #462 も「表記の一貫性の観点で挙げておく」に留めている）。ここを広げると、
/// ログの書式まで UI の契約に縛られることになり、規約の射程が変わってしまう。
/// </para>
/// </remarks>
public sealed class TimeFormatContractTests
{
    /// <summary>
    /// 人が読む形に整える <c>ToString</c>——カスタム書式指定子（<c>yyyy</c>・<c>MM</c>・<c>dd</c>・
    /// <c>HH</c>・<c>mm</c>・<c>ss</c>）を含むもの。
    /// </summary>
    /// <remarks>
    /// <b>往復（ラウンドトリップ）書式は対象外</b>（<c>"o"</c>・<c>"O"</c>・<c>"s"</c>・<c>"u"</c>）:
    /// §6 が縛るのは**表示**であり、機械可読の直列化は別物である。実際に
    /// <c>LogSearch.razor</c> は検索条件の期間を <c>"O"</c> で URL へ載せている——サーバの
    /// タイムゾーンに依存せず、共有された URL がどの端末で開かれても同じ瞬間を指すための
    /// 意図的な選択で、これを禁止すると URL の可搬性を壊す。<c>YaguraTimestamp</c> が
    /// <c>&lt;time datetime&gt;</c> 属性に出す ISO 8601 も同じ理由で対象外になる。
    /// </remarks>
    private static readonly Regex DateTimeToStringPattern = new(
        @"ToString\(\s*""[^""]*(?:yyyy|MM|dd|HH|mm|ss)[^""]*""",
        RegexOptions.Compiled);

    /// <summary>
    /// タイムゾーン変換の直接呼び出し（<c>ToLocalTime</c> / <c>ToUniversalTime</c>）。
    /// 整形と同じ理由で画面には置かない——「変換は画面、書式は共通」に割れると、
    /// どちらが正なのか読めなくなる。
    /// </summary>
    private static readonly Regex TimeZoneConversionPattern = new(
        @"\.(ToLocalTime|ToUniversalTime)\(\)",
        RegexOptions.Compiled);

    [Fact]
    public void NoRazorScreen_FormatsDateTimeOnItsOwn()
    {
        var repoRoot = RepositoryPaths.FindRoot();
        var violations = new List<string>();

        foreach (var file in EnumerateRazorFiles(repoRoot))
        {
            var content = File.ReadAllText(file);

            foreach (Match match in DateTimeToStringPattern.Matches(content))
            {
                violations.Add(Describe(repoRoot, file, content, match.Index, $"独自の時刻書式 {match.Value}"));
            }

            foreach (Match match in TimeZoneConversionPattern.Matches(content))
            {
                violations.Add(Describe(repoRoot, file, content, match.Index, $"画面でのタイムゾーン変換 {match.Value}"));
            }
        }

        Assert.True(
            violations.Count == 0,
            "画面が独自の時刻書式・変換を持っている（ui.md §6 が禁止。Issue #462）。" +
            "YaguraTimestamp コンポーネントまたは YaguraTimeFormatter 経由に統一すること:\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void Scan_ActuallyInspectsRazorFiles()
    {
        // 走査が空振り（探索パスの誤り）していないことの自己検査——「違反 0 件」が
        // 「1 件も見ていない」を意味しないようにする。
        var repoRoot = RepositoryPaths.FindRoot();
        Assert.True(
            EnumerateRazorFiles(repoRoot).Count() > 10,
            ".razor を十分に検出できなかった（走査が機能していない）。");
    }

    [Fact]
    public void Pattern_DetectsTheOriginalViolations()
    {
        // 検査そのものが効くことの確認: Issue #462 が挙げた実際の逸脱 3 形を弾けること。
        // これが無いと、正規表現が壊れて何も検出しなくなっても緑のままになる。
        Assert.Matches(DateTimeToStringPattern, """at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)""");
        Assert.Matches(DateTimeToStringPattern, """value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")""");
        Assert.Matches(DateTimeToStringPattern, """candidate.NotBefore.ToLocalTime().ToString("yyyy-MM-dd")""");
        Assert.Matches(TimeZoneConversionPattern, """value.Value.ToLocalTime()""");

        // 逆に、時刻と無関係な ToString を巻き込まないこと（誤検出で規約が嫌われないように）。
        Assert.DoesNotMatch(DateTimeToStringPattern, """count.ToString("N0", CultureInfo.InvariantCulture)""");
        Assert.DoesNotMatch(DateTimeToStringPattern, """ratio.ToString("P1")""");

        // 機械可読の直列化も巻き込まないこと（LogSearch の URL 期間・YaguraTimestamp の
        // datetime 属性。これを弾くと URL の可搬性を壊すため、意図的に対象外にしている）。
        Assert.DoesNotMatch(DateTimeToStringPattern, """value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)""");
        Assert.DoesNotMatch(DateTimeToStringPattern, """Value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)""");
    }

    private static IEnumerable<string> EnumerateRazorFiles(string repoRoot) =>
        Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "*.razor", SearchOption.AllDirectories);

    private static string Describe(string repoRoot, string file, string content, int index, string what)
    {
        var line = content[..index].Count(c => c == '\n') + 1;
        return $"{Path.GetRelativePath(repoRoot, file)}:{line} — {what}";
    }
}
