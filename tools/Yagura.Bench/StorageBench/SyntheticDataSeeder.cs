using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;

namespace Yagura.Bench.StorageBench;

/// <summary>
/// DB-9/DB-10 のストレージベンチマークが使う「スキーマ v1 形状」のデータベースへ、行数規模別の
/// 合成データを高速投入するシーダー。<b>投入経路そのものは計測対象ではない</b>（計測対象は
/// クエリレイテンシ——DB-9——または <c>InitializeAsync</c> の DDL 実行時間——DB-10）ため、
/// 本番の <c>ILogStore.WriteBatchAsync</c>（1 行ずつのパラメータ化 INSERT。database.md §1.2
/// 「部分成功の扱い」を優先する設計）ではなく、SQLite は 1 トランザクションへまとめたバッチ INSERT、
/// SQL Server は <see cref="SqlBulkCopy"/> を使い、大規模行数の投入時間そのものを短縮する。
/// </summary>
public static class SyntheticDataSeeder
{
    /// <summary>
    /// SQLite 用「スキーマ v1」DDL（Issue #145/#146 の v1→v2 移行実装前の
    /// <c>SqliteLogStore.InitializeAsync</c> が発行していた DDL の写し。単一列索引のみ・
    /// <c>SchemaMigrationHistory</c> 表なし）。
    /// </summary>
    public const string SqliteV1SchemaDdl =
        """
        CREATE TABLE LogRecords (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ReceivedAt TEXT NOT NULL,
            SourceAddress TEXT NOT NULL,
            SourcePort INTEGER NOT NULL,
            Protocol INTEGER NOT NULL,
            DeviceTimestamp TEXT NULL,
            Facility INTEGER NULL,
            Severity INTEGER NULL,
            Hostname TEXT NULL,
            AppName TEXT NULL,
            ProcId TEXT NULL,
            MsgId TEXT NULL,
            StructuredData TEXT NULL,
            Message TEXT NULL,
            Raw BLOB NULL,
            ParseStatus INTEGER NOT NULL
        );

        CREATE INDEX IX_LogRecords_ReceivedAt ON LogRecords (ReceivedAt);

        CREATE TABLE SystemEvents (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Kind TEXT NOT NULL,
            StartAt TEXT NOT NULL,
            EndAt TEXT NOT NULL,
            Approximate INTEGER NOT NULL,
            Details TEXT NULL
        );

        CREATE INDEX IX_SystemEvents_StartAt ON SystemEvents (StartAt);

        CREATE TABLE SchemaVersion (
            Id INTEGER PRIMARY KEY CHECK (Id = 1),
            Version INTEGER NOT NULL
        );

        INSERT INTO SchemaVersion (Id, Version) VALUES (1, 1);
        """;

    /// <summary>
    /// SQL Server 用「スキーマ v1」DDL（<c>tests/Yagura.Storage.ConformanceTests/SqlServerSchemaMigrationTests.cs</c>
    /// の <c>V1SchemaDdl</c> と同一内容——単一の正とするため値を重複させず、コメントで出典を明記する）。
    /// </summary>
    public const string SqlServerV1SchemaDdl =
        """
        CREATE TABLE dbo.LogRecords (
            Id BIGINT IDENTITY(1,1) PRIMARY KEY,
            ReceivedAt DATETIME2(7) NOT NULL,
            SourceAddress NVARCHAR(255) NOT NULL,
            SourcePort INT NOT NULL,
            Protocol INT NOT NULL,
            DeviceTimestamp DATETIME2(7) NULL,
            Facility INT NULL,
            Severity INT NULL,
            Hostname NVARCHAR(255) NULL,
            AppName NVARCHAR(255) NULL,
            ProcId NVARCHAR(255) NULL,
            MsgId NVARCHAR(255) NULL,
            StructuredData NVARCHAR(MAX) NULL,
            Message NVARCHAR(MAX) NULL,
            Raw VARBINARY(MAX) NULL,
            ParseStatus INT NOT NULL
        );

        CREATE INDEX IX_LogRecords_ReceivedAt ON dbo.LogRecords (ReceivedAt);

        CREATE TABLE dbo.SystemEvents (
            Id BIGINT IDENTITY(1,1) PRIMARY KEY,
            Kind NVARCHAR(255) NOT NULL,
            StartAt DATETIME2(7) NOT NULL,
            EndAt DATETIME2(7) NOT NULL,
            Approximate BIT NOT NULL,
            Details NVARCHAR(MAX) NULL
        );

        CREATE INDEX IX_SystemEvents_StartAt ON dbo.SystemEvents (StartAt);

        CREATE TABLE dbo.SchemaVersion (
            Id INT NOT NULL PRIMARY KEY CHECK (Id = 1),
            Version INT NOT NULL
        );

        INSERT INTO dbo.SchemaVersion (Id, Version) VALUES (1, 1);
        """;

    private const int SqliteBatchSize = 5_000;
    private const int SqlServerBatchSize = 10_000;

