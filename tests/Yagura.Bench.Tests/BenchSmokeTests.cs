using Yagura.Bench.LoadGeneration;
using Yagura.Bench.Scenarios;

namespace Yagura.Bench.Tests;

/// <summary>
/// ベンチハーネス自身の自己検証（Issue #60「短時間の煙テスト」）。実バイナリ（Yagura.Host.dll）を
/// 子プロセスとして起動し、小さい流量（数百通）で「送信数 = 保存件数 + 全カウンタの合計」の
/// 突合が成立することを CI で回せる速さで確認する。
/// </summary>
/// <remarks>
/// 本番ベンチの長時間実行はここに含めない（Issue #60「ベンチ本番の長時間実行は CI に含めない」）。
/// <see cref="ScenarioRunner.RunAsync"/> を直接呼び出す（CLI プロセスを別途起動する二重起動は
/// 行わない——検証対象は「ハーネスの内部ロジックが正しく突合すること」であり、CLI 引数解析
/// （<see cref="ScenarioOptionsParser"/>）は別途ユニットテストの対象になり得るが、本テストの
/// 主眼ではない）。
/// </remarks>
public sealed class BenchSmokeTests
{
    [Fact]
    public async Task SustainedZeroDropScenario_SmallLoad_ReconciliationSucceeds()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"yagura-bench-smoke-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        try
        {
            var options = new ScenarioOptions(
                Scenario: BenchScenario.SustainedZeroDrop,
                Transport: LoadTransport.Udp,
                RatePerSecond: 200,
                DurationSeconds: 2,
                BurstCount: 0,
                SenderSocketCount: 2,
                PaddingBytes: 0,
                DataRoot: null,
                OutputDirectory: outputDirectory,
                SqlServerConnectionString: null,
                SpoolQuotaBytes: 4L * 1024 * 1024,
                KeepDataRoot: false);

            var report = await ScenarioRunner.RunAsync(options);

            // architecture.md §4.1「損失は必ずどれかのカウンタに計上される」の検証がそのまま
            // 合否条件になる（Issue #60・§5.1）。小さい流量（数百通）ではアプリ内カウンタ
            // （破棄・退避系）は発生しないはずであり、これは完全一致を要求する。
            Assert.Equal(0, report.Reconciliation.AccountedLossCount);

            // OS レベル UDP 破棄差分（§4.2）は「ベンチ実行ホストが UDP 受信を専有していること」を
            // 前提とする突合入力であり（architecture.md §4.2「この突合式はベンチ実行ホストが
            // UDP 受信を専有していること…を前提とする」）、CI・開発機の共有環境ではこの前提が
            // 保証されない（同居する他プロセスの UDP トラフィックが本カウンタに混入し得る）。
            // そのため本 smoke テストは「送信数 = 保存件数 + アプリ内カウンタ」（OS 統計差分を
            // 除いた式）の完全一致を主張し、OS 統計差分そのものは参考情報として記録するに留める
            // （突合式全体の 0 判定は本番ベンチ実行時の運用で確認する。§4.2 の限界の反映）。
            Assert.Equal(
                report.Reconciliation.SentCount,
                report.Reconciliation.SavedCount + report.Reconciliation.AccountedLossCount);

            Assert.True(report.LoadResult.SentCount > 0, "送信数が 0 だった（負荷生成器が送出できていない）。");
            Assert.Equal(report.LoadResult.SentCount, report.Reconciliation.SentCount);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BurstQ1DropScenario_SmallBurst_ReconciliationSucceeds()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"yagura-bench-smoke-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        try
        {
            var options = new ScenarioOptions(
                Scenario: BenchScenario.BurstQ1Drop,
                Transport: LoadTransport.Udp,
                RatePerSecond: 0,
                DurationSeconds: 0,
                BurstCount: 300,
                SenderSocketCount: 2,
                PaddingBytes: 0,
                DataRoot: null,
                OutputDirectory: outputDirectory,
                SqlServerConnectionString: null,
                SpoolQuotaBytes: 4L * 1024 * 1024,
                KeepDataRoot: false);

            var report = await ScenarioRunner.RunAsync(options);

            // バーストが Q1 容量（既定 1024）を超えない小規模であっても、突合式自体
            // （破棄が発生してもしなくても、送信数 = 保存件数 + アプリ内カウンタ合計）が
            // 成立することを確認する——本テストの主眼は「ハーネスの計測・突合ロジックの正しさ」
            // であり、Q1 破棄の発生有無そのものは問わない。OS 統計差分は CI・共有開発機では
            // 専有前提（§4.2）が保証されないため、上の SustainedZeroDropScenario テストと同じ
            // 理由でアプリ内カウンタのみの式を検証対象にする。
            Assert.Equal(
                report.Reconciliation.SentCount,
                report.Reconciliation.SavedCount + report.Reconciliation.AccountedLossCount);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }
}
