using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Yagura.Storage;
using Yagura.Storage.Sqlite;

namespace Yagura.Bench.StorageBench;

/// <summary>
/// DB-9（database.md §4・§8）: SQLite 自由文検索の「ネイティブ ASCII 限定 <c>LIKE</c>」と
/// 「アプリ定義比較関数（<c>SqliteConnection.CreateFunction</c>）候補案」を行数規模別に比較する。
/// </summary>
/// <remarks>
/// <para>
/// <b>比較関数候補案の実装</b>: <c>string.Contains(needle, StringComparison.OrdinalIgnoreCase)</c>
/// を使う。.NET の <c>OrdinalIgnoreCase</c> は不変カルチャの単純大小変換テーブルに基づく比較であり
/// （Microsoft Learn "Best practices for comparing strings"・"Globalization invariant mode" —
/// 確認日 2026-07-10: Invariant Mode 下では非 ASCII の大小変換が ASCII 限定へ縮退する——
/// 裏を返せば通常モードでは ASCII に限らない大小変換が働く）、DB-6 の規則
/// 「折り畳むのは大文字小文字のみ（かな種・全角/半角・アクセントは同一視しない）」を過不足なく
/// 満たす: café/CAFÉ は同一視される一方（単純な大小ペア）、café/cafe（アクセント）・
/// あ/ア（かな種に大小の概念がない）・Ａ/A（全角/半角は別コードポイントで大小関係を持たない）は
/// いずれも一致しない。データベース側での確認は
/// <c>tests/Yagura.Storage.ConformanceTests/SqliteFreeTextSearchNonAsciiTests.cs</c> が担う——
/// 本ベンチマークは性能のみを計測する。
/// </para>
/// <para>
/// <b>索引の効き方</b>: v2 スキーマの複合索引 <c>IX_LogRecords_ReceivedAt_Id</c>
/// （<c>ReceivedAt DESC, Id DESC</c>）は本クエリの <c>ORDER BY</c> と一致するため、SQLite は
/// ソートを避けてこの索引をスキャン順に使い得る——<c>WHERE</c> 側の述語（<c>LIKE</c> か比較関数か）は
/// 索引に乗らない（中間一致は原理的に索引を使えない——Issue #145）ため、行ごとに評価しながら
/// ストリームし、<c>LIMIT</c> 件ヒットした時点で打ち切れる。一致行が少ない検索語（非 ASCII 需要語・
/// 該当なし語）はこの早期打ち切りが効きにくく、ほぼ全表を走査する——本ベンチはこの最悪系を含めて計測する。
/// </para>
/// </remarks>
public static class QueryLatencyBenchmark
{
    /// <summary>結果上限（架空値。architecture.md M-10 の仮値 = 10,000 件をそのまま使う）。</summary>
    private const int Limit = 10_000;

    /// <summary>対話的検索のタイムアウト予算（架空値。architecture.md M-10 の仮値 = 30 秒）。
    /// 合否判定はこの値を基準にするが、クエリ自体の打ち切りには使わない
    /// （<see cref="DiagnosticQueryTimeout"/> 参照——超過の実測値そのものが判断材料のため）。</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// クエリ自体の打ち切り時間（診断目的の上限。<see cref="Budget"/>（30 秒）を超えても実測を続け、
    /// 超過幅を報告する——予算超過を単に例外で握りつぶさない。90 秒は予算の 3 倍であり、
    /// これ以上待っても「予算内に収まる」という結論には至らないための実務的な上限）。
    /// </summary>
    private static readonly TimeSpan DiagnosticQueryTimeout = TimeSpan.FromSeconds(90);

    private const int WarmupTrials = 1;
    private const int MeasuredTrials = 3;

    public sealed record SearchCaseResult(
        string Label,
        string SearchTerm,
        int LikeMatchCount,
        int FunctionMatchCount,
        LatencyStats LikeLatency,
        LatencyStats FunctionLatency,
        bool LikeExceededBudget,
        bool FunctionExceededBudget);

