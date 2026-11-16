using Yagura.Bench.HostProcess;
using Yagura.Bench.LoadGeneration;
using Yagura.Bench.Reporting;
using Yagura.Bench.Scenarios;
using Yagura.Bench.Verification;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Udp;

namespace Yagura.Bench.Tests;

/// <summary>
/// シナリオの CLI 指定から、ホストへ渡す設定（<see cref="BenchHostConfiguration"/>）と結果 JSON の
/// 測定条件（<see cref="BenchConditions"/>）を組み立てる規則の単体テスト。ホストは起動しない。
/// </summary>
public sealed class BenchHostConfigurationTests
{
    private static ScenarioOptions Options(
        BenchScenario scenario,
        string? sqlServer = null,
        int? udpReceiveBufferBytes = null,
        bool? flowControlEnabled = null,
        int? flowControlRate = null,
        int? flowControlBurst = null,
        int drainTimeoutSeconds = ScenarioOptions.DefaultDrainTimeoutSeconds) =>
        new(
            Scenario: scenario,
            Transport: LoadTransport.Tcp,
            RatePerSecond: 1000,
            DurationSeconds: 10,
            BurstCount: 5000,
            SenderSocketCount: 4,
            PaddingBytes: 0,
            DataRoot: null,
            OutputDirectory: "out",
            SqlServerConnectionString: sqlServer,
            SpoolQuotaBytes: 4L * 1024 * 1024,
            KeepDataRoot: false,
            UdpReceiveBufferBytes: udpReceiveBufferBytes,
            FlowControlEnabled: flowControlEnabled,
            FlowControlMessagesPerSecond: flowControlRate,
            FlowControlBurstSize: flowControlBurst,
            DrainTimeoutSeconds: drainTimeoutSeconds);

    /// <summary>CI 回帰ベンチの 2 シナリオは既定のまま設定ファイルを書かない（結果の前提を変えない）。</summary>
    [Theory]
    [InlineData(BenchScenario.SustainedZeroDrop)]
    [InlineData(BenchScenario.ProviderWriteCeiling)]
    [InlineData(BenchScenario.Throughput)]
    [InlineData(BenchScenario.BurstQ1Drop)]
    public void BuildHostConfiguration_Defaults_IsEmpty(BenchScenario scenario)
    {
        var configuration = ScenarioRunner.BuildHostConfiguration(Options(scenario));

        Assert.True(configuration.IsEmpty);
    }

    [Fact]
    public void BuildHostConfiguration_SpoolActivationRecovery_AlwaysCarriesQuota()
    {
        var configuration = ScenarioRunner.BuildHostConfiguration(Options(BenchScenario.SpoolActivationRecovery));

        Assert.Equal(4L * 1024 * 1024, configuration.SpoolQuotaBytes);
        Assert.Null(configuration.FlowControlEnabled);
    }

    /// <summary>SQL Server・流量制御・受信バッファを同時に指定でき、1 つの設定に合成される。</summary>
    [Fact]
    public void BuildHostConfiguration_ProviderWriteCeiling_ComposesSqlServerFlowControlAndReceiveBuffer()
    {
        const string ConnectionString = "Server=(localdb)\\MSSQLLocalDB;Database=YaguraBench;Integrated Security=true";

        var configuration = ScenarioRunner.BuildHostConfiguration(Options(
            BenchScenario.ProviderWriteCeiling,
            sqlServer: ConnectionString,
            udpReceiveBufferBytes: 4 * 1024 * 1024,
            flowControlEnabled: false));

        Assert.Equal(ConnectionString, configuration.SqlServerConnectionString);
        Assert.Equal(4 * 1024 * 1024, configuration.UdpReceiveBufferBytes);
        Assert.False(configuration.FlowControlEnabled);
        Assert.Null(configuration.SpoolQuotaBytes);
    }

    /// <summary><c>--sqlserver</c> は ProviderWriteCeiling 専用（README の契約）。他シナリオでは SQLite のまま測る。</summary>
    [Fact]
    public void BuildHostConfiguration_SqlServerOutsideProviderWriteCeiling_IsIgnored()
    {
        var configuration = ScenarioRunner.BuildHostConfiguration(Options(
            BenchScenario.SustainedZeroDrop,
            sqlServer: "Server=.;Database=YaguraBench;Integrated Security=true",
            flowControlEnabled: false));

        Assert.Null(configuration.SqlServerConnectionString);
        Assert.False(configuration.FlowControlEnabled);
    }

