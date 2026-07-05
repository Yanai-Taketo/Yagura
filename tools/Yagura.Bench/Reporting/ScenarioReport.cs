using Yagura.Bench.LoadGeneration;
using Yagura.Bench.Verification;

namespace Yagura.Bench.Reporting;

/// <summary>
/// 1 シナリオ実行の結果（Issue #60「結果は機械可読（JSON）+ 人間可読サマリ」）。
/// </summary>
/// <param name="ScenarioName">シナリオ名（<see cref="Scenarios.BenchScenario"/> の識別子）。</param>
/// <param name="RunId">実行 ID。</param>
/// <param name="StartedAt">シナリオ実行開始時刻（UTC）。</param>
/// <param name="Environment">実行環境情報。</param>
/// <param name="LoadGeneratorOptions">使用した負荷生成器の構成（送信側 socket/スレッド構成の記録を兼ねる）。</param>
/// <param name="LoadResult">負荷生成器の実行結果。</param>
/// <param name="Reconciliation">突合結果。</param>
/// <param name="ElapsedWallClock">シナリオ全体（送出開始〜検証完了）の所要時間。</param>
/// <param name="AdditionalMetrics">
/// シナリオ固有の追加測定値（例: スプール発動シナリオの drain 所要時間）。キーは日本語可。
/// </param>
/// <param name="Notes">シナリオ実行時の注記（判断・スキップ理由等）。</param>
/// <param name="HostStdout">本体（Yagura.Host 子プロセス）の全出力（stderr は接頭辞 [stderr] 付き）。
/// 飽和の形態分析（M-7/M-13: スプール退避が Q2 溢れ由来かタイムアウト由来か——本体の警告ログに
/// 現れる）と障害調査のための一次情報。Program.cs が .hostlog.txt へも書き出す。</param>
public sealed record ScenarioReport(
    string ScenarioName,
    string RunId,
    DateTimeOffset StartedAt,
    EnvironmentInfo Environment,
    LoadGeneratorOptions LoadGeneratorOptions,
    LoadGeneratorResult LoadResult,
    ReconciliationResult Reconciliation,
    TimeSpan ElapsedWallClock,
    IReadOnlyDictionary<string, string> AdditionalMetrics,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> HostStdout);
