using Yagura.Bench.Baseline;
using Yagura.Bench.LoadGeneration;
using Yagura.Bench.Reporting;
using Yagura.Bench.Verification;
using Yagura.Ingestion.Diagnostics;

namespace Yagura.Bench.Tests;

/// <summary>
/// 基準比較ロジックの単体テスト（Issue #62。architecture.md §5.2「CI の回帰判定は基準比とする」）。
/// </summary>
/// <remarks>
/// <see cref="BenchSmokeTests"/> と異なり、実プロセス（Yagura.Host.dll）を起動せず
/// <see cref="ScenarioReport"/> を直接組み立てて比較ロジックのみを検証する——高速に回せる
/// ロジックテストと、実プロセスを介する統合的な煙テストを分離する狙い。
/// </remarks>
public sealed class BaselineComparatorTests
{
    private static ScenarioReport BuildReport(
        string scenarioName,
        long sentCount,
        long savedCount,
        TimeSpan elapsed,
        bool isReconciled = true) =>
        new(
            ScenarioName: scenarioName,
            RunId: "test-run",
            StartedAt: DateTimeOffset.UtcNow,
            Environment: new EnvironmentInfo(
                OsDescription: "test-os",
                OsArchitecture: "x64",
                ProcessorName: "test-cpu",
                LogicalProcessorCount: 4,
                TotalPhysicalMemoryBytes: 0,
                DataRootDriveDescription: "test-drive",
                DotnetRuntimeVersion: "test-runtime",
                MachineName: "test-machine"),
            LoadGeneratorOptions: new LoadGeneratorOptions(
                LoadTransport.Udp, LoadPattern.Sustained, "127.0.0.1", 0, "test-run"),
            LoadResult: new LoadGeneratorResult("test-run", sentCount, sentCount, 0, elapsed, 1),
            Reconciliation: new ReconciliationResult(
                SentCount: sentCount,
                SavedCount: savedCount,
                AccountedLossCount: 0,
                SpoolEvacuatedCount: 0,
                OsUdpDatagramsDiscardedDelta: 0,
                DerivedOsBufferLossCount: 0,
                Difference: isReconciled ? 0 : sentCount - savedCount,
                IsReconciled: isReconciled,
                Counters: new IngestionCounterSnapshot(0, 0, 0, 0, 0, 0, 0)),
            ElapsedWallClock: elapsed,
            AdditionalMetrics: new Dictionary<string, string>(),
            Notes: [],
            HostStdout: []);

    private static BaselineFile BuildBaselineFile(
        string scenarioKey, double throughput, long savedCount, double tolerance, bool enforceRatio = true) =>
        new()
        {
            Meta = new BaselineMeta("provisional-test", "2026-07-06", "test fixture", "test fixture"),
            Scenarios = new Dictionary<string, BaselineEntry>
            {
                [scenarioKey] = new(scenarioKey, throughput, savedCount, tolerance, RequireReconciled: true, EnforceRatio: enforceRatio),
            },
        };

