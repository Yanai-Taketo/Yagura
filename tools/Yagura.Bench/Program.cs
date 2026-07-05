using Yagura.Bench.Reporting;
using Yagura.Bench.Scenarios;

namespace Yagura.Bench;

/// <summary>
/// ベンチハーネスの CLI エントリポイント（Issue #60。architecture.md §5.1）。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        ScenarioOptions options;
        try
        {
            options = ScenarioOptionsParser.Parse(args);
        }
        catch (BenchUsageException ex)
        {
            Console.WriteLine(ex.Message);
            return args.Length == 0 || args[0] is "-h" or "--help" ? 0 : 1;
        }

        Directory.CreateDirectory(options.OutputDirectory);

        Console.WriteLine($"シナリオ '{options.Scenario}' を開始する...");

        ScenarioReport report;
        try
        {
            report = await ScenarioRunner.RunAsync(options).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ベンチ実行中にエラーが発生した: {ex}");
            return 1;
        }

        var timestamp = report.StartedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss");
        var jsonPath = Path.Combine(options.OutputDirectory, $"{report.ScenarioName}-{timestamp}.json");
        var summaryPath = Path.Combine(options.OutputDirectory, $"{report.ScenarioName}-{timestamp}.summary.txt");

        ReportWriter.WriteJsonFile(report, jsonPath);
        var summary = ReportWriter.ToHumanReadableSummary(report);
        File.WriteAllText(summaryPath, summary);

        Console.WriteLine(summary);
        Console.WriteLine($"JSON: {jsonPath}");
        Console.WriteLine($"サマリ: {summaryPath}");

        // 突合不成立は「損失がどれかのカウンタに計上される」という architecture.md §4.1 の
        // 原則が破れたことを意味するため、終了コードで検知可能にする（CI 等での自動判定に使える。
        // ただし M7-1 時点では長時間本番ベンチの CI 組み込みは行わない——M7-3 のスコープ）。
        return report.Reconciliation.IsReconciled ? 0 : 2;
    }
}