    public sealed record RowCountResult(
        long RowCount,
        TimeSpan SeedElapsed,
        TimeSpan MigrationElapsed,
        IReadOnlyList<SearchCaseResult> Cases);

    public static async Task<IReadOnlyList<RowCountResult>> RunAsync(
        IReadOnlyList<long> rowCounts,
        string dataRoot,
        Action<string>? log = null)
    {
        var results = new List<RowCountResult>();

        foreach (var rowCount in rowCounts)
        {
            log?.Invoke($"[QueryLatency] rows={rowCount} 投入開始...");
            var dbPath = Path.Combine(dataRoot, $"query-latency-{rowCount}.db");
            var nonAsciiRows = SyntheticDataSeeder.PickNonAsciiNeedleRows(rowCount);

            var seedStopwatch = Stopwatch.StartNew();
            await SyntheticDataSeeder.SeedSqliteV1Async(dbPath, rowCount, nonAsciiRows).ConfigureAwait(false);
            seedStopwatch.Stop();
            log?.Invoke($"[QueryLatency] rows={rowCount} 投入完了 ({seedStopwatch.Elapsed})。v1→v2 移行実行...");

            var migrationStopwatch = Stopwatch.StartNew();
            await using (var store = new SqliteLogStore(dbPath))
            {
                await store.InitializeAsync().ConfigureAwait(false);
            }

            migrationStopwatch.Stop();
            log?.Invoke($"[QueryLatency] rows={rowCount} 移行完了 ({migrationStopwatch.Elapsed})。クエリ計測開始...");

            var cases = new List<SearchCaseResult>
            {
                await MeasureSearchCaseAsync(dbPath, "ASCII(中選択性)", SyntheticLogRecordFactory.AsciiNeedleSearch, log).ConfigureAwait(false),
                await MeasureSearchCaseAsync(dbPath, "非ASCII(疎な一致=保証集合正例)", SyntheticLogRecordFactory.NonAsciiNeedleSearch, log).ConfigureAwait(false),
                await MeasureSearchCaseAsync(dbPath, "該当なし(全表走査の最悪系)", "zzz-no-such-term-zzz", log).ConfigureAwait(false),
            };

            results.Add(new RowCountResult(rowCount, seedStopwatch.Elapsed, migrationStopwatch.Elapsed, cases));

            // Microsoft.Data.Sqlite は既定で接続をプールするため、Dispose 後もネイティブファイル
            // ハンドルが残り得る（SqliteLogStore.DisposeAsync と同じ理由——doc コメント参照）。
            // ClearPool は接続文字列（Mode 等の差異を含む）が完全一致する場合のみ有効なため、
            // 計測で複数の接続文字列（既定 / ReadOnly）を使った本ベンチでは ClearAllPools で
            // 確実にすべて破棄してから削除する。
            SqliteConnection.ClearAllPools();

            DeleteIfExists(dbPath);
            DeleteIfExists(dbPath + "-wal");
            DeleteIfExists(dbPath + "-shm");
        }

        return results;
    }

    private static async Task<SearchCaseResult> MeasureSearchCaseAsync(string dbPath, string label, string searchTerm, Action<string>? log)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString();

        var (likeStats, likeCount, likeExceeded) = await MeasureAsync(connectionString, searchTerm, useComparisonFunction: false, log).ConfigureAwait(false);
        var (functionStats, functionCount, functionExceeded) = await MeasureAsync(connectionString, searchTerm, useComparisonFunction: true, log).ConfigureAwait(false);

        log?.Invoke(
            $"[QueryLatency]   {label} '{searchTerm}': LIKE {likeStats} (hit={likeCount}{(likeExceeded ? ", 予算30秒超過あり" : "")}) / " +
            $"UDF {functionStats} (hit={functionCount}{(functionExceeded ? ", 予算30秒超過あり" : "")})");

