using Yagura.Bench.HostProcess;
using Yagura.Bench.LoadGeneration;
using Yagura.Bench.Reporting;
using Yagura.Bench.Verification;
using Yagura.Host.Observability;

namespace Yagura.Bench.Scenarios;

/// <summary>
/// シナリオランナー（Issue #60「計測対象の最小セットを再現可能な形で実行する CLI」）。
/// </summary>
public static class ScenarioRunner
{
    /// <summary>
    /// 負荷停止後、Q2 の消費・DB 書き込みが落ち着くのを待つ静定時間。最終カウンタの正確性は
    /// この待機ではなく<b>グレースフル停止</b>（停止手順 3 の最終カウンタ書き込み。
    /// <see cref="ConsoleCtrlSender"/>）が保証する——本値は定期永続化間隔（既定 10 秒）より
    /// 短く、Kill フォールバック時（グレースフル停止が使えない環境）の取りこぼしをカバーしない
    /// （その場合は StopAndReconcileAsync が警告を出す）。
    /// </summary>
    private static readonly TimeSpan MetadataSettleMargin = TimeSpan.FromSeconds(5);

    public static async Task<ScenarioReport> RunAsync(ScenarioOptions options)
    {
        var runId = Guid.NewGuid().ToString("N")[..12];
        var dataRoot = options.DataRoot ?? Path.Combine(Path.GetTempPath(), $"yagura-bench-{runId}");
        var startedAt = DateTimeOffset.UtcNow;

        Directory.CreateDirectory(dataRoot);

        try
        {
            return options.Scenario switch
            {
                BenchScenario.Throughput => await RunThroughputAsync(options, runId, dataRoot, startedAt).ConfigureAwait(false),
                BenchScenario.SustainedZeroDrop => await RunSustainedZeroDropAsync(options, runId, dataRoot, startedAt).ConfigureAwait(false),
                BenchScenario.BurstQ1Drop => await RunBurstQ1DropAsync(options, runId, dataRoot, startedAt).ConfigureAwait(false),
                BenchScenario.SpoolActivationRecovery => await RunSpoolActivationRecoveryAsync(options, runId, dataRoot, startedAt).ConfigureAwait(false),
                BenchScenario.ProviderWriteCeiling => await RunProviderWriteCeilingAsync(options, runId, dataRoot, startedAt).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(options)),
            };
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
                    // ベストエフォート（tests/Yagura.E2E.Tests と同じ判断。子プロセス停止直後の
                    // SQLite WAL 補助ファイルのハンドル解放に短い遅延があり得る）。
                }
            }
        }
    }

    /// <summary>受信スループット（UDP/TCP 別）。指定レートで持続送出し、スループット・突合を報告する。</summary>
    private static async Task<ScenarioReport> RunThroughputAsync(ScenarioOptions options, string runId, string dataRoot, DateTimeOffset startedAt)
    {
        var wallClock = System.Diagnostics.Stopwatch.StartNew();
        await using var host = await BenchHostProcess.StartAsync(dataRoot).ConfigureAwait(false);
        var osUdpBaseline = OsUdpStatsProbe.GetCurrentTotalDiscarded();

        var targetPort = options.Transport == LoadTransport.Udp ? host.UdpPort : host.TcpPort;
        var loadOptions = new LoadGeneratorOptions(
            options.Transport,
            LoadPattern.Sustained,
            "127.0.0.1",
            targetPort,
            runId,
            RatePerSecond: options.RatePerSecond,
            DurationSeconds: options.DurationSeconds,
            SenderSocketCount: options.SenderSocketCount,
            PaddingBytes: options.PaddingBytes);

        var loadResult = await SendAsync(loadOptions).ConfigureAwait(false);

        var reconciliation = await StopAndReconcileAsync(host, dataRoot, loadResult, osUdpBaseline, transportIsUdp: options.Transport == LoadTransport.Udp).ConfigureAwait(false);
        wallClock.Stop();

        return BuildReport(
            "Throughput",
            runId,
            startedAt,
            dataRoot,
            loadOptions,
            loadResult,
            reconciliation,
            wallClock.Elapsed,
            new Dictionary<string, string>(),
            [$"目標レート {options.RatePerSecond} msg/sec を {options.DurationSeconds} 秒間、{options.Transport} で送出した。"],
            host.StdoutLines);
    }

    /// <summary>破棄ゼロで維持できる持続流量の確認。指定レートで送出し、破棄系カウンタが 0 のままかを報告する。</summary>
    private static async Task<ScenarioReport> RunSustainedZeroDropAsync(ScenarioOptions options, string runId, string dataRoot, DateTimeOffset startedAt)
    {
        var wallClock = System.Diagnostics.Stopwatch.StartNew();

        if (options.UdpReceiveBufferBytes is { } udpReceiveBufferBytes)
        {
            // 受信バッファ値別の破棄ゼロ上限比較（M-2 実測。BenchConfigurationFile 参照）。
            BenchConfigurationFile.WriteUdpReceiveBufferConfiguration(dataRoot, udpReceiveBufferBytes);
        }

        await using var host = await BenchHostProcess.StartAsync(dataRoot).ConfigureAwait(false);
        var osUdpBaseline = OsUdpStatsProbe.GetCurrentTotalDiscarded();

        var targetPort = options.Transport == LoadTransport.Udp ? host.UdpPort : host.TcpPort;
        var loadOptions = new LoadGeneratorOptions(
            options.Transport,
            LoadPattern.Sustained,
            "127.0.0.1",
            targetPort,
            runId,
            RatePerSecond: options.RatePerSecond,
            DurationSeconds: options.DurationSeconds,
            SenderSocketCount: options.SenderSocketCount,
            PaddingBytes: options.PaddingBytes);

        var loadResult = await SendAsync(loadOptions).ConfigureAwait(false);
        var reconciliation = await StopAndReconcileAsync(host, dataRoot, loadResult, osUdpBaseline, transportIsUdp: options.Transport == LoadTransport.Udp).ConfigureAwait(false);
        wallClock.Stop();

        // 「破棄ゼロ」はアプリ内カウンタに加えて OS バッファ破棄（導出値）もゼロであることを
        // 要求する（M7-2 設計変更: 自己宛 UDP は OS 統計に現れないため、導出値が観測手段）。
        var dropFree = reconciliation.AccountedLossCount == 0 && reconciliation.DerivedOsBufferLossCount == 0;
        var notes = new List<string>
        {
            $"目標レート {options.RatePerSecond} msg/sec を {options.DurationSeconds} 秒間送出した。",
            dropFree
                ? "破棄・退避系カウンタは 0 のまま維持された（このレートは破棄ゼロで維持できる）。"
                : "破棄・退避系カウンタが発生した（このレートは破棄ゼロを維持できていない——レートを下げて再試行すること）。",
        };

        return BuildReport(
            "SustainedZeroDrop",
            runId,
            startedAt,
            dataRoot,
            loadOptions,
            loadResult,
            reconciliation,
            wallClock.Elapsed,
            new Dictionary<string, string> { ["破棄ゼロ維持"] = dropFree.ToString() },
            notes,
            host.StdoutLines);
    }

    /// <summary>
    /// バースト負荷時の Q1 破棄の発生有無（§3.1 の前提検証）。UDP 固定
    /// （Q1 溢れは UDP 由来のみが「破棄」として現れる設計——TCP は読み取り停止で表れるため対象外）。
    /// </summary>
    private static async Task<ScenarioReport> RunBurstQ1DropAsync(ScenarioOptions options, string runId, string dataRoot, DateTimeOffset startedAt)
    {
        var wallClock = System.Diagnostics.Stopwatch.StartNew();

        if (options.UdpReceiveBufferBytes is { } udpReceiveBufferBytes)
        {
            // 受信バッファ値別の OS バッファ破棄（導出値）比較（M-2 実測。BenchConfigurationFile 参照）。
            BenchConfigurationFile.WriteUdpReceiveBufferConfiguration(dataRoot, udpReceiveBufferBytes);
        }

        await using var host = await BenchHostProcess.StartAsync(dataRoot).ConfigureAwait(false);
        var osUdpBaseline = OsUdpStatsProbe.GetCurrentTotalDiscarded();

        var loadOptions = new LoadGeneratorOptions(
            LoadTransport.Udp,
            LoadPattern.Burst,
            "127.0.0.1",
            host.UdpPort,
            runId,
            BurstCount: options.BurstCount,
            SenderSocketCount: options.SenderSocketCount,
            PaddingBytes: options.PaddingBytes);

        var loadResult = await SendAsync(loadOptions).ConfigureAwait(false);
        var reconciliation = await StopAndReconcileAsync(host, dataRoot, loadResult, osUdpBaseline).ConfigureAwait(false);
        wallClock.Stop();

        var q1DropOccurred = reconciliation.Counters.InternalBufferDropped > 0;
        var notes = new List<string>
        {
            $"{options.BurstCount} 通を可能な限り高速で一斉送出した（送信側 socket 数 {options.SenderSocketCount}）。",
            q1DropOccurred
                ? $"Q1（内部バッファ）破棄が {reconciliation.Counters.InternalBufferDropped} 件発生した。architecture.md §3.1 の前提（バースト時に限られる）どおりの挙動。"
                : "Q1（内部バッファ）破棄は発生しなかった（このバースト規模では Q1 容量内に収まった）。",
            reconciliation.DerivedOsBufferLossCount > 0
                ? $"OS ソケットバッファでの破棄（導出値）が {reconciliation.DerivedOsBufferLossCount} 件発生した——Q1 に届く前の OS レベルの損失。受信バッファ拡大（M-2）の効果測定の入力になる。"
                : "OS ソケットバッファでの破棄（導出値）は発生しなかった。",
        };

        return BuildReport(
            "BurstQ1Drop",
            runId,
            startedAt,
            dataRoot,
            loadOptions,
            loadResult,
            reconciliation,
            wallClock.Elapsed,
            new Dictionary<string, string> { ["Q1破棄発生"] = q1DropOccurred.ToString() },
            notes,
            host.StdoutLines);
    }

    /// <summary>スプール発動 → 追いつきの所要（縮小容量 + バースト送出でスプール発動を誘発し、drain 完了までの時間を計測する）。</summary>
    private static async Task<ScenarioReport> RunSpoolActivationRecoveryAsync(ScenarioOptions options, string runId, string dataRoot, DateTimeOffset startedAt)
    {
        var wallClock = System.Diagnostics.Stopwatch.StartNew();

        // スプール容量を縮小した設定ファイルを事前に書く（既定 1GiB では短時間の負荷で
        // 上限到達を再現できないため。BenchConfigurationFile 参照）。
        BenchConfigurationFile.WriteSpoolQuotaConfiguration(dataRoot, options.SpoolQuotaBytes);

        await using var host = await BenchHostProcess.StartAsync(dataRoot).ConfigureAwait(false);
        var osUdpBaseline = OsUdpStatsProbe.GetCurrentTotalDiscarded();

        // Q2 容量（PipelineConstants.Q2Capacity=1024）を上回るバーストで送出し、
        // 解析段 → 永続化段の間でスプール退避を誘発する。
        var loadOptions = new LoadGeneratorOptions(
            LoadTransport.Udp,
            LoadPattern.Burst,
            "127.0.0.1",
            host.UdpPort,
            runId,
            BurstCount: options.BurstCount,
            SenderSocketCount: options.SenderSocketCount,
            PaddingBytes: options.PaddingBytes);

        var loadResult = await SendAsync(loadOptions).ConfigureAwait(false);

        var spoolDirectory = Path.Combine(dataRoot, "spool");
        var spoolActivated = ExternalSpoolUsageProbe.GetSegmentBytesOnDisk(spoolDirectory) > 0;

        var drainStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var drained = await ExternalSpoolUsageProbe.WaitForDrainCompletionAsync(spoolDirectory, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        drainStopwatch.Stop();

        var reconciliation = await StopAndReconcileAsync(host, dataRoot, loadResult, osUdpBaseline).ConfigureAwait(false);
        wallClock.Stop();

        var notes = new List<string>
        {
            $"スプール容量を {options.SpoolQuotaBytes} バイトに縮小し、{options.BurstCount} 通のバーストでスプール発動を誘発した。",
            spoolActivated
                ? "スプール発動を確認した（*.seg ファイルがディスク上に現れた）。"
                : "スプールは発動しなかった（バースト規模またはレートが発動条件に届かなかった——burst-count を増やして再試行すること）。",
            drained
                ? $"drain が完了し、スプール使用量は 0 に戻った（所要 {drainStopwatch.Elapsed}）。"
                : $"drain が 2 分以内に完了しなかった（タイムアウト。所要 {drainStopwatch.Elapsed} で打ち切り）。",
        };

        return BuildReport(
            "SpoolActivationRecovery",
            runId,
            startedAt,
            dataRoot,
            loadOptions,
            loadResult,
            reconciliation,
            wallClock.Elapsed,
            new Dictionary<string, string>
            {
                ["スプール発動"] = spoolActivated.ToString(),
                ["drain完了"] = drained.ToString(),
                ["drain所要時間"] = drainStopwatch.Elapsed.ToString(),
                ["スプール容量バイト"] = options.SpoolQuotaBytes.ToString(),
            },
            notes,
            host.StdoutLines);
    }

    /// <summary>SQLite / SQL Server 各 provider の書き込み上限。指定 provider へ高レート持続送出し、スプール退避の発生有無から上限接近を報告する。</summary>
    private static async Task<ScenarioReport> RunProviderWriteCeilingAsync(ScenarioOptions options, string runId, string dataRoot, DateTimeOffset startedAt)
    {
        var wallClock = System.Diagnostics.Stopwatch.StartNew();
        var usingSqlServer = !string.IsNullOrWhiteSpace(options.SqlServerConnectionString);

        if (usingSqlServer)
        {
            BenchConfigurationFile.WriteSqlServerConfiguration(dataRoot, options.SqlServerConnectionString!);
        }

        // SQL Server は SQLite(実行ごとに新規の一時データルート)と異なり同一データベースを
        // 実行間で使い回すため、保存件数は実行前の既存件数との差分で数える必要がある
        // (CounterReconciler.Reconcile の savedCount 引数の要件)。これを怠った初版は
        // YAGURA-STG 実測(2026-07-05)で前実行分の累積が混入し、全 SQL Server 実行が
        // 見かけ上「過剰計上 NG」になった(差分補正後は全実行が損失ゼロで完全成立)。
        var savedBaseline = usingSqlServer
            ? await LogStoreProbe.GetSqlServerRecordCountAsync(options.SqlServerConnectionString!).ConfigureAwait(false)
            : 0;

        await using var host = await BenchHostProcess.StartAsync(dataRoot).ConfigureAwait(false);
        var osUdpBaseline = OsUdpStatsProbe.GetCurrentTotalDiscarded();

        var loadOptions = new LoadGeneratorOptions(
            options.Transport,
            LoadPattern.Sustained,
            "127.0.0.1",
            options.Transport == LoadTransport.Udp ? host.UdpPort : host.TcpPort,
            runId,
            RatePerSecond: options.RatePerSecond,
            DurationSeconds: options.DurationSeconds,
            SenderSocketCount: options.SenderSocketCount,
            PaddingBytes: options.PaddingBytes);

        var loadResult = await SendAsync(loadOptions).ConfigureAwait(false);
        var reconciliation = await StopAndReconcileAsync(host, dataRoot, loadResult, osUdpBaseline, sqlServerConnectionString: options.SqlServerConnectionString, transportIsUdp: options.Transport == LoadTransport.Udp, savedBaseline: savedBaseline).ConfigureAwait(false);
        wallClock.Stop();

        var approachingCeiling = reconciliation.SpoolEvacuatedCount > 0;
        var notes = new List<string>
        {
            $"provider: {(usingSqlServer ? "SQL Server" : "SQLite")}。目標レート {options.RatePerSecond} msg/sec を {options.DurationSeconds} 秒間送出した。",
            approachingCeiling
                ? $"スプール退避が {reconciliation.SpoolEvacuatedCount} 件発生した——このレートは provider の書き込み上限（または上限接近）を示唆する（§5.3 の飽和シグナルと同じ考え方）。"
                : "スプール退避は発生しなかった——このレートは provider が正味処理できている範囲内。",
        };

        return BuildReport(
            "ProviderWriteCeiling",
            runId,
            startedAt,
            dataRoot,
            loadOptions,
            loadResult,
            reconciliation,
            wallClock.Elapsed,
            new Dictionary<string, string>
            {
                ["provider"] = usingSqlServer ? "SqlServer" : "Sqlite",
                ["上限接近シグナル(スプール退避>0)"] = approachingCeiling.ToString(),
            },
            notes,
            host.StdoutLines);
    }

    private static async Task<LoadGeneratorResult> SendAsync(LoadGeneratorOptions options) =>
        options.Transport == LoadTransport.Udp
            ? await UdpLoadGenerator.RunAsync(options).ConfigureAwait(false)
            : await TcpLoadGenerator.RunAsync(options).ConfigureAwait(false);

    /// <summary>
    /// 送出完了後、drain の残りが片付くのを待ってからグレースフル停止
    /// （<see cref="BenchHostProcess.StopGracefullyAsync"/>。Ctrl+C 経由で architecture.md §1.3
    /// の停止手順 1〜3——メタデータ領域への最終カウンタ書き込みを含む——を実行させる）で
    /// 子プロセスを止め、保存件数・カウンタを突合する。
    /// </summary>
    /// <remarks>
    /// グレースフル停止（<see cref="ConsoleCtrlSender"/>）採用前は Kill 停止に頼っており、
    /// 直近の定期永続化（既定 10 秒間隔）以降に発生した破棄がメタデータ領域へ反映されないまま
    /// 検証してしまい、突合が実機で不成立になっていた（バースト系シナリオで顕在化）。
    /// グレースフル停止は停止手順 3 を同期的に実行させるため、この種の取りこぼしを避けられる。
    /// </remarks>
    private static async Task<ReconciliationResult> StopAndReconcileAsync(
        BenchHostProcess host,
        string dataRoot,
        LoadGeneratorResult loadResult,
        long osUdpBaselineDiscarded = 0,
        string? sqlServerConnectionString = null,
        bool transportIsUdp = true,
        long savedBaseline = 0)
    {
        // スプール drain が残っている場合は完了を待つ（突合式は drain 完了後でないと
        // 「スプール退避」と「保存件数」の間で二重計上のように見えるため。
        // CounterReconciler.Reconcile のコメント参照）。
        var spoolDirectory = Path.Combine(dataRoot, "spool");
        await ExternalSpoolUsageProbe.WaitForDrainCompletionAsync(spoolDirectory, TimeSpan.FromMinutes(2)).ConfigureAwait(false);

        // Q2 の消費・書き込みが完全に落ち着くまで軽く待ってから停止する（グレースフル停止
        // 自体が停止手順内で Q2 残量を drain するため必須ではないが、停止直前まで大量の
        // 書き込みが飛んでいる状態を避けて突合を安定させる）。
        await Task.Delay(MetadataSettleMargin).ConfigureAwait(false);

        // OS UDP 破棄差分は子プロセスを止める前に読む（子プロセス終了後もシステム全体統計
        // 自体は残るが、突合の対象時間窓を「送出開始（baseline 取得時点）〜検証時点」に
        // 揃えるため、停止前後どちらで読んでも値は同じになる——システム全体のカウンタで
        // プロセスの生死に依存しないため）。
        var osUdpCurrent = OsUdpStatsProbe.GetCurrentTotalDiscarded();
        var osUdpDelta = Math.Max(0, osUdpCurrent - osUdpBaselineDiscarded);

        await host.StopGracefullyAsync().ConfigureAwait(false);

        var counters = CounterReconciler.ReadCounters(dataRoot);

        long savedCount;
        if (!string.IsNullOrWhiteSpace(sqlServerConnectionString))
        {
            // 実行前の既存件数(savedBaseline)との差分で「本実行で保存された件数」を数える
            // (SQL Server は実行間で同一データベースを使い回すため。RunProviderWriteCeilingAsync
            // のコメント参照)。
            savedCount = await LogStoreProbe.GetSqlServerRecordCountAsync(sqlServerConnectionString).ConfigureAwait(false) - savedBaseline;
        }
        else
        {
            var databasePath = Path.Combine(dataRoot, "yagura.db");
            savedCount = await LogStoreProbe.GetSqliteRecordCountAsync(databasePath).ConfigureAwait(false);
        }

        var reconciliation = CounterReconciler.Reconcile(
            loadResult.SentCount,
            savedCount,
            counters,
            osUdpDelta,
            transportIsUdp: transportIsUdp);

        if (host.GracefulStopSucceeded == false)
        {
            // Kill フォールバックが発生した場合、メタデータ領域の最終カウンタ書き込み
            // （§1.3 手順 3）を経ていないため、突合結果（特に不一致時）の信頼性が下がる
            // ことを利用者に伝える（CounterReconciler のコメント参照）。
            Console.Error.WriteLine(
                "[警告] グレースフル停止（Ctrl+C）が失敗またはタイムアウトしたため Kill にフォールバックした。" +
                "直近の定期永続化以降の増分が突合に反映されていない可能性がある。");
        }

        return reconciliation;
    }

    private static ScenarioReport BuildReport(
        string scenarioName,
        string runId,
        DateTimeOffset startedAt,
        string dataRoot,
        LoadGeneratorOptions loadOptions,
        LoadGeneratorResult loadResult,
        ReconciliationResult reconciliation,
        TimeSpan elapsed,
        Dictionary<string, string> additionalMetrics,
        List<string> notes,
        IReadOnlyList<string>? hostStdout = null) =>
        new(
            scenarioName,
            runId,
            startedAt,
            EnvironmentInfo.Collect(dataRoot),
            loadOptions,
            loadResult,
            reconciliation,
            elapsed,
            additionalMetrics,
            notes,
            hostStdout ?? []);
}
