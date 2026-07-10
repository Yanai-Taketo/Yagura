using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Yagura.Storage.SqlServer;
using Yagura.Storage.Sqlite;

namespace Yagura.Bench.StorageBench;

/// <summary>
/// DB-10（database.md §5.4・§8）: スキーマ v1→v2 移行（SQL Server は列 COLLATE 明示 + 索引再構築、
/// SQLite は索引のみ）の DDL 実行時間を行数規模別に実機計測する。SQL Server は移行中の書き込み
/// 可否（受信継続性への示唆）も probe write で観測する。
/// </summary>
public static class SchemaMigrationDdlBenchmark
{
    /// <summary>移行開始後、probe write を打ち込むまでの遅延（移行トランザクションが確実にスキーマロックを取得した後で試みるための余裕）。</summary>
    private static readonly TimeSpan ProbeStartDelay = TimeSpan.FromMilliseconds(300);

    public sealed record RowCountResult(
        string Provider,
        long RowCount,
        TimeSpan SeedElapsed,
        TimeSpan MigrationElapsed,
        TimeSpan? ProbeWriteElapsed,
        bool SchemaVersionConfirmed,
        bool RowCountPreserved);

    /// <summary>SQLite（既定）または SQL Server（<paramref name="sqlServerConnectionStringTemplate"/> 指定時）を対象に計測する。</summary>
    public static async Task<IReadOnlyList<RowCountResult>> RunAsync(
        IReadOnlyList<long> rowCounts,
        string dataRoot,
        string? sqlServerConnectionStringTemplate,
        Action<string>? log = null)
    {
        var results = new List<RowCountResult>();

        foreach (var rowCount in rowCounts)
        {
            if (sqlServerConnectionStringTemplate is not null)
            {
                results.Add(await RunSqlServerAsync(rowCount, sqlServerConnectionStringTemplate, log).ConfigureAwait(false));
            }
            else
            {
                results.Add(await RunSqliteAsync(rowCount, dataRoot, log).ConfigureAwait(false));
            }
        }

        return results;
    }

    private static async Task<RowCountResult> RunSqliteAsync(long rowCount, string dataRoot, Action<string>? log)
    {
        log?.Invoke($"[SchemaMigrationDdl/SQLite] rows={rowCount} 投入開始...");
        var dbPath = Path.Combine(dataRoot, $"ddl-{rowCount}.db");
        var nonAsciiRows = SyntheticDataSeeder.PickNonAsciiNeedleRows(rowCount);

        var seedStopwatch = Stopwatch.StartNew();
        await SyntheticDataSeeder.SeedSqliteV1Async(dbPath, rowCount, nonAsciiRows).ConfigureAwait(false);
        seedStopwatch.Stop();

        log?.Invoke($"[SchemaMigrationDdl/SQLite] rows={rowCount} 投入完了 ({seedStopwatch.Elapsed})。移行実行...");

        var migrationStopwatch = Stopwatch.StartNew();
        var store = new SqliteLogStore(dbPath);
        await store.InitializeAsync().ConfigureAwait(false);
        migrationStopwatch.Stop();
        await store.DisposeAsync().ConfigureAwait(false);

        log?.Invoke($"[SchemaMigrationDdl/SQLite] rows={rowCount} 移行完了 ({migrationStopwatch.Elapsed})。");

        // SQLite は本移行で ALTER COLUMN 相当の DDL を伴わない（database.md §4——TEXT は元々無制限、
        // COLLATE も列単位指定を持たない）ため、書き込みブロックの probe は行わない
        // （単一トランザクション内の索引 DDL のみで、WAL の読み書き非ブロック性質はそのまま——
        // SqliteLogStore の doc コメント参照）。
        var (versionOk, countOk) = await VerifySqliteAsync(dbPath, rowCount).ConfigureAwait(false);

        DeleteIfExists(dbPath);
        DeleteIfExists(dbPath + "-wal");
        DeleteIfExists(dbPath + "-shm");

        return new RowCountResult("SQLite", rowCount, seedStopwatch.Elapsed, migrationStopwatch.Elapsed, ProbeWriteElapsed: null, versionOk, countOk);
    }

