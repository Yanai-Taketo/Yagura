using System.Text.RegularExpressions;
using Yagura.Host.Observability.ActiveNotification.Email;

namespace Yagura.Host.Tests.Observability.ActiveNotification.Email;

/// <summary>
/// メール通知 allowlist と証跡の機械検証（ADR-0017 委任 6。Issue #387）。
/// </summary>
/// <remarks>
/// <para>
/// 委任 6 は「リストと実配線の一致を<b>ログ呼び出しレベルで</b>機械検証する（定数表との突合に
/// 留めない。EventId 未指定の通知系ログの検出を含める）」を要求する。本テストは 2 点を固定する:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>security.md §4.3 の「メール通知」列 ⇔ <see cref="EmailNotificationAllowlist"/> の一致</b>
/// （文書を正本とする宣言の実効化。<c>ForwarderKitVersionSyncTests</c> と同じ「文書 ⇔ 実装の
/// 機械同期」パターン）。
/// </description></item>
/// <item><description>
/// <b>通知系の発火点ファイルに、文書化されていない EventId 未指定の警告・エラーログが無いこと</b>
/// （<c>[permanent-failure]</c> は EventId 欠落のまま公開され、後追いの人手裁定〔#369〕で 1030 を
/// 採番した——機械検証があれば実装時に検出できていた欠陥クラス）。新しい EventId 未指定の通知
/// ログが増えると本テストが失敗し、実装者に「EventId を採番する（通知対象）」か「per-occurrence
/// の文脈ログとして下記の既知集合へ理由つきで追加する」かの判断を強制する。
/// </description></item>
/// </list>
/// </remarks>
public sealed class EmailNotificationAllowlistSyncTests
{
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

    // ------------------------------------------------------------------
    // (1) security.md §4.3 の「メール通知」列 ⇔ allowlist
    // ------------------------------------------------------------------

