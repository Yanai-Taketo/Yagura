using Yagura.Bench.Baseline;
using Yagura.Bench.Reporting;
using Yagura.Bench.Scenarios;
using Yagura.Bench.StorageBench;

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

        // DB-9/DB-10 のストレージベンチマーク（database.md §8）は受信パイプラインの負荷測定
        // （送出・突合の概念を持つ ScenarioReport）とは性質が異なるため、専用の実行・出力経路を持つ
        // （StorageBenchReport。ReportWriter/StorageBenchReport の doc コメント参照）。
        if (options.Scenario is BenchScenario.QueryLatency or BenchScenario.SchemaMigrationDdl)
        {
            return await RunStorageBenchAsync(options).ConfigureAwait(false);
        }

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

        // --compare-baseline 指定時は基準比較を行う（Issue #62。architecture.md §5.2「CI の回帰判定
        // は基準比とする」）。絶対値の合否（突合成立）はここでも従来どおり評価するが、基準比較が
        // 追加の不合格条件になる——両方が独立した終了コードを持つ（2 = 突合不成立、3 = 基準比較 NG）。
        if (options.CompareBaselinePath is not null)
        {
            BaselineFile baselineFile;
            try
            {
                baselineFile = BaselineComparator.LoadBaselineFile(options.CompareBaselinePath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"基準値ファイルの読み込みに失敗した: {ex.Message}");
                return 1;
            }

            var comparison = BaselineComparator.Compare(report, baselineFile);
            Console.WriteLine();
            Console.WriteLine(comparison.ToHumanReadableSummary());

            if (!comparison.Passed)
            {
                return 3;
            }
        }

        // 突合不成立は「損失がどれかのカウンタに計上される」という architecture.md §4.1 の
        // 原則が破れたことを意味するため、終了コードで検知可能にする（CI 等での自動判定に使える。
        // ただし M7-1 時点では長時間本番ベンチの CI 組み込みは行わない——M7-3 のスコープ）。
        return report.Reconciliation.IsReconciled ? 0 : 2;
    }

    /// <summary>
    /// DB-9（<see cref="BenchScenario.QueryLatency"/>）・DB-10（<see cref="BenchScenario.SchemaMigrationDdl"/>）の
    /// 実行経路。既定の行数規模は QueryLatency/SchemaMigrationDdl とも 10 万・100 万
    /// （<c>--rows</c> 未指定時）——1000 万行は実行時間が長いため明示指定を要求する。
    /// </summary>
    private static async Task<int> RunStorageBenchAsync(ScenarioOptions options)
    {
        var runId = Guid.NewGuid().ToString("N")[..12];
        var dataRoot = options.DataRoot ?? Path.Combine(Path.GetTempPath(), $"yagura-bench-{runId}");
        Directory.CreateDirectory(dataRoot);

        var rowCounts = options.RowCounts ?? [100_000, 1_000_000];
        var startedAt = DateTimeOffset.UtcNow;
        var wallClock = System.Diagnostics.Stopwatch.StartNew();
        var notes = new List<string>();

        void Log(string message) => Console.WriteLine(message);

        try
        {
            IReadOnlyList<QueryLatencyBenchmark.RowCountResult>? queryResults = null;
            IReadOnlyList<SchemaMigrationDdlBenchmark.RowCountResult>? ddlResults = null;

            if (options.Scenario == BenchScenario.QueryLatency)
            {
                queryResults = await QueryLatencyBenchmark.RunAsync(rowCounts, dataRoot, Log).ConfigureAwait(false);
                notes.Add(
                    $"結果上限 {10_000} 件・タイムアウト {TimeSpan.FromSeconds(30)}（architecture.md M-10 の仮値）を前提に計測した。");
            }
            else
            {
                var usingSqlServer = !string.IsNullOrWhiteSpace(options.SqlServerConnectionString);
                var sqlServerTemplate = usingSqlServer
                    ? options.SqlServerConnectionString
                    : null;
                ddlResults = await SchemaMigrationDdlBenchmark.RunAsync(rowCounts, dataRoot, sqlServerTemplate, Log).ConfigureAwait(false);
                notes.Add(usingSqlServer
                    ? "SQL Server（--sqlserver 指定先）を対象に、v1→v2 移行（列 COLLATE 明示 + 索引再構築）の DDL 実行時間を計測した。"
                    : "SQLite を対象に、v1→v2 移行（索引のみ。COLLATE 相当の DDL 変更は不要）の DDL 実行時間を計測した。");
            }

            wallClock.Stop();

            var report = new StorageBenchReport(
                options.Scenario.ToString(),
                runId,
                startedAt,
                Reporting.EnvironmentInfo.Collect(dataRoot),
                wallClock.Elapsed,
                queryResults,
                ddlResults,
                notes);

            var timestamp = report.StartedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss");
            var jsonPath = Path.Combine(options.OutputDirectory, $"{report.ScenarioName}-{timestamp}.json");
            var summaryPath = Path.Combine(options.OutputDirectory, $"{report.ScenarioName}-{timestamp}.summary.txt");

            ReportWriter.WriteJsonFile(report, jsonPath);
            var summary = ReportWriter.ToHumanReadableSummary(report);
            File.WriteAllText(summaryPath, summary);

            Console.WriteLine(summary);
            Console.WriteLine($"JSON: {jsonPath}");
            Console.WriteLine($"サマリ: {summaryPath}");

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ストレージベンチ実行中にエラーが発生した: {ex}");
            return 1;
        }
        finally
        {
            if (!options.KeepDataRoot && Directory.Exists(dataRoot))
            {
                try
                {
                    Directory.Delete(dataRoot, recursive: true);
                }
                catch (IOException)
                {
                    // ベストエフォート（ScenarioRunner.RunAsync と同じ判断）。
                }
            }
        }
    }
}