    [Fact]
    public void Compare_ResultMatchesBaseline_Passes()
    {
        var report = BuildReport("SustainedZeroDrop", sentCount: 5000, savedCount: 5000, elapsed: TimeSpan.FromSeconds(1));
        var baseline = BuildBaselineFile("SustainedZeroDrop", throughput: 5000, savedCount: 5000, tolerance: 0.5);

        var result = BaselineComparator.Compare(report, baseline);

        Assert.True(result.Passed);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void Compare_ResultExceedsBaseline_Passes()
    {
        // 改善方向（基準を上回る）は常に合格——§5.2 は「劣化」のみを不合格条件とする。
        var report = BuildReport("SustainedZeroDrop", sentCount: 8000, savedCount: 8000, elapsed: TimeSpan.FromSeconds(1));
        var baseline = BuildBaselineFile("SustainedZeroDrop", throughput: 5000, savedCount: 5000, tolerance: 0.1);

        var result = BaselineComparator.Compare(report, baseline);

        Assert.True(result.Passed);
    }

    [Fact]
    public void Compare_ThroughputWithinTolerance_Passes()
    {
        // 基準比 60%（許容帯 50% = 許容下限 50%）は許容範囲内。
        var report = BuildReport("SustainedZeroDrop", sentCount: 3000, savedCount: 3000, elapsed: TimeSpan.FromSeconds(1));
        var baseline = BuildBaselineFile("SustainedZeroDrop", throughput: 5000, savedCount: 3000, tolerance: 0.5);

        var result = BaselineComparator.Compare(report, baseline);

        Assert.True(result.Passed);
    }

    [Fact]
    public void Compare_ThroughputBelowTolerance_Fails()
    {
        // 基準比 40%（許容下限 50%）は不合格。
        var report = BuildReport("SustainedZeroDrop", sentCount: 2000, savedCount: 2000, elapsed: TimeSpan.FromSeconds(1));
        var baseline = BuildBaselineFile("SustainedZeroDrop", throughput: 5000, savedCount: 2000, tolerance: 0.5);

        var result = BaselineComparator.Compare(report, baseline);

        Assert.False(result.Passed);
        Assert.Contains("スループット", result.FailureReason);
    }

    [Fact]
    public void Compare_SavedCountBelowTolerance_Fails()
    {
        var report = BuildReport("SustainedZeroDrop", sentCount: 5000, savedCount: 1000, elapsed: TimeSpan.FromSeconds(1));
        var baseline = BuildBaselineFile("SustainedZeroDrop", throughput: 5000, savedCount: 5000, tolerance: 0.5);

        var result = BaselineComparator.Compare(report, baseline);

        Assert.False(result.Passed);
        Assert.Contains("保存件数", result.FailureReason);
    }

    [Fact]
    public void Compare_NotReconciled_FailsEvenIfThroughputOk()
    {
        var report = BuildReport(
            "SustainedZeroDrop", sentCount: 5000, savedCount: 5000, elapsed: TimeSpan.FromSeconds(1), isReconciled: false);
        var baseline = BuildBaselineFile("SustainedZeroDrop", throughput: 5000, savedCount: 5000, tolerance: 0.5);

        var result = BaselineComparator.Compare(report, baseline);

        Assert.False(result.Passed);
        Assert.Contains("突合", result.FailureReason);
    }

    [Fact]
    public void Compare_RatioBelowToleranceButInformational_PassesWithNote()
    {
        // enforceRatio=false のシナリオ（M-5 初回実測で双峰性が確認された SustainedZeroDrop 型）は、
        // 比の劣化を不合格にせず情報として残す。
        var report = BuildReport("SustainedZeroDrop", sentCount: 5000, savedCount: 1000, elapsed: TimeSpan.FromSeconds(1));
        var baseline = BuildBaselineFile("SustainedZeroDrop", throughput: 5000, savedCount: 5000, tolerance: 0.5, enforceRatio: false);

        var result = BaselineComparator.Compare(report, baseline);

        Assert.True(result.Passed);
        Assert.Null(result.FailureReason);
        Assert.NotNull(result.InformationalNotes);
        Assert.Contains("保存件数", result.InformationalNotes);
    }

    [Fact]
    public void Compare_InformationalRatioDoesNotWaiveReconciliation_Fails()
    {
        // enforceRatio=false でも突合成立の不変条件は降格されない（「損失は必ずどれかのカウンタに
        // 計上される」原則は環境の揺らぎと無関係に成り立つべき）。
        var report = BuildReport(
            "SustainedZeroDrop", sentCount: 5000, savedCount: 5000, elapsed: TimeSpan.FromSeconds(1), isReconciled: false);
        var baseline = BuildBaselineFile("SustainedZeroDrop", throughput: 5000, savedCount: 5000, tolerance: 0.5, enforceRatio: false);

        var result = BaselineComparator.Compare(report, baseline);

        Assert.False(result.Passed);
        Assert.Contains("突合", result.FailureReason);
    }

    [Fact]
    public void Compare_ScenarioMissingFromBaseline_Fails()
    {
        var report = BuildReport("Throughput", sentCount: 5000, savedCount: 5000, elapsed: TimeSpan.FromSeconds(1));
        var baseline = BuildBaselineFile("SustainedZeroDrop", throughput: 5000, savedCount: 5000, tolerance: 0.5);

        var result = BaselineComparator.Compare(report, baseline);

        Assert.False(result.Passed);
        Assert.Contains("エントリが無い", result.FailureReason);
    }

    [Fact]
    public void LoadBaselineFile_ParsesRepositoryCiBaseline()
    {
        // リポジトリの実ファイル（tools/Yagura.Bench/baselines/ci-baseline.json）が
        // 実際にパース可能であることを確認する（CI がこのファイルを読めなくなる回帰の防止）。
        var repoRoot = FindRepositoryRoot();
        var baselinePath = Path.Combine(repoRoot, "tools", "Yagura.Bench", "baselines", "ci-baseline.json");

        Assert.True(File.Exists(baselinePath), $"基準値ファイルが見つからない: {baselinePath}");

        var baselineFile = BaselineComparator.LoadBaselineFile(baselinePath);

        Assert.NotEmpty(baselineFile.Scenarios);
        Assert.Contains("SustainedZeroDrop", baselineFile.Scenarios.Keys);
        Assert.Contains("ProviderWriteCeiling", baselineFile.Scenarios.Keys);

        // 記録元（暫定の間はローカル実測の出所、CI 実測確定後は CI run URL）が空になる回帰を防ぐ
        // ——基準値の出所を追跡できることが更新手続き（conventions.md）の前提であるため。
        Assert.False(
            string.IsNullOrWhiteSpace(baselineFile.Meta.RecordedFrom),
            "_meta.recordedFrom が空。基準値の記録元（ローカル実測の出所または CI run URL）を必ず記載する。");

        // M-5 初回実測（2026-07-06）で確定した判定モードの固定化——変更する場合は実測データを
        // 添えた基準値更新 PR で本アサーションも意識的に更新する（conventions.md の手続き参照）:
        // blocking 判定は ProviderWriteCeiling が担い、SustainedZeroDrop の比判定は双峰性の実測に
        // より情報表示のみ（突合成立の不変条件は両シナリオとも維持）。
        Assert.True(baselineFile.Scenarios["ProviderWriteCeiling"].EnforceRatio, "ProviderWriteCeiling は blocking 判定を担う（M-5 確定内容）。");
        Assert.False(baselineFile.Scenarios["SustainedZeroDrop"].EnforceRatio, "SustainedZeroDrop の比判定は情報表示（M-5 実測の双峰性が根拠）。");
        Assert.True(baselineFile.Scenarios["ProviderWriteCeiling"].RequireReconciled);
        Assert.True(baselineFile.Scenarios["SustainedZeroDrop"].RequireReconciled);
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
