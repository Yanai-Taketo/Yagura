using Yagura.Bench.LoadGeneration;

namespace Yagura.Bench.Scenarios;

/// <summary>
/// CLI から解釈済みのシナリオ実行オプション（Issue #60「CLI 引数で選択実行」）。
/// </summary>
/// <param name="Scenario">実行するシナリオ。</param>
/// <param name="Transport">対象トランスポート（<see cref="BenchScenario.Throughput"/> 等で使用）。</param>
/// <param name="RatePerSecond">持続流量シナリオの目標レート（毎秒）。</param>
/// <param name="DurationSeconds">持続流量シナリオの継続秒数。</param>
/// <param name="BurstCount">バーストシナリオの総送出数。</param>
/// <param name="SenderSocketCount">送信側 socket 数。</param>
/// <param name="PaddingBytes">メッセージパディング長。</param>
/// <param name="DataRoot">Yagura.Host のデータルート（省略時は一時ディレクトリを都度生成）。</param>
/// <param name="OutputDirectory">結果 JSON・サマリの出力先ディレクトリ。</param>
/// <param name="SqlServerConnectionString">
/// SQL Server provider を使う場合の接続文字列（<see cref="BenchScenario.ProviderWriteCeiling"/> で
/// <c>--provider sqlserver</c> 指定時に必須。未指定時は SQLite を対象とする）。
/// </param>
/// <param name="SpoolQuotaBytes">
/// <see cref="BenchScenario.SpoolActivationRecovery"/> でスプール発動を短時間で再現するための
/// 縮小容量（バイト）。
/// </param>
/// <param name="KeepDataRoot">終了後にデータルートを削除せず残すか（障害調査用）。</param>
public sealed record ScenarioOptions(
    BenchScenario Scenario,
    LoadTransport Transport,
    int RatePerSecond,
    int DurationSeconds,
    long BurstCount,
    int SenderSocketCount,
    int PaddingBytes,
    string? DataRoot,
    string OutputDirectory,
    string? SqlServerConnectionString,
    long SpoolQuotaBytes,
    bool KeepDataRoot);