    [Fact]
    public void BuildHostConfiguration_FlowControlThresholds_ArePassedThrough()
    {
        var configuration = ScenarioRunner.BuildHostConfiguration(Options(
            BenchScenario.BurstQ1Drop,
            flowControlRate: 50_000,
            flowControlBurst: 100_000));

        Assert.Null(configuration.FlowControlEnabled);
        Assert.Equal(50_000, configuration.FlowControlMessagesPerSecond);
        Assert.Equal(100_000, configuration.FlowControlBurstSize);
    }

    /// <summary>
    /// 何も指定しない実行の測定条件は「製品既定の流量制御が効いている」と記録される。集計側は
    /// この記録で、流量制御を外し忘れた標本を公称値の算出から見分ける。
    /// </summary>
    [Fact]
    public void BenchConditions_Defaults_RecordProductDefaultFlowControlAndBuffer()
    {
        var conditions = BenchConditions.From(Options(BenchScenario.SustainedZeroDrop), drainCompleted: true, drainWaitElapsed: TimeSpan.Zero);

        Assert.Equal("Sqlite", conditions.Provider);
        Assert.True(conditions.FlowControlEnabled);
        Assert.False(conditions.FlowControlOverridden);
        Assert.Equal(TokenBucketIngressGate.DefaultMessagesPerSecond, conditions.FlowControlMessagesPerSecond);
        Assert.Equal(TokenBucketIngressGate.DefaultBurstSize, conditions.FlowControlBurstSize);
        Assert.Equal(UdpSyslogListenerOptions.DefaultReceiveBufferBytes, conditions.UdpReceiveBufferBytes);
        Assert.False(conditions.UdpReceiveBufferOverridden);
        Assert.Equal(ScenarioOptions.DefaultDrainTimeoutSeconds, conditions.DrainTimeoutSeconds);
        Assert.True(conditions.DrainCompleted);
    }

    [Fact]
    public void BenchConditions_FlowControlOff_RecordsDisabledWithoutThresholds()
    {
        var conditions = BenchConditions.From(
            Options(BenchScenario.ProviderWriteCeiling, sqlServer: "Server=.;Database=B", udpReceiveBufferBytes: 4 * 1024 * 1024, flowControlEnabled: false, drainTimeoutSeconds: 600),
            drainCompleted: false,
            drainWaitElapsed: TimeSpan.FromSeconds(600));

        Assert.Equal("SqlServer", conditions.Provider);
        Assert.False(conditions.FlowControlEnabled);
        Assert.True(conditions.FlowControlOverridden);
        Assert.Null(conditions.FlowControlMessagesPerSecond);
        Assert.Null(conditions.FlowControlBurstSize);
        Assert.Equal(4 * 1024 * 1024, conditions.UdpReceiveBufferBytes);
        Assert.True(conditions.UdpReceiveBufferOverridden);
        Assert.Equal(600, conditions.DrainTimeoutSeconds);
        Assert.False(conditions.DrainCompleted);
    }

    /// <summary>集計スクリプトは JSON のキー名で測定条件を読む。名前の変更は集計を黙って壊す。</summary>
    [Fact]
    public void ReportJson_ContainsConditionsKeysReadByAggregationScript()
    {
        var report = new ScenarioReport(
            ScenarioName: "SustainedZeroDrop",
            RunId: "test-run",
            StartedAt: DateTimeOffset.UnixEpoch,
            Environment: new EnvironmentInfo("os", "X64", "cpu", 4, 0, "drive", "runtime", "machine"),
            LoadGeneratorOptions: new LoadGeneratorOptions(LoadTransport.Udp, LoadPattern.Sustained, "127.0.0.1", 0, "test-run"),
            LoadResult: new LoadGeneratorResult("test-run", 0, 0, 0, TimeSpan.Zero, 4),
            Reconciliation: new ReconciliationResult(0, 0, 0, 0, 0, 0, 0, true, new IngestionCounterSnapshot(0, 0, 0, 0, 0, 0, 0)),
            ElapsedWallClock: TimeSpan.Zero,
            AdditionalMetrics: new Dictionary<string, string>(),
            Notes: [],
            HostStdout: [],
            Conditions: BenchConditions.From(Options(BenchScenario.SustainedZeroDrop, flowControlEnabled: false), drainCompleted: true, drainWaitElapsed: TimeSpan.Zero));

        var json = ReportWriter.ToJson(report);

        Assert.Contains("\"Conditions\"", json, StringComparison.Ordinal);
        Assert.Contains("\"FlowControlEnabled\": false", json, StringComparison.Ordinal);
        Assert.Contains("\"Provider\": \"Sqlite\"", json, StringComparison.Ordinal);
        Assert.Contains("\"UdpReceiveBufferBytes\"", json, StringComparison.Ordinal);
        Assert.Contains("\"DrainCompleted\": true", json, StringComparison.Ordinal);
    }
}
