using System.Text.Json;
using Yagura.Bench.Reporting;

namespace Yagura.Bench.Baseline;

/// <summary>
/// CI 回帰判定（Issue #62。architecture.md §5.2）の基準比較。
/// </summary>
/// <remarks>
/// <b>絶対値の合否は行わない</b>（§5.2「CI の回帰判定（毎 PR）は基準比とする」）。本実行の
/// スループット・保存件数を基準値ファイルの値と比で判定し、許容帯を超える劣化のみを不合格とする。
/// 基準値を上回る（改善）方向は常に合格。
/// </remarks>
public static class BaselineComparator
{
    /// <summary>基準値ファイル（JSON）を読み込む。</summary>
    public static BaselineFile LoadBaselineFile(string path)
    {
        var json = File.ReadAllText(path);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<BaselineFile>(json, options)
            ?? throw new InvalidOperationException($"基準値ファイル '{path}' の解析に失敗した（内容が null）。");
    }

    /// <summary>
    /// 1 シナリオ実行結果を基準値と比較する。
    /// </summary>
    /// <param name="report">ベンチ実行結果。</param>
    /// <param name="baselineFile">読み込み済みの基準値ファイル。</param>
    /// <returns>比較結果（合否・実測値・基準比）。</returns>
    public static BaselineComparisonResult Compare(ScenarioReport report, BaselineFile baselineFile)
    {
        if (!baselineFile.Scenarios.TryGetValue(report.ScenarioName, out var entry))
        {
            return new BaselineComparisonResult(
                ScenarioKey: report.ScenarioName,
                Passed: false,
                FailureReason: $"基準値ファイルにシナリオ '{report.ScenarioName}' のエントリが無い。",
                ActualThroughputPerSecond: 0,
                BaselineThroughputPerSecond: 0,
                ThroughputRatio: 0,
                ActualSavedCount: 0,
                BaselineSavedCount: 0,
                SavedCountRatio: 0,
                ToleranceRatio: 0,
                IsReconciled: report.Reconciliation.IsReconciled);
        }

        var actualThroughput = report.LoadResult.Elapsed.TotalSeconds > 0
            ? report.LoadResult.SucceededCount / report.LoadResult.Elapsed.TotalSeconds
            : 0;
        var actualSavedCount = report.Reconciliation.SavedCount;

        // 基準比 = 実測 / 基準値。1.0 = 基準どおり、1.0 未満 = 劣化、1.0 超 = 改善。
        // 許容帯は「劣化方向のみ」判定する（改善方向に上限は設けない。§5.2 の意図どおり）。
        var throughputRatio = entry.BaselineThroughputPerSecond > 0
            ? actualThroughput / entry.BaselineThroughputPerSecond
            : 1.0;
        var savedCountRatio = entry.BaselineSavedCount > 0
            ? actualSavedCount / (double)entry.BaselineSavedCount
            : 1.0;

        var minAcceptableRatio = 1.0 - entry.ToleranceRatio;

        var failures = new List<string>();
        if (throughputRatio < minAcceptableRatio)
        {
            failures.Add(
                $"スループットが基準比 {throughputRatio:P1}（許容下限 {minAcceptableRatio:P1}）を下回った " +
                $"(実測 {actualThroughput:F1} msg/sec, 基準 {entry.BaselineThroughputPerSecond:F1} msg/sec)。");
        }

        if (savedCountRatio < minAcceptableRatio)
        {
            failures.Add(
                $"保存件数が基準比 {savedCountRatio:P1}（許容下限 {minAcceptableRatio:P1}）を下回った " +
                $"(実測 {actualSavedCount}, 基準 {entry.BaselineSavedCount})。");
        }

        if (entry.RequireReconciled && !report.Reconciliation.IsReconciled)
        {
            failures.Add("突合が成立しなかった（送信数 = 保存件数 + 全カウンタの原則が破れた）。");
        }

        return new BaselineComparisonResult(
            ScenarioKey: report.ScenarioName,
            Passed: failures.Count == 0,
            FailureReason: failures.Count == 0 ? null : string.Join(" ", failures),
            ActualThroughputPerSecond: actualThroughput,
            BaselineThroughputPerSecond: entry.BaselineThroughputPerSecond,
            ThroughputRatio: throughputRatio,
            ActualSavedCount: actualSavedCount,
            BaselineSavedCount: entry.BaselineSavedCount,
            SavedCountRatio: savedCountRatio,
            ToleranceRatio: entry.ToleranceRatio,
            IsReconciled: report.Reconciliation.IsReconciled);
    }
}

/// <summary>基準比較の結果。</summary>
public sealed record BaselineComparisonResult(
    string ScenarioKey,
    bool Passed,
    string? FailureReason,
    double ActualThroughputPerSecond,
    double BaselineThroughputPerSecond,
    double ThroughputRatio,
    long ActualSavedCount,
    long BaselineSavedCount,
    double SavedCountRatio,
    double ToleranceRatio,
    bool IsReconciled)
{
    /// <summary>人間可読の 1 行〜数行サマリ。</summary>
    public string ToHumanReadableSummary()
    {
        var verdict = Passed ? "OK" : "NG（回帰）";
        var lines = new List<string>
        {
            $"=== 基準比較: {ScenarioKey} === 判定: {verdict}",
            $"スループット: 実測 {ActualThroughputPerSecond:F1} msg/sec / 基準 {BaselineThroughputPerSecond:F1} msg/sec (基準比 {ThroughputRatio:P1}、許容帯 -{ToleranceRatio:P0})",
            $"保存件数: 実測 {ActualSavedCount} / 基準 {BaselineSavedCount} (基準比 {SavedCountRatio:P1})",
            $"突合成立: {IsReconciled}",
        };

        if (FailureReason is not null)
        {
            lines.Add($"不合格理由: {FailureReason}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
