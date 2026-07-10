using Microsoft.Data.Sqlite;
using Yagura.Storage;
using Yagura.Storage.Sqlite;

namespace Yagura.Storage.Tests;

/// <summary>
/// SQLite provider のスキーマ v1 -> v2 移行の検証（Issue #145。database.md §1.2 契約 1・§4）。
/// </summary>
/// <remarks>
/// <para>
/// <b>検証対象</b>: (1) v1 スキーマ（ReceivedAt 単一列索引のみ・SchemaMigrationHistory なし）で
/// 作られた既存データベースファイルが、<see cref="SqliteLogStore.InitializeAsync"/> 1 回で
/// v2（複合索引 3 本・履歴表）へ移行されること。(2) 移行が冪等であること（database.md §5.2 の
/// 冪等性要件は provider 共通——§1.2 契約 1）。(3) 既存データの保全。(4) 適用版・適用日時の
/// 事後照会（database.md §5.4 観測性の SQLite 側実体）。
/// </para>
/// <para>
/// SQLite 側は列長・COLLATE の変更を伴わない（TEXT は元々無制限。自由文検索の非 ASCII
/// 大文字小文字非区別は DB-9（2026-07-10 実測・採用確定）を経てアプリ定義比較関数
/// （クエリ実行時にのみ登録するアプリケーション層の変更）で実装した——database.md §4）ため、
/// v1 -> v2 のスキーマ DDL としての実体は索引の付け替えと履歴表の追加のみである。
/// </para>
/// </remarks>
public sealed class SqliteSchemaMigrationTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"yagura-migration-tests-{Guid.NewGuid():N}.db");

    /// <summary>
    /// v1 スキーマの DDL（v2 移行実装前の <c>SqliteLogStore.InitializeAsync</c> が発行していた
    /// DDL の写し。ReceivedAt 単一列索引のみ・SchemaMigrationHistory なし）。
    /// </summary>
    private const string V1SchemaDdl =
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

    public void Dispose()
    {
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task InitializeAsync_OnV1Database_MigratesSchemaToV2()
    {
        CreateV1Database();
        Execute(
            """
            INSERT INTO LogRecords
                (ReceivedAt, SourceAddress, SourcePort, Protocol, Facility, Severity,
                 Hostname, Message, ParseStatus)
            VALUES
                ('2026-07-01T00:00:00.0000000', '192.0.2.1', 514, 0, 1, 5,
                 'legacy-host', 'pre-migration record', 0);
            """);

        await using (var store = new SqliteLogStore(_databasePath))
        {
            await store.InitializeAsync();
        }

        // (1) スキーマバージョンが現行（v2）へ更新されている。
        Assert.Equal((long)SqliteLogStore.CurrentSchemaVersion, ExecuteScalar("SELECT Version FROM SchemaVersion WHERE Id = 1;"));

        // (2) Issue #145: 複合索引 3 本が存在し、包含される旧単一列索引は削除されている。
        var indexNames = QueryLogRecordsIndexNames();
        Assert.Contains("IX_LogRecords_ReceivedAt_Id", indexNames);
        Assert.Contains("IX_LogRecords_Severity_ReceivedAt", indexNames);
        Assert.Contains("IX_LogRecords_SourceAddress_ReceivedAt", indexNames);
        Assert.DoesNotContain("IX_LogRecords_ReceivedAt", indexNames);

        // (3) 既存データの保全。
        Assert.Equal(1L, ExecuteScalar("SELECT COUNT(*) FROM LogRecords;"));
        Assert.Equal("legacy-host", ExecuteScalar("SELECT Hostname FROM LogRecords;"));

        // (4) 観測性（database.md §5.4 の SQLite 側実体）: 適用版と適用日時が事後照会できる。
        var appliedAt = ExecuteScalar(
            $"SELECT AppliedAt FROM SchemaMigrationHistory WHERE Version = {SqliteLogStore.CurrentSchemaVersion};");
        var parsed = DateTimeOffset.Parse(
            Assert.IsType<string>(appliedAt),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.True(parsed > DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task InitializeAsync_OnV1Database_IsIdempotentAcrossReruns()
    {
        CreateV1Database();

        await using var store = new SqliteLogStore(_databasePath);
        await store.InitializeAsync();

        var appliedAtFirst = ExecuteScalar(
            $"SELECT AppliedAt FROM SchemaMigrationHistory WHERE Version = {SqliteLogStore.CurrentSchemaVersion};");

        var exception = await Record.ExceptionAsync(() => store.InitializeAsync());
        Assert.Null(exception);

        // 再実行しても履歴は増えず、適用日時も上書きされない（= 再適用されていない。
        // database.md §5.2「既に適用済みなら何もしない」）。
        var appliedAtSecond = ExecuteScalar(
            $"SELECT AppliedAt FROM SchemaMigrationHistory WHERE Version = {SqliteLogStore.CurrentSchemaVersion};");
        Assert.Equal(appliedAtFirst, appliedAtSecond);
        Assert.Equal(1L, ExecuteScalar("SELECT COUNT(*) FROM SchemaMigrationHistory;"));
        Assert.Equal((long)SqliteLogStore.CurrentSchemaVersion, ExecuteScalar("SELECT Version FROM SchemaVersion WHERE Id = 1;"));
    }

    [Fact]
    public async Task InitializeAsync_OnV1Database_MigratedStoreServesQueries()
    {
        // 移行直後のストアが通常の書き込み・絞り込み検索・送信元別集計（いずれも v2 索引の
        // 対象クエリ——Issue #145）をそのまま処理できることの一気通貫確認。
        CreateV1Database();

        await using var store = new SqliteLogStore(_databasePath);
        await store.InitializeAsync();

        var baseline = DateTimeOffset.UtcNow;
        await store.WriteBatchAsync(new[]
        {
            new LogRecord(baseline.AddSeconds(-2), "10.0.0.1", 514, Protocol.Udp, ParseStatus.Parsed, Severity: 3, Message: "critical disk"),
            new LogRecord(baseline.AddSeconds(-1), "10.0.0.2", 514, Protocol.Udp, ParseStatus.Parsed, Severity: 6, Message: "info heartbeat"),
        });

        var severityFiltered = await store.QueryAsync(
            new LogQuery(Limit: 10, Timeout: TimeSpan.FromSeconds(5), SeverityAtMost: 3));
        Assert.Single(severityFiltered);
        Assert.Equal("critical disk", severityFiltered[0].Message);

        var sourceFiltered = await store.QueryAsync(
            new LogQuery(Limit: 10, Timeout: TimeSpan.FromSeconds(5), SourceAddress: "10.0.0.2"));
        Assert.Single(sourceFiltered);

        var activity = await store.QuerySourceActivityAsync(limit: 10, timeout: TimeSpan.FromSeconds(5));
        Assert.Equal(2, activity.Count);
    }

    [Fact]
    public async Task InitializeAsync_FreshDatabase_CreatesV2IndexesAndHistory()
    {
        // 新規作成（v1 を経ない）経路でも v2 の索引集合と履歴が揃うこと。
        await using (var store = new SqliteLogStore(_databasePath))
        {
            await store.InitializeAsync();
        }

        var indexNames = QueryLogRecordsIndexNames();
        Assert.Contains("IX_LogRecords_ReceivedAt_Id", indexNames);
        Assert.Contains("IX_LogRecords_Severity_ReceivedAt", indexNames);
        Assert.Contains("IX_LogRecords_SourceAddress_ReceivedAt", indexNames);
        Assert.DoesNotContain("IX_LogRecords_ReceivedAt", indexNames);

        Assert.Equal((long)SqliteLogStore.CurrentSchemaVersion, ExecuteScalar("SELECT Version FROM SchemaVersion WHERE Id = 1;"));
        Assert.Equal(1L, ExecuteScalar(
            $"SELECT COUNT(*) FROM SchemaMigrationHistory WHERE Version = {SqliteLogStore.CurrentSchemaVersion};"));
    }

    // ------------------------------------------------------------------
    // ヘルパ
    // ------------------------------------------------------------------

    private void CreateV1Database() => Execute(V1SchemaDdl);

    private void Execute(string sql)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private object? ExecuteScalar(string sql)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private IReadOnlyList<string> QueryLogRecordsIndexNames()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'LogRecords' AND name NOT LIKE 'sqlite_%';";
        using var reader = command.ExecuteReader();

        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private SqliteConnection OpenConnection()
    {
        // Pooling 無効: テスト後片付けのファイル削除をネイティブ接続プールに邪魔させない
        // （SqliteCapacityTests と同じ理由）。
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}