    [Fact]
    public void SecurityMd_EmailNotificationColumn_MatchesTheAllowlist()
    {
        var securityMd = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "docs", "design", "security.md"));

        // §4.3 のイベント ID 表: | ID | 区画 | レベル | メール通知 | 表示名 | 意味 |
        // メール通知列が「対象（警告）」「対象（エラー）」なら allowlist に該当重大度で含まれ、
        // 「—」なら含まれない、という対応を表全体で突合する。
        var rowPattern = new Regex(
            @"^\|\s*(?<id>\d{3,4})\s*\|[^|]*\|[^|]*\|(?<email>[^|]*)\|",
            RegexOptions.Multiline);

        var expectedWarning = new SortedSet<int>();
        var expectedError = new SortedSet<int>();
        var expectedExcluded = new SortedSet<int>();

        foreach (Match row in rowPattern.Matches(securityMd))
        {
            var id = int.Parse(row.Groups["id"].Value);
            var email = row.Groups["email"].Value.Trim();

            if (email.Contains("対象（警告）"))
            {
                expectedWarning.Add(id);
            }
            else if (email.Contains("対象（エラー）"))
            {
                expectedError.Add(id);
            }
            else if (email == "—")
            {
                expectedExcluded.Add(id);
            }
            // それ以外（表記ゆれ）は本テストで拾えるよう、下の網羅チェックで検出される。
        }

        Assert.True(expectedWarning.Count + expectedError.Count > 0, "§4.3 表からメール通知対象 ID を 1 件も抽出できなかった（表の書式が変わった可能性）。");

        // 表が「対象」とした ID は allowlist にあり、重大度も一致する。
        foreach (var id in expectedWarning)
        {
            Assert.True(
                EmailNotificationAllowlist.TryGetSeverity(new(id), out var sev) && sev == EmailNotificationAllowlist.Severity.Warning,
                $"§4.3 が 1{id} をメール通知対象（警告）としているが allowlist と一致しない。");
        }

        foreach (var id in expectedError)
        {
            Assert.True(
                EmailNotificationAllowlist.TryGetSeverity(new(id), out var sev) && sev == EmailNotificationAllowlist.Severity.Error,
                $"§4.3 が {id} をメール通知対象（エラー）としているが allowlist と一致しない。");
        }

        // 表が「—」とした ID は allowlist に無い。
        foreach (var id in expectedExcluded)
        {
            Assert.False(
                EmailNotificationAllowlist.TryGetSeverity(new(id), out _),
                $"§4.3 が {id} を対象外（—）としているが allowlist に含まれている。");
        }

        // allowlist の全 ID が §4.3 表に「対象」として現れる（表を正本とする宣言の裏取り）。
        var expectedAll = new SortedSet<int>(expectedWarning.Concat(expectedError));
        var allowlistIds = new SortedSet<int>(EmailNotificationAllowlist.RegisteredEventIds);
        Assert.True(
            allowlistIds.IsSubsetOf(expectedAll),
            $"allowlist の ID のうち §4.3 表に「対象」として現れないものがある: {string.Join(", ", allowlistIds.Except(expectedAll))}");
    }

    // ------------------------------------------------------------------
    // (2) EventId 未指定の通知系ログの検出
    // ------------------------------------------------------------------

    /// <summary>
    /// EventId を持たないことを<b>意図した</b>通知系カテゴリの警告・エラーログ（per-occurrence の
    /// 文脈ログ——通知に値する<b>集約</b>条件は別 ID が担う）。新規の EventId 未指定ログが増えたら
    /// 本集合との差分としてテストが失敗し、実装者に採番か本集合への追加（理由つき）を促す。
    /// </summary>
    /// <remarks>
    /// キーは「ファイルの相対パス」、値は「その行のメッセージ先頭の識別子（一意に指せる断片）」。
    /// いずれも PersistenceWriter の退避ログで、通知に値する集約条件（持続的な退避 = 1004、
    /// 書込失敗 = 1005、恒久障害 = 1030）は ActiveNotificationMonitor / 別経路が担うため、
    /// 個別発生ログには EventId を付けない（付けると per-occurrence でメールが飛ぶ）。
    /// </remarks>
    private static readonly (string RelativePath, string MessagePrefix)[] KnownEventIdLessNotificationLogs =
    [
        ("src/Yagura.Ingestion/Persistence/PersistenceWriter.cs", "[write-gate-timeout]"),
        ("src/Yagura.Ingestion/Persistence/PersistenceWriter.cs", "停止要求により DB 書き込みを打ち切り"),
        ("src/Yagura.Ingestion/Persistence/PersistenceWriter.cs", "バッチ書き込みがタイムアウト時間"),
    ];

    /// <summary>走査対象の通知系発火点（EventId で機械分類されるべきログを持つファイル群）。</summary>
    private static readonly string[] NotificationFiringPointDirectories =
    [
        "src/Yagura.Host/Observability/ActiveNotification",
    ];

    private static readonly string[] NotificationFiringPointFiles =
    [
        "src/Yagura.Ingestion/Persistence/PersistenceWriter.cs",
    ];

    [Fact]
    public void NotificationFiringPoints_HaveNoUndocumentedEventIdLessWarningsOrErrors()
    {
        var repoRoot = FindRepositoryRoot();

        var files = NotificationFiringPointDirectories
            .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(repoRoot, dir), "*.cs", SearchOption.AllDirectories))
            .Concat(NotificationFiringPointFiles.Select(f => Path.Combine(repoRoot, f)))
            .Distinct();

        // _logger.LogWarning( / LogError( / LogCritical( の直後（空白・改行を跨ぐ）が文字列リテラル
        // （= EventId を渡していない）呼び出しを拾う。
        var callPattern = new Regex(
            @"_logger[?]?\.Log(?:Warning|Error|Critical)\s*\(\s*(?:@?"")",
            RegexOptions.Singleline);

        var known = KnownEventIdLessNotificationLogs
            .Select(k => (Path.GetFullPath(Path.Combine(repoRoot, k.RelativePath)), k.MessagePrefix))
            .ToHashSet();

        var undocumented = new List<string>();

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match m in callPattern.Matches(text))
            {
                // 呼び出し直後の文字列リテラルの先頭数十文字を取り出して既知集合と突合する。
                var literalStart = text.IndexOf('"', m.Index);
                var snippet = text.Substring(literalStart + 1, Math.Min(40, text.Length - literalStart - 1));

                var matchedKnown = known.Any(k =>
                    string.Equals(Path.GetFullPath(file), k.Item1, StringComparison.Ordinal)
                    && snippet.StartsWith(k.MessagePrefix, StringComparison.Ordinal));

                if (!matchedKnown)
                {
                    var line = text[..m.Index].Count(c => c == '\n') + 1;
                    undocumented.Add($"{Path.GetRelativePath(repoRoot, file)}:{line}  \"{snippet.Split('\n')[0]}...\"");
                }
            }
        }

        Assert.True(
            undocumented.Count == 0,
            "EventId 未指定の通知系ログが見つかりました（[permanent-failure] と同じ欠陥クラス）。" +
            "通知対象なら EventId を採番し、per-occurrence の文脈ログなら KnownEventIdLessNotificationLogs へ" +
            "理由つきで追加してください:\n" + string.Join("\n", undocumented));
    }

    [Fact]
    public void KnownEventIdLessLogs_AreAllStillPresent_SoTheAllowSetDoesNotRot()
    {
        // 既知集合が実体から乖離（該当ログの削除・文言変更）したまま残らないようにする——
        // ForwarderKitVersionSyncTests と同じ「文書化した想定が実体とずれたら気づく」向き。
        var repoRoot = FindRepositoryRoot();

        foreach (var (relativePath, prefix) in KnownEventIdLessNotificationLogs)
        {
            var text = File.ReadAllText(Path.Combine(repoRoot, relativePath));
            Assert.Contains(prefix, text, StringComparison.Ordinal);
        }
    }
}
