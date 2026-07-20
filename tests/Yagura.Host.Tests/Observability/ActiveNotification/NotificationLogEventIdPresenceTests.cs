using System.Text.RegularExpressions;

namespace Yagura.Host.Tests.Observability.ActiveNotification;

/// <summary>
/// 通知系ソースの <c>LogWarning</c> / <c>LogError</c> 呼び出しが EventId を渡していることの
/// 機械検証（ADR-0017 委任 6。Issue #387）。
/// </summary>
/// <remarks>
/// <para>
/// <b>なぜソースを走査するのか</b>: メール通知（決定 7 の ILogger レール上の第 2 のシンク）は
/// EventId の allowlist で選別するため、<b>EventId なしの警告は構造的に捕捉できない</b>。
/// この欠陥クラスは実際に起きている——<c>[permanent-failure]</c> は EventId なし（= 0）で
/// 書き出されており、Issue #369 で 1030 を採番するまでメール通知が捕捉できなかった（#323 でも
/// 同種の欠落があった）。定数表との突合では「発火点が ID を渡し忘れた」を検出できないため、
/// <c>ForwarderKitVersionSyncTests</c> と同じ「リポジトリのファイルを読む」形で発火点を走査する。
/// </para>
/// <para>
/// <b>対象範囲</b>: 能動通知の発火点を持つソースに限定する——
/// <c>src/Yagura.Host/Observability/ActiveNotification/</c> 配下一式（周期監視・メールチャネル・
/// 途絶検知）と <c>src/Yagura.Ingestion/Persistence/PersistenceWriter.cs</c>（即時通知 1005・1030 の
/// 発火点。<c>[permanent-failure]</c> の前例が起きた場所）。Observability 全体へ広げないのは、
/// 監査・メタデータ領域の内部診断ログ（EventId を持たない設計が正当）まで巻き込むため。
/// </para>
/// <para>
/// <b>走査は正規表現による近似</b>で、誤検知しない程度の判定に留める（構文解析はしない）。
/// 第 1 引数が <c>new EventId(...)</c> または <c>～EventIds.X</c> 形式なら「EventId あり」、
/// 文字列リテラル・例外変数などで始まれば「EventId なし」と数える。
/// </para>
/// </remarks>
public sealed class NotificationLogEventIdPresenceTests
{
    /// <summary>
    /// EventId なし呼び出しの既知の許容数（ラチェット）。対象は<b>通知の発火点ではない</b>
    /// パイプライン運転の診断ログで、EventId を持たないことが正当と判断済みのもの。
    /// 新たに EventId なしの LogWarning / LogError が増えると数が合わず本テストが落ちる——
    /// その変更が通知対象なら security.md §4.3 で採番して allowlist の判断を行い、
    /// 診断ログならこの基準値を更新して「通知対象ではない」判断をコミットに残すこと。
    /// </summary>
    /// <remarks>
    /// PersistenceWriter.cs の 6 件の内訳: 書き込みゲート取得タイムアウト・停止要求による
    /// 打ち切り・バッチ書き込みタイムアウト・容量枯渇退避（[capacity-exhausted]）・
    /// 一時的失敗退避・包括の失敗退避——いずれもスプール退避の運転記録であり、通知としては
    /// 1004（退避継続）・1005（スプール書込失敗）・1030（恒久障害）が別途 EventId 付きで発火する。
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, int> WaivedCallCountsByFileName =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["PersistenceWriter.cs"] = 6,
        };

    private static readonly Regex LogCallPattern = new(
        @"\.Log(?:Warning|Error)\s*\(",
        RegexOptions.Compiled);

    /// <summary>第 1 引数が EventId 供給と分かる形（<c>new EventId(...)</c> / <c>～EventIds.X</c>）。</summary>
    private static readonly Regex EventIdFirstArgumentPattern = new(
        @"^(?:new\s+EventId\b|[\w.]*EventIds\s*\.\s*\w+)",
        RegexOptions.Compiled);

    [Fact]
    public void NotificationSources_PassAnEventIdToEveryWarningAndErrorLog()
    {
        var repoRoot = FindRepositoryRoot();
        var sources = Directory
            .GetFiles(
                Path.Combine(repoRoot, "src", "Yagura.Host", "Observability", "ActiveNotification"),
                "*.cs",
                SearchOption.AllDirectories)
            .Append(Path.Combine(repoRoot, "src", "Yagura.Ingestion", "Persistence", "PersistenceWriter.cs"))
            .ToList();

        var totalCallSites = 0;
        var offenders = new List<string>();
        var waivedByFileName = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var path in sources)
        {
            var text = File.ReadAllText(path);
            var fileName = Path.GetFileName(path);

            foreach (Match match in LogCallPattern.Matches(text))
            {
                totalCallSites++;

                var firstArgument = text[(match.Index + match.Length)..].TrimStart();
                if (EventIdFirstArgumentPattern.IsMatch(firstArgument))
                {
                    continue;
                }

                if (WaivedCallCountsByFileName.ContainsKey(fileName))
                {
                    waivedByFileName[fileName] = waivedByFileName.GetValueOrDefault(fileName) + 1;
                    continue;
                }

                var line = text[..match.Index].Count(c => c == '\n') + 1;
                var snippet = firstArgument[..Math.Min(60, firstArgument.Length)].ReplaceLineEndings(" ");
                offenders.Add($"{fileName}:{line}: 第 1 引数 = {snippet}");
            }
        }

        // 走査自体が空振りしていないことの防波堤——正規表現の腐りで「0 件検出 = 全緑」に
        // ならないよう、既知の規模（監視・メールチャネル・PersistenceWriter で数十件）を下回ったら
        // パース不能として落とす。
        Assert.True(totalCallSites >= 25,
            $"LogWarning/LogError の呼び出しが {totalCallSites} 件しか見つからず、走査が壊れている可能性が高い。");

        Assert.True(offenders.Count == 0,
            "EventId を渡していない LogWarning/LogError が通知系ソースに見つかりました" +
            "（メール通知の allowlist が構造的に捕捉できない——[permanent-failure]/#369 と同じ欠陥クラス）。" +
            "通知対象なら security.md §4.3 で採番し、診断ログなら本テストの許容表を更新して判断を残すこと:\n"
            + string.Join("\n", offenders));

        // 許容数の一致（ラチェット）: 増えたら上の offenders と同じ判断を要求し、減ったら
        // （EventId が採番されたら）基準値を下げさせる——許容表を腐らせない。
        foreach (var (fileName, expected) in WaivedCallCountsByFileName)
        {
            Assert.True(waivedByFileName.GetValueOrDefault(fileName) == expected,
                $"{fileName} の EventId なし呼び出し数が既知の {expected} 件から " +
                $"{waivedByFileName.GetValueOrDefault(fileName)} 件へ変わりました。判断を添えて許容表を更新してください。");
        }
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