    private static async Task<(bool VersionOk, bool CountOk)> VerifySqliteAsync(string dbPath, long expectedCount)
    {
        await using var store = new SqliteLogStore(dbPath);
        // InitializeAsync は冪等——再実行は「適用済みなら何もしない」のみで、検証のための追加接続として安全。
        await store.InitializeAsync().ConfigureAwait(false);
        var stats = await store.GetStatisticsAsync().ConfigureAwait(false);
        return (SqliteLogStore.CurrentSchemaVersion == 2, stats.RecordCount == expectedCount);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static async Task<RowCountResult> RunSqlServerAsync(long rowCount, string connectionStringTemplate, Action<string>? log)
    {
        var candidateName = $"yagura_ddlbench_{rowCount}_{Guid.NewGuid():N}";
        var databaseName = candidateName[..Math.Min(60, candidateName.Length)];
        var masterConnectionString = new SqlConnectionStringBuilder(connectionStringTemplate) { InitialCatalog = string.Empty }.ConnectionString;

        await using (var masterConnection = new SqlConnection(masterConnectionString))
        {
            await masterConnection.OpenAsync().ConfigureAwait(false);
            await using var createCommand = masterConnection.CreateCommand();
            createCommand.CommandText = $"CREATE DATABASE [{databaseName}];";
            await createCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var dbConnectionString = new SqlConnectionStringBuilder(connectionStringTemplate) { InitialCatalog = databaseName }.ConnectionString;

        try
        {
            log?.Invoke($"[SchemaMigrationDdl/SqlServer] rows={rowCount} 投入開始 (db={databaseName})...");
            var nonAsciiRows = SyntheticDataSeeder.PickNonAsciiNeedleRows(rowCount);

            var seedStopwatch = Stopwatch.StartNew();
            await SyntheticDataSeeder.SeedSqlServerV1Async(dbConnectionString, rowCount, nonAsciiRows).ConfigureAwait(false);
            seedStopwatch.Stop();

            log?.Invoke($"[SchemaMigrationDdl/SqlServer] rows={rowCount} 投入完了 ({seedStopwatch.Elapsed})。移行 + 書き込み probe 実行...");

            var store = new SqlServerLogStore(dbConnectionString);

            var migrationStopwatch = Stopwatch.StartNew();
            var migrationTask = Task.Run(async () =>
            {
                await store.InitializeAsync().ConfigureAwait(false);
                migrationStopwatch.Stop();
            });

            // 受信継続性の probe（database.md §5.4「受信継続性」）: 移行が単一トランザクション内の
            // ALTER COLUMN を伴う限り、スキーマロックにより同時実行の書き込みは移行完了までブロック
            // され得る——このブロック時間そのものが「移行中に到着したログをスプールが受ける」
            // 設計判断（§5.4「受信継続性」）の実測入力になる。
            await Task.Delay(ProbeStartDelay).ConfigureAwait(false);
            var probeStopwatch = Stopwatch.StartNew();
            TimeSpan? probeElapsed = null;
            try
            {
                await using var probeConnection = new SqlConnection(dbConnectionString);
                await probeConnection.OpenAsync().ConfigureAwait(false);
                await using var probeCommand = probeConnection.CreateCommand();
                probeCommand.CommandTimeout = 0; // 無制限——ブロック時間そのものを計測するため打ち切らない。
                probeCommand.CommandText =
                    """
                    INSERT INTO dbo.LogRecords
                        (ReceivedAt, SourceAddress, SourcePort, Protocol, Facility, Severity, Message, ParseStatus)
                    VALUES
                        (SYSUTCDATETIME(), N'probe', 514, 0, 1, 6, N'ddl-migration-write-probe', 0);
                    """;
                await probeCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
                probeStopwatch.Stop();
                probeElapsed = probeStopwatch.Elapsed;
            }
            catch (SqlException ex)
            {
                log?.Invoke($"[SchemaMigrationDdl/SqlServer] rows={rowCount} probe write が例外で終了: {ex.Message}");
            }

            await migrationTask.ConfigureAwait(false);

            log?.Invoke(
                $"[SchemaMigrationDdl/SqlServer] rows={rowCount} 移行完了 ({migrationStopwatch.Elapsed})。" +
                $"probe write 所要 = {(probeElapsed is { } pe ? pe.ToString() : "(失敗)")}");

            var (versionOk, countOk) = await VerifySqlServerAsync(dbConnectionString, rowCount).ConfigureAwait(false);
            await store.DisposeAsync().ConfigureAwait(false);

            return new RowCountResult("SqlServer", rowCount, seedStopwatch.Elapsed, migrationStopwatch.Elapsed, probeElapsed, versionOk, countOk);
        }
        finally
        {
            await using var masterConnection = new SqlConnection(masterConnectionString);
            await masterConnection.OpenAsync().ConfigureAwait(false);
            await using var dropCommand = masterConnection.CreateCommand();
            // 既定 30 秒では不足し得る（sp_getapplock コマンドのコメント参照——同じ教訓）。
            // 移行が失敗・中断した後の後片付けは、コミット前の変更を ROLLBACK する必要があり、
            // 大規模行数では ALTER COLUMN の巻き戻し自体が数分単位になり得る
            // （本ベンチの開発時、1000 万行規模で実際にこの巻き戻しが長時間化する事象を観測した）。
            dropCommand.CommandTimeout = 0;
            dropCommand.CommandText =
                $"""
                IF DB_ID(N'{databaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{databaseName}];
                END;
                """;
            await dropCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    private static async Task<(bool VersionOk, bool CountOk)> VerifySqlServerAsync(string connectionString, long expectedCount)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT Version FROM dbo.SchemaVersion WHERE Id = 1;";
        var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync().ConfigureAwait(false));

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT_BIG(*) FROM dbo.LogRecords;";
        var count = Convert.ToInt64(await countCommand.ExecuteScalarAsync().ConfigureAwait(false));

        // probe write が成功していれば +1 されているため、期待値との差は 0 か 1 のみを許容する。
        return (version == SqlServerLogStore.CurrentSchemaVersion, count is var c && (c == expectedCount || c == expectedCount + 1));
    }
}
