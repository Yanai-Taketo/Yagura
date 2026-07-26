using System.Text.RegularExpressions;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// 破壊的操作ボタン（<c>YaguraButtonRole.Destructive</c>）の確認ダイアログ 3 引数
/// （<c>ConfirmTitle</c>・<c>ConfirmSummary</c>・<c>ConfirmActionLabel</c>）の充足を、
/// <c>.razor</c> ソースの横断走査で機械検証する（Issue #457 の回帰防止）。
/// </summary>
/// <remarks>
/// <para>
/// <b>なぜソース走査なのか</b>: <c>YaguraButton.OnParametersSet</c> は 3 引数の欠落を
/// <see cref="InvalidOperationException"/> で拒否する（ui.md §3.1 を構造で強制する設計）。
/// これは正しい防御だが、**例外が飛ぶのは当該ボタンが実際に描画される瞬間**であり、
/// 確認パネルのように条件付きで現れる部品では初期描画テストに現れない。Issue #457 は
/// まさにこの死角で、フォワーダ MSI の削除確認パネル（第一段のクリック後にのみ描画）が
/// circuit ごと落ち、削除機能が使用不能だった。
/// </para>
/// <para>
/// <b>状態遷移テストではなく静的走査を選んだ理由</b>: 描画ハーネス
/// （<see cref="CommonComponentRenderHarness"/>）は <c>HtmlRenderer</c> による初期描画のみで
/// クリック後の状態遷移を再現できない（同ハーネスの remarks 参照——対話的検証が要るなら
/// bUnit の採用是非から入る話になる）。一方、本欠陥は「マークアップ上の属性欠落」であり、
/// 静的走査なら**将来追加される画面も含めて全数**を、依存を増やさずに押さえられる。
/// 条件付き描画の有無に関わらず検出できる点で、状態遷移テストより覆域が広い。
/// </para>
/// </remarks>
public sealed class DestructiveButtonContractTests
{
    /// <summary>
    /// <c>&lt;YaguraButton …&gt;</c> 要素 1 つ分を取り出す。属性は複数行にまたがるため
    /// <c>Singleline</c>（<c>.</c> が改行に一致）で開始タグの終わりまでを掴む。
    /// </summary>
    private static readonly Regex YaguraButtonElementPattern = new(
        @"<YaguraButton\b.*?>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    [Fact]
    public void AllDestructiveYaguraButtons_SpecifyAllThreeConfirmParameters()
    {
        var repoRoot = FindRepositoryRoot();
        var razorFiles = Directory.EnumerateFiles(
            Path.Combine(repoRoot, "src"), "*.razor", SearchOption.AllDirectories);

        var violations = new List<string>();

        foreach (var file in razorFiles)
        {
            var content = File.ReadAllText(file);

            foreach (Match element in YaguraButtonElementPattern.Matches(content))
            {
                var markup = element.Value;
                if (!markup.Contains("YaguraButtonRole.Destructive", StringComparison.Ordinal))
                {
                    continue;
                }

                var missing = new[] { "ConfirmTitle", "ConfirmSummary", "ConfirmActionLabel" }
                    .Where(parameterName => !markup.Contains(parameterName + "=", StringComparison.Ordinal))
                    .ToList();

                if (missing.Count > 0)
                {
                    var line = content[..element.Index].Count(c => c == '\n') + 1;
                    violations.Add(
                        $"{Path.GetRelativePath(repoRoot, file)}:{line} — 未指定: {string.Join(", ", missing)}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "破壊的操作のボタン（Role=Destructive）に確認ダイアログの引数が欠けている（描画時に " +
            "InvalidOperationException となり circuit が落ちる——Issue #457）:\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void Scan_FindsAtLeastOneDestructiveButton()
    {
        // 走査自体が空振り（正規表現の破綻・探索パスの誤り）していないことの自己検査——
        // 「違反 0 件」が「1 件も見ていない」を意味しないようにする。
        var repoRoot = FindRepositoryRoot();
        var destructiveCount = Directory
            .EnumerateFiles(Path.Combine(repoRoot, "src"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(file => YaguraButtonElementPattern.Matches(File.ReadAllText(file)).Cast<Match>())
            .Count(match => match.Value.Contains("YaguraButtonRole.Destructive", StringComparison.Ordinal));

        Assert.True(destructiveCount > 0, "Destructive ボタンを 1 つも検出できなかった（走査が機能していない）。");
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagura.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Yagura.sln を含むリポジトリルートが見つからない。");
    }
}
