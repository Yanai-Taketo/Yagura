using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Yagura.Bench.Reporting;

/// <summary>
/// 結果出力（Issue #60「機械可読（JSON）+ 人間可読サマリ」）。
/// </summary>
public static class ReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>結果を JSON 文字列へ整形する。</summary>
    public static string ToJson(ScenarioReport report) => JsonSerializer.Serialize(report, JsonOptions);

    /// <summary>結果を JSON ファイルへ書き出す。</summary>
    public static void WriteJsonFile(ScenarioReport report, string filePath) =>
        File.WriteAllText(filePath, ToJson(report));

    /// <summary>人間可読サマリを組み立てる（コンソール出力・ファイル保存の両方に使える単純文字列）。</summary>
    public static string ToHumanReadableSummary(ScenarioReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"=== ベンチ結果: {report.ScenarioName} ===");
        sb.AppendLine(CultureInfo.InvariantCulture, $"実行 ID: {report.RunId}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"開始時刻(UTC): {report.StartedAt:o}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"所要時間: {report.ElapsedWallClock}");
        sb.AppendLine();

        sb.AppendLine("--- 実行環境 ---");
        sb.AppendLine(CultureInfo.InvariantCulture, $"マシン名: {report.Environment.MachineName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"OS: {report.Environment.OsDescription} ({report.Environment.OsArchitecture})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"CPU: {report.Environment.ProcessorName ?? "(取得不可)"} / 論理コア数 {report.Environment.LogicalProcessorCount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"メモリ: {report.Environment.TotalPhysicalMemoryBytes / (1024 * 1024 * 1024)} GiB");
        sb.AppendLine(CultureInfo.InvariantCulture, $"ディスク: {report.Environment.DataRootDriveDescription}");
        sb.AppendLine(CultureInfo.InvariantCulture, $".NET ランタイム: {report.Environment.DotnetRuntimeVersion}");
        sb.AppendLine();

        sb.AppendLine("--- 負荷生成器 ---");
        sb.AppendLine(CultureInfo.InvariantCulture, $"トランスポート: {report.LoadGeneratorOptions.Transport} / パターン: {report.LoadGeneratorOptions.Pattern}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"送信側 socket 数: {report.LoadResult.SenderSocketCount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"送出試行数: {report.LoadResult.AttemptedCount} / 成功: {report.LoadResult.SucceededCount} / 失敗: {report.LoadResult.FailedCount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"送出所要時間: {report.LoadResult.Elapsed} (実効レート: {ComputeEffectiveRate(report.LoadResult):F1} msg/sec)");
        sb.AppendLine();

        sb.AppendLine("--- 突合（送信数 = 保存件数 + 全カウンタの合計）---");
        var r = report.Reconciliation;
        sb.AppendLine(CultureInfo.InvariantCulture, $"送信数: {r.SentCount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"保存件数: {r.SavedCount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"破棄・退避系カウンタ合計: {r.AccountedLossCount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  内部バッファ破棄: {r.Counters.InternalBufferDropped}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  TCP接続拒否: {r.Counters.TcpConnectionRejected}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  スプール書込失敗: {r.Counters.SpoolWriteFailed}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  スプール破棄: {r.Counters.SpoolDiscarded}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  永続化失敗: {r.Counters.PersistenceFailed}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  流量制御破棄: {r.Counters.FlowControlDropped}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"参考: スプール退避（累積。drain完了後は保存件数に含まれる）: {r.SpoolEvacuatedCount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"参考: OS UDP 統計カウンタ差分（自己宛送信は現れない——非ゼロは他プロセスの背景ノイズ）: {r.OsUdpDatagramsDiscardedDelta}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"OS ソケットバッファ破棄（導出値 = 送信数 - 保存 - アプリ内カウンタ。UDP のみ）: {r.DerivedOsBufferLossCount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"差分（アプリ内計上との残差）: {r.Difference}");
        var verdict = r.IsReconciled
            ? r.DerivedOsBufferLossCount > 0
                ? $"OK（全損失を帰属できた——うち OS バッファ破棄（導出値）{r.DerivedOsBufferLossCount} 件）"
                : "OK（突合成立——完全一致）"
            : "NG（突合不成立——過剰計上、または TCP での計上漏れ疑い）";
        sb.AppendLine(CultureInfo.InvariantCulture, $"判定: {verdict}");
        sb.AppendLine();

        if (report.AdditionalMetrics.Count > 0)
        {
            sb.AppendLine("--- シナリオ固有の追加測定値 ---");
            foreach (var (key, value) in report.AdditionalMetrics)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{key}: {value}");
            }

            sb.AppendLine();
        }

        if (report.Notes.Count > 0)
        {
            sb.AppendLine("--- 注記 ---");
            foreach (var note in report.Notes)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {note}");
            }
        }

        return sb.ToString();
    }

    private static double ComputeEffectiveRate(LoadGeneration.LoadGeneratorResult result) =>
        result.Elapsed.TotalSeconds > 0 ? result.SucceededCount / result.Elapsed.TotalSeconds : 0;
}
