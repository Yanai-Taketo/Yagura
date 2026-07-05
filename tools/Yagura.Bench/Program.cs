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
        // 隠しモード: Ctrl+C 送出ヘルパー(BenchHostProcess.StopGracefullyAsync が self-invoke する)。
        // コンソールの切り離し(FreeConsole)を伴う Ctrl+C 送出をベンチ本体プロセスで行うと、
        // 実コンソールからの対話実行時に本体のコンソールハンドルが壊れ、以後の Console 出力が
        // 未処理例外でクラッシュする(exit 0xE0434352。オーナー実機 + ローカル再現で確認。
        // AttachConsole での再接続 + ストリーム再バインドでも解消しなかった)。使い捨ての
        // ヘルパープロセスに隔離すれば、本体のコンソールには一切触れずに送出できる。
        if (args.Length == 2 && args[0] == "__send-ctrlc" && int.TryParse(args[1], out var targetPid))
        {
            return HostProcess.ConsoleCtrlSender.TrySendCtrlC(targetPid) ? 0 : 1;
        }

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

        // 本体（子プロセス）の全出力も残す——飽和形態の分析（M-7/M-13: スプール退避の契機が
        // Q2 溢れかタイムアウトかは本体の警告ログにのみ現れる）と障害調査の一次情報。
        if (report.HostStdout.Count > 0)
        {
            var hostLogPath = Path.Combine(options.OutputDirectory, $"{report.ScenarioName}-{timestamp}.hostlog.txt");
            File.WriteAllLines(hostLogPath, report.HostStdout);
            Console.WriteLine($"本体ログ: {hostLogPath}");
        }

        Console.WriteLine(summary);
        Console.WriteLine($"JSON: {jsonPath}");
        Console.WriteLine($"サマリ: {summaryPath}");

        // 突合不成立は「損失がどれかのカウンタに計上される」という architecture.md §4.1 の
        // 原則が破れたことを意味するため、終了コードで検知可能にする（CI 等での自動判定に使える。
        // ただし M7-1 時点では長時間本番ベンチの CI 組み込みは行わない——M7-3 のスコープ）。
        return report.Reconciliation.IsReconciled ? 0 : 2;
    }
}