        return new SearchCaseResult(label, searchTerm, likeCount, functionCount, likeStats, functionStats, likeExceeded, functionExceeded);
    }

    private static async Task<(LatencyStats Stats, int MatchCount, bool ExceededBudget)> MeasureAsync(
        string connectionString, string searchTerm, bool useComparisonFunction, Action<string>? log)
    {
        var samples = new List<TimeSpan>();
        var matchCount = 0;
        var exceededBudget = false;

        for (var trial = 0; trial < WarmupTrials + MeasuredTrials; trial++)
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            if (useComparisonFunction)
            {
                RegisterComparisonFunction(connection);
            }

            // 予算（Budget=30 秒）そのものではクエリを打ち切らない——診断目的で
            // DiagnosticQueryTimeout（90 秒）まで実測を続け、実際にどれだけ超過したかを
            // 報告する（クラス doc コメント参照）。
            using var timeoutCts = new CancellationTokenSource(DiagnosticQueryTimeout);

            await using var command = connection.CreateCommand();
            if (useComparisonFunction)
            {
                command.CommandText =
                    """
                    SELECT Id, ReceivedAt, Message
                    FROM LogRecords
                    WHERE yagura_ci_contains(Message, $needle) = 1
                    ORDER BY ReceivedAt DESC, Id DESC
                    LIMIT $limit;
                    """;
                command.Parameters.Add("$needle", SqliteType.Text).Value = searchTerm;
            }
            else
            {
                // SqliteLogStore.QueryAsync の現行 SearchText 節（ASCII 限定 LIKE）と同一の SQL 形。
                command.CommandText =
                    """
                    SELECT Id, ReceivedAt, Message
                    FROM LogRecords
                    WHERE Message LIKE $searchText ESCAPE '\'
                    ORDER BY ReceivedAt DESC, Id DESC
                    LIMIT $limit;
                    """;
                command.Parameters.Add("$searchText", SqliteType.Text).Value = "%" + EscapeLikePattern(searchTerm) + "%";
            }

            command.Parameters.Add("$limit", SqliteType.Integer).Value = Limit;

            var stopwatch = Stopwatch.StartNew();
            var rowsThisTrial = 0;
            try
            {
                await using (var reader = await command.ExecuteReaderAsync(timeoutCts.Token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(timeoutCts.Token).ConfigureAwait(false))
                    {
                        rowsThisTrial++;
                    }
                }

                stopwatch.Stop();
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                // DiagnosticQueryTimeout（90 秒）にも収まらなかった——予算（30 秒）を大幅に超過して
                // いることは既に確定しているため、これ以上は待たずに記録して次の試行へ進む。
                stopwatch.Stop();
                log?.Invoke(
                    $"[QueryLatency]     試行 {trial}（{(useComparisonFunction ? "UDF" : "LIKE")}）が診断上限 " +
                    $"{DiagnosticQueryTimeout} 以内に完了しなかった。");
                if (trial >= WarmupTrials)
                {
                    samples.Add(stopwatch.Elapsed);
                    exceededBudget = true;
                }

                continue;
            }

            if (trial >= WarmupTrials)
            {
                samples.Add(stopwatch.Elapsed);
                matchCount = rowsThisTrial;
                if (stopwatch.Elapsed > Budget)
                {
                    exceededBudget = true;
                }
            }
        }

        return (LatencyStats.From(samples), matchCount, exceededBudget);
    }

    private static void RegisterComparisonFunction(SqliteConnection connection)
    {
        // 第一候補（database.md §4）: 呼び出し経路を限定した専用関数（LIKE 演算子自体の上書きはしない）。
        // OrdinalIgnoreCase の選定根拠は本ファイルの doc コメント参照。
        connection.CreateFunction<string?, string?, bool>(
            "yagura_ci_contains",
            (text, needle) => text is not null && needle is not null && text.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string EscapeLikePattern(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is '\\' or '%' or '_')
            {
                builder.Append('\\');
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}
