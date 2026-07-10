using Yagura.Bench.StorageBench;

namespace Yagura.Bench.Reporting;

/// <summary>
/// DB-9/DB-10 のストレージベンチマーク（<see cref="Scenarios.BenchScenario.QueryLatency"/>・
/// <see cref="Scenarios.BenchScenario.SchemaMigrationDdl"/>）の結果。既存の
/// <see cref="ScenarioReport"/>（受信パイプラインの負荷測定——送出・突合を前提とする形）とは
/// 性質が異なる（送出・突合の概念を持たない）ため、別の軽量な結果型として持つ。
/// </summary>
public sealed record StorageBenchReport(
    string ScenarioName,
    string RunId,
    DateTimeOffset StartedAt,
    EnvironmentInfo Environment,
    TimeSpan ElapsedWallClock,
    IReadOnlyList<QueryLatencyBenchmark.RowCountResult>? QueryLatencyResults,
    IReadOnlyList<SchemaMigrationDdlBenchmark.RowCountResult>? SchemaMigrationResults,
    IReadOnlyList<string> Notes);
