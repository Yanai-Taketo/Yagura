using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Yagura.Host.Observability.ActiveNotification.Email;

namespace Yagura.Host.Tests.Observability.ActiveNotification.Email;

/// <summary>
/// security.md §4.3 の表の「メール通知」列と <see cref="EmailNotificationAllowlist"/> の一致検証
/// （ADR-0017 決定 6・委任 6。Issue #387）。
/// </summary>
/// <remarks>
/// <para>
/// §4.3 の注記は<b>表を正本</b>と定める——「新しい ID を追加する PR は本列を必ず埋める」
/// 「実装（allowlist）との一致はテストで固定する」。本テストがその実効化であり、
/// <c>ForwarderKitVersionSyncTests</c> と同じ「文書 ⇔ 実装の機械同期」パターンでファイルを読む。
/// これにより、表だけ更新して allowlist を忘れる／allowlist だけ更新して表を忘れる、の
/// どちらの向きの乖離も CI で落ちる。
/// </para>
/// <para>
/// 重大度（対象（警告）/ 対象（エラー））も表が正本である（改訂 1——流量上限の対象外か否かを
/// 発火点の LogLevel という別の場所の暗黙値に依存させない）。表の書式が変わってパースできなく
/// なった場合は「0 行」または未知の列値として本テストが落ちる（パース不能なら落ちる、でよい）。
/// </para>
/// </remarks>
public sealed class EmailAllowlistSecurityDocSyncTests
{
    /// <summary>§4.3 のイベント ID 行（先頭セルが数値の 6 列行）にマッチする。</summary>
    private static readonly Regex EventIdRowPattern = new(@"^\|\s*(\d{4})\s*\|", RegexOptions.Compiled);

    [Fact]
    public void MailColumnOfTheEventIdTable_MatchesTheAllowlistAndItsSeverities()
    {
        var documented = ParseMailColumn();

        // パース不能（表の書式変更・移動）はここで落ちる。
        Assert.True(documented.Count >= 20,
            $"security.md からイベント ID 行が {documented.Count} 行しか読めず、表のパースが壊れている可能性が高い。");

        var documentedTargets = documented
            .Where(row => row.Severity is not null)
            .ToDictionary(row => row.Id, row => row.Severity!.Value);

        // 表で「対象」とされた ID 集合 = allowlist の登録集合（両方向の包含で乖離の向きを示す）。
        var registered = EmailNotificationAllowlist.RegisteredEventIds.ToHashSet();

        var onlyInDoc = documentedTargets.Keys.Where(id => !registered.Contains(id)).Order().ToList();
        Assert.True(onlyInDoc.Count == 0,
            $"§4.3 でメール通知対象とされているのに allowlist に無い ID: {string.Join(", ", onlyInDoc)}");

        var onlyInCode = registered.Where(id => !documentedTargets.ContainsKey(id)).Order().ToList();
        Assert.True(onlyInCode.Count == 0,
            $"allowlist に登録されているのに §4.3 でメール通知対象になっていない ID: {string.Join(", ", onlyInCode)}");

        // 重大度（= 流量上限の対象外か否か）も表が正本。
        foreach (var (id, severity) in documentedTargets)
        {
            Assert.True(
                EmailNotificationAllowlist.TryGetSeverity(new EventId(id), out var actual),
                $"ID {id} の重大度を allowlist から取得できない。");
            Assert.True(severity == actual,
                $"ID {id} の重大度が不一致: §4.3 = {severity} / allowlist = {actual}");
        }

        // 「対象外（構造的）」はメール通知チャネル自身の ID にのみ付く印（ループの定義レベル排除）。
        var structural = documented.Where(row => row.StructurallyExcluded).Select(row => row.Id).Order().ToList();
        Assert.Equal(
            new[] { EmailNotificationEventIds.SendFailed.Id, EmailNotificationEventIds.Throttled.Id },
            structural);
    }

    private sealed record DocumentedRow(int Id, EmailNotificationAllowlist.Severity? Severity, bool StructurallyExcluded);

    private static List<DocumentedRow> ParseMailColumn()
    {
        var path = Path.Combine(FindRepositoryRoot(), "docs", "design", "security.md");
        var rows = new List<DocumentedRow>();

        foreach (var line in File.ReadLines(path))
        {
            var match = EventIdRowPattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            // | ID | 区画 | レベル | メール通知 | 表示名 | 意味 | → split で先頭・末尾に空セルが付く 8 要素。
            var cells = line.Split('|');
            if (cells.Length != 8)
            {
                throw new InvalidOperationException(
                    $"§4.3 のイベント ID 行の列数が想定（6 列）と異なりパースできない: {line[..Math.Min(80, line.Length)]}");
            }

            var id = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var mailCell = cells[4].Trim();

            rows.Add(mailCell switch
            {
                "対象（警告）" => new DocumentedRow(id, EmailNotificationAllowlist.Severity.Warning, false),
                "対象（エラー）" => new DocumentedRow(id, EmailNotificationAllowlist.Severity.Error, false),
                "—" => new DocumentedRow(id, null, false),
                "**対象外（構造的）**" => new DocumentedRow(id, null, true),
                _ => throw new InvalidOperationException(
                    $"ID {id} の「メール通知」列の値「{mailCell}」を解釈できない（表の書式変更ならテストを追従させること）。"),
            });
        }

        return rows;
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
