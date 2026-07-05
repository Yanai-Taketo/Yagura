using Yagura.Bench.LoadGeneration;
using Yagura.Storage.Spool;

namespace Yagura.Bench.Scenarios;

/// <summary>
/// CLI 引数の解析（Issue #60）。<c>tools/Yagura.Bench/README.md</c> に使用例を記載する。
/// </summary>
public static class ScenarioOptionsParser
{
    public static ScenarioOptions Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            throw new BenchUsageException(BuildUsageText());
        }

        if (!Enum.TryParse<BenchScenario>(args[0], ignoreCase: true, out var scenario))
        {
            throw new BenchUsageException(
                $"未知のシナリオ '{args[0]}'。有効な値: {string.Join(", ", Enum.GetNames<BenchScenario>())}\n\n{BuildUsageText()}");
        }

        var transport = LoadTransport.Udp;
        var rate = 1000;
        var duration = 10;
        long burstCount = 5000;
        var senderSockets = 4;
        var padding = 0;
        string? dataRoot = null;
        var outputDirectory = Path.Combine(Directory.GetCurrentDirectory(), "bench-results");
        string? sqlServerConnectionString = null;
        var spoolQuotaBytes = 4L * 1024 * 1024; // 既定 4 MiB。TargetSegmentSizeBytes と同程度——数セグメントで発動させる狙い。
        var keepDataRoot = false;
        string? compareBaselinePath = null;
        int? udpReceiveBufferBytes = null;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--transport":
                    transport = ParseTransport(RequireValue(args, ref i, "--transport"));
                    break;
                case "--rate":
                    rate = int.Parse(RequireValue(args, ref i, "--rate"));
                    break;
                case "--duration":
                    duration = int.Parse(RequireValue(args, ref i, "--duration"));
                    break;
                case "--burst-count":
                    burstCount = long.Parse(RequireValue(args, ref i, "--burst-count"));
                    break;
                case "--sender-sockets":
                    senderSockets = int.Parse(RequireValue(args, ref i, "--sender-sockets"));
                    break;
                case "--padding-bytes":
                    padding = int.Parse(RequireValue(args, ref i, "--padding-bytes"));
                    break;
                case "--data-root":
                    dataRoot = RequireValue(args, ref i, "--data-root");
                    break;
                case "--output-dir":
                    outputDirectory = RequireValue(args, ref i, "--output-dir");
                    break;
                case "--sqlserver":
                    sqlServerConnectionString = RequireValue(args, ref i, "--sqlserver");
                    break;
                case "--spool-quota-bytes":
                    spoolQuotaBytes = long.Parse(RequireValue(args, ref i, "--spool-quota-bytes"));
                    break;
                case "--keep-data-root":
                    keepDataRoot = true;
                    break;
                case "--compare-baseline":
                    compareBaselinePath = RequireValue(args, ref i, "--compare-baseline");
                    break;
                case "--udp-receive-buffer-bytes":
                    udpReceiveBufferBytes = int.Parse(RequireValue(args, ref i, "--udp-receive-buffer-bytes"));
                    break;
                default:
                    throw new BenchUsageException($"未知のオプション '{args[i]}'。\n\n{BuildUsageText()}");
            }
        }

        return new ScenarioOptions(
            scenario,
            transport,
            rate,
            duration,
            burstCount,
            senderSockets,
            padding,
            dataRoot,
            outputDirectory,
            sqlServerConnectionString,
            spoolQuotaBytes,
            keepDataRoot,
            compareBaselinePath,
            udpReceiveBufferBytes);
    }

    private static LoadTransport ParseTransport(string value) => value.ToLowerInvariant() switch
    {
        "udp" => LoadTransport.Udp,
        "tcp" => LoadTransport.Tcp,
        _ => throw new BenchUsageException($"--transport は udp または tcp のみ指定可能（指定値: '{value}'）。"),
    };

    private static string RequireValue(string[] args, ref int i, string optionName)
    {
        if (i + 1 >= args.Length)
        {
            throw new BenchUsageException($"{optionName} には値が必要。");
        }

        i++;
        return args[i];
    }

    public static string BuildUsageText() =>
        """
        Yagura.Bench — Yagura ベンチハーネス (Issue #60 / architecture.md §5.1)

        使い方:
          Yagura.Bench <scenario> [オプション]

        シナリオ:
          Throughput               受信スループット計測（UDP/TCP 別。--transport で選択）
          SustainedZeroDrop        破棄ゼロで維持できる持続流量の確認
          BurstQ1Drop              バースト負荷時の Q1 破棄の発生有無（UDP 固定）
          SpoolActivationRecovery  スプール発動 → 追いつきの所要時間
          ProviderWriteCeiling     SQLite / SQL Server 書き込み上限（--sqlserver で SQL Server 対象）

        共通オプション:
          --transport <udp|tcp>       対象トランスポート（既定 udp。Throughput/SustainedZeroDrop/ProviderWriteCeiling で使用）
          --rate <N>                  持続流量シナリオの目標レート（毎秒。既定 1000）
          --duration <秒>              持続流量シナリオの継続秒数（既定 10）
          --burst-count <N>           バーストシナリオの総送出数（既定 5000）
          --sender-sockets <N>        送信側 socket 数（既定 4。ベンチ自身のボトルネック回避）
          --padding-bytes <N>         メッセージ本文への追加パディング長（既定 0）
          --data-root <path>          Yagura.Host のデータルート（既定: 一時ディレクトリを都度生成）
          --output-dir <path>         結果 JSON・サマリの出力先（既定: ./bench-results）
          --sqlserver <接続文字列>      SQL Server provider を対象にする（ProviderWriteCeiling 専用）
          --spool-quota-bytes <N>     スプール容量（SpoolActivationRecovery 専用。既定 4194304 = 4MiB）
          --keep-data-root            終了後にデータルートを削除せず残す（障害調査用）
          --compare-baseline <path>   基準値ファイルと比較し、許容帯超過の劣化で終了コード 3（CI 回帰判定。
                                      Issue #62 / architecture.md §5.2。絶対値の合否は行わない）
          --udp-receive-buffer-bytes <N>  UDP 受信バッファサイズ（SO_RCVBUF。バイト。M-2。
                                           既定: 未指定 = 製品既定のまま上書きしない）

        例:
          Yagura.Bench Throughput --transport udp --rate 2000 --duration 15
          Yagura.Bench BurstQ1Drop --burst-count 20000 --sender-sockets 8
          Yagura.Bench SpoolActivationRecovery --spool-quota-bytes 2097152
          Yagura.Bench ProviderWriteCeiling --sqlserver "Server=.;Database=YaguraBench;Integrated Security=true;"
          Yagura.Bench SustainedZeroDrop --rate 5000 --duration 10 --compare-baseline tools\Yagura.Bench\baselines\ci-baseline.json
          Yagura.Bench SustainedZeroDrop --rate 30000 --duration 10 --udp-receive-buffer-bytes 4194304
        """;
}

/// <summary>使い方エラー（<see cref="Program"/> がメッセージを表示して終了コード 1 で終了する）。</summary>
public sealed class BenchUsageException(string message) : Exception(message);