    /// <summary>
    /// SQLite に v1 スキーマ + 合成データを投入する。<paramref name="nonAsciiNeedleRowIndexes"/> は
    /// DB-6 保証集合（<see cref="SyntheticLogRecordFactory.NonAsciiNeedleStored"/>）を埋め込む行番号。
    /// </summary>
    public static async Task SeedSqliteV1Async(
        string databasePath,
        long rowCount,
        IReadOnlySet<long> nonAsciiNeedleRowIndexes,
        Action<long>? onProgress = null)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        // 投入速度優先（計測対象ではないシーディング区間。本番設定—— WAL・fsync 既定——とは
        // 意図的に切り離す）。
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=OFF;";
            await pragma.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using (var ddl = connection.CreateCommand())
        {
            ddl.CommandText = SqliteV1SchemaDdl;
            await ddl.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var baseline = DateTimeOffset.UtcNow.AddDays(-3);

        for (long start = 0; start < rowCount; start += SqliteBatchSize)
        {
            var end = Math.Min(start + SqliteBatchSize, rowCount);

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO LogRecords
                    (ReceivedAt, SourceAddress, SourcePort, Protocol, Facility, Severity,
                     Hostname, AppName, ProcId, Message, ParseStatus)
                VALUES
                    ($receivedAt, $sourceAddress, $sourcePort, $protocol, $facility, $severity,
                     $hostname, $appName, $procId, $message, $parseStatus);
                """;

            var receivedAt = command.Parameters.Add("$receivedAt", SqliteType.Text);
            var sourceAddress = command.Parameters.Add("$sourceAddress", SqliteType.Text);
            var sourcePort = command.Parameters.Add("$sourcePort", SqliteType.Integer);
            var protocol = command.Parameters.Add("$protocol", SqliteType.Integer);
            var facility = command.Parameters.Add("$facility", SqliteType.Integer);
            var severity = command.Parameters.Add("$severity", SqliteType.Integer);
            var hostname = command.Parameters.Add("$hostname", SqliteType.Text);
            var appName = command.Parameters.Add("$appName", SqliteType.Text);
            var procId = command.Parameters.Add("$procId", SqliteType.Text);
            var message = command.Parameters.Add("$message", SqliteType.Text);
            var parseStatus = command.Parameters.Add("$parseStatus", SqliteType.Integer);

            for (var i = start; i < end; i++)
            {
                var row = SyntheticLogRecordFactory.Create(i, baseline, nonAsciiNeedleRowIndexes: nonAsciiNeedleRowIndexes);
                receivedAt.Value = row.ReceivedAt.UtcDateTime.ToString("O");
                sourceAddress.Value = row.SourceAddress;
                sourcePort.Value = row.SourcePort;
                protocol.Value = row.Protocol;
                facility.Value = row.Facility!.Value;
                severity.Value = row.Severity!.Value;
                hostname.Value = row.Hostname;
                appName.Value = row.AppName;
                procId.Value = row.ProcId;
                message.Value = row.Message;
                parseStatus.Value = row.ParseStatus;

                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await transaction.CommitAsync().ConfigureAwait(false);
            onProgress?.Invoke(end);
        }
    }

    /// <summary>
    /// SQL Server（LocalDB 既定）に v1 スキーマ + 合成データを投入する。
    /// </summary>
    public static async Task SeedSqlServerV1Async(
        string connectionString,
        long rowCount,
        IReadOnlySet<long> nonAsciiNeedleRowIndexes,
        Action<long>? onProgress = null)
    {
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var ddl = connection.CreateCommand();
            ddl.CommandText = SqlServerV1SchemaDdl;
            await ddl.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var baseline = DateTimeOffset.UtcNow.AddDays(-3);
        var table = BuildStagingTable();

        await using var bulkConnection = new SqlConnection(connectionString);
        await bulkConnection.OpenAsync().ConfigureAwait(false);

        using var bulkCopy = new SqlBulkCopy(bulkConnection)
        {
            DestinationTableName = "dbo.LogRecords",
            BatchSize = SqlServerBatchSize,
            BulkCopyTimeout = 0, // 無制限——大規模行数投入はシーディング区間であり計測対象ではない。
        };
        foreach (DataColumn column in table.Columns)
        {
            bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        for (long start = 0; start < rowCount; start += SqlServerBatchSize)
        {
            var end = Math.Min(start + SqlServerBatchSize, rowCount);
            table.Rows.Clear();

            for (var i = start; i < end; i++)
            {
                var row = SyntheticLogRecordFactory.Create(i, baseline, nonAsciiNeedleRowIndexes: nonAsciiNeedleRowIndexes);
                table.Rows.Add(
                    row.ReceivedAt.UtcDateTime,
                    row.SourceAddress,
                    row.SourcePort,
                    row.Protocol,
                    row.Facility!.Value,
                    row.Severity!.Value,
                    row.Hostname,
                    row.AppName,
                    row.ProcId,
                    row.Message,
                    row.ParseStatus);
            }

            await bulkCopy.WriteToServerAsync(table).ConfigureAwait(false);
            onProgress?.Invoke(end);
        }
    }

    private static DataTable BuildStagingTable()
    {
        var table = new DataTable();
        table.Columns.Add("ReceivedAt", typeof(DateTime));
        table.Columns.Add("SourceAddress", typeof(string));
        table.Columns.Add("SourcePort", typeof(int));
        table.Columns.Add("Protocol", typeof(int));
        table.Columns.Add("Facility", typeof(int));
        table.Columns.Add("Severity", typeof(int));
        table.Columns.Add("Hostname", typeof(string));
        table.Columns.Add("AppName", typeof(string));
        table.Columns.Add("ProcId", typeof(string));
        table.Columns.Add("Message", typeof(string));
        table.Columns.Add("ParseStatus", typeof(int));
        return table;
    }

    /// <summary>
    /// 指定行数のうち、DB-6 非 ASCII 保証集合の埋め込み対象とする行番号を選ぶ（等間隔サンプリング。
    /// 固定 <paramref name="count"/> 件——行数規模が変わっても検索対象件数を一定に保つ）。
    /// </summary>
    public static IReadOnlySet<long> PickNonAsciiNeedleRows(long rowCount, int count = 7)
    {
        if (rowCount <= count)
        {
            return Enumerable.Range(0, (int)rowCount).Select(i => (long)i).ToHashSet();
        }

        var step = rowCount / count;
        return Enumerable.Range(0, count).Select(i => i * step).ToHashSet();
    }
}
