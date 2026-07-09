using Microsoft.Data.SqlClient;
using Yagura.Storage;
using Yagura.Storage.SqlServer;

namespace Yagura.Storage.ConformanceTests;

/// <summary>
/// SQL Server provider のスキーマ v1 -> v2 移行の検証（Issue #145・#146・#147。
/// database.md §5.4「スキーマ移行への申し送り」・§1.2 DB-6）。
/// </summary>
/// <remarks>
/// <para>
/// <b>検証対象</b>: (1) v1 スキーマ（NVARCHAR(255) ヘッダ列・COLLATE 未指定・ReceivedAt 単一列
/// 索引のみ）で作られた既存データベースが、<see cref="SqlServerLogStore.InitializeAsync"/> 1 回で
/// v2（NVARCHAR(MAX)・COLLATE <see cref="SqlServerLogStore.SearchCollation"/>・複合索引）へ
/// 移行されること。(2) 移行が冪等であること（再実行で何もしない——database.md §5.2）。
/// (3) 既存データが移行を跨いで保全されること。(4) 適用版・適用日時が事後照会できること
/// （database.md §5.4 観測性）。(5) DB-6 の一致規則——非 ASCII を含む大文字小文字非区別の
/// 正例（CAFÉ/café）と、同一視してはならない負例（あ/ア・全角Ａ/半角A・café/cafe）の双方が
/// blocking で成立すること（database.md §1.2 の保証集合。SQL Server 側のみ——SQLite の
/// 非 ASCII は DB-9 の性能実測後に実装するため、本検証は SQLite には適用しない）。
/// </para>
/// <para>
/// <b>スキップ戦略</b>: <see cref="SqlServerLogStoreConformanceTests"/> と同一
/// （ローカルに SQL Server / LocalDB が無ければスキップ。CI ではスキップせず fail——
/// 偽装 green を防ぐ）。
/// </para>
/// </remarks>
public sealed class SqlServerSchemaMigrationTests : IAsyncLifetime
{
    private readonly List<string> _databaseNames = [];

    /// <summary>
    /// v1 スキーマの DDL（v2 移行実装前の <c>SqlServerLogStore.InitializeAsync</c> が発行していた
    /// DDL の写し。COLLATE 未指定・ヘッダ列 NVARCHAR(255)・ReceivedAt 単一列索引のみ）。
    /// </summary>
    private const string V1SchemaDdl =
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

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var databaseName in _databaseNames)
        {
            await using var masterConnection = new SqlConnection(SqlServerTestConnection.GetMasterConnectionString());
            await masterConnection.OpenAsync();

            await using var dropCommand = masterConnection.CreateCommand();
            dropCommand.CommandText =
                $"""
                IF DB_ID(N'{databaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{databaseName}];
                END;
                """;
            await dropCommand.ExecuteNonQueryAsync();
        }
    }

    // ------------------------------------------------------------------
    // v1 -> v2 移行（Issue #145 索引・#147 列長・#146 COLLATE）
    // ------------------------------------------------------------------

    [SkippableFact]
    public async Task InitializeAsync_OnV1Database_MigratesSchemaToV2()
    {
        var connectionString = await CreateV1DatabaseAsync();

        // v1 時代のデータ（移行を跨いで保全されるべき既存レコード）。
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO dbo.LogRecords
                (ReceivedAt, SourceAddress, SourcePort, Protocol, Facility, Severity,
                 Hostname, AppName, ProcId, MsgId, Message, ParseStatus)
            VALUES
                ('2026-07-01T00:00:00', N'192.0.2.1', 514, 0, 1, 5,
                 N'legacy-host', N'legacy-app', N'42', N'legacy-msg', N'pre-migration record', 0);
            """);

        var store = new SqlServerLogStore(connectionString);
        await store.InitializeAsync();
        await store.DisposeAsync();

        // (1) スキーマバージョンが現行（v2）へ更新されている。
        var version = await ExecuteScalarAsync(connectionString, "SELECT Version FROM dbo.SchemaVersion WHERE Id = 1;");
        Assert.Equal(SqlServerLogStore.CurrentSchemaVersion, Convert.ToInt32(version));

        // (2) Issue #147: ヘッダ 4 列が NVARCHAR(MAX)（sys.columns.max_length = -1）である。
        foreach (var column in new[] { "Hostname", "AppName", "ProcId", "MsgId" })
        {
            var maxLength = await ExecuteScalarAsync(
                connectionString,
                $"SELECT max_length FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.LogRecords') AND name = N'{column}';");
            Assert.Equal((short)-1, Assert.IsType<short>(maxLength));
        }

        // (3) Issue #146: 対象 NVARCHAR 列すべてに COLLATE が明示されている（database.md §5.4
        //     適用列——dbo.LogRecords の全 NVARCHAR 列 + dbo.SystemEvents の Kind・Details）。
        foreach (var (table, column) in new[]
                 {
                     ("dbo.LogRecords", "SourceAddress"),
                     ("dbo.LogRecords", "Hostname"),
                     ("dbo.LogRecords", "AppName"),
                     ("dbo.LogRecords", "ProcId"),
                     ("dbo.LogRecords", "MsgId"),
                     ("dbo.LogRecords", "StructuredData"),
                     ("dbo.LogRecords", "Message"),
                     ("dbo.SystemEvents", "Kind"),
                     ("dbo.SystemEvents", "Details"),
                 })
        {
            var collation = await ExecuteScalarAsync(
                connectionString,
                $"SELECT collation_name FROM sys.columns WHERE object_id = OBJECT_ID(N'{table}') AND name = N'{column}';");
            Assert.Equal(SqlServerLogStore.SearchCollation, collation);
        }

        // (4) Issue #145: 複合索引 3 本が存在し、包含される旧単一列索引は削除されている。
        foreach (var indexName in new[]
                 {
                     "IX_LogRecords_ReceivedAt_Id",
                     "IX_LogRecords_Severity_ReceivedAt",
                     "IX_LogRecords_SourceAddress_ReceivedAt",
                 })
        {
            Assert.True(await IndexExistsAsync(connectionString, indexName), $"索引 {indexName} が作成されていない。");
        }

        Assert.False(
            await IndexExistsAsync(connectionString, "IX_LogRecords_ReceivedAt"),
            "v1 の単一列索引 IX_LogRecords_ReceivedAt が削除されていない（複合索引に包含されるため冗長）。");

        // (5) 既存データの保全。
        var count = await ExecuteScalarAsync(connectionString, "SELECT COUNT_BIG(*) FROM dbo.LogRecords;");
        Assert.Equal(1L, count);
        var legacyHostname = await ExecuteScalarAsync(
            connectionString, "SELECT Hostname FROM dbo.LogRecords;");
        Assert.Equal("legacy-host", legacyHostname);

        // (6) 観測性（database.md §5.4）: 適用版と適用日時が事後照会できる。
        var appliedAt = await ExecuteScalarAsync(
            connectionString,
            $"SELECT AppliedAt FROM dbo.SchemaMigrationHistory WHERE Version = {SqlServerLogStore.CurrentSchemaVersion};");
        Assert.IsType<DateTime>(appliedAt);
    }

    [SkippableFact]
    public async Task InitializeAsync_OnV1Database_IsIdempotentAcrossReruns()
    {
        var connectionString = await CreateV1DatabaseAsync();

        var store = new SqlServerLogStore(connectionString);
        await store.InitializeAsync();

        // 1 回目の移行の適用日時を控え、再実行後も変わらない（= 再適用されていない）ことを確認する
        // （database.md §5.2「既に適用済みなら何もしない」「途中失敗からの再実行を安全にする」）。
        var appliedAtFirst = await ExecuteScalarAsync(
            connectionString,
            $"SELECT AppliedAt FROM dbo.SchemaMigrationHistory WHERE Version = {SqlServerLogStore.CurrentSchemaVersion};");

        var exception = await Record.ExceptionAsync(() => store.InitializeAsync());
        Assert.Null(exception);
        await store.DisposeAsync();

        var appliedAtSecond = await ExecuteScalarAsync(
            connectionString,
            $"SELECT AppliedAt FROM dbo.SchemaMigrationHistory WHERE Version = {SqlServerLogStore.CurrentSchemaVersion};");
        Assert.Equal(appliedAtFirst, appliedAtSecond);

        var historyRows = await ExecuteScalarAsync(
            connectionString, "SELECT COUNT_BIG(*) FROM dbo.SchemaMigrationHistory;");
        Assert.Equal(1L, historyRows);

        var version = await ExecuteScalarAsync(connectionString, "SELECT Version FROM dbo.SchemaVersion WHERE Id = 1;");
        Assert.Equal(SqlServerLogStore.CurrentSchemaVersion, Convert.ToInt32(version));
    }

    [SkippableFact]
    public async Task InitializeAsync_OnV1Database_ThenLongHeadersRoundTripWithoutTruncation()
    {
        // Issue #147 の症状そのもの（300 文字の Hostname が v1 では 255 文字へ黙って切り詰め）が、
        // 移行後の実データベースで解消していることの一気通貫検証。新規作成スキーマ側は
        // 適合スイート（WriteBatchAsync_HeadersLongerThan255Characters_RoundTripWithoutTruncation）が
        // 検証する——ここでは「移行された」スキーマを対象にする。
        var connectionString = await CreateV1DatabaseAsync();
        var store = new SqlServerLogStore(connectionString);
        await store.InitializeAsync();

        var longHostname = new string('h', 300);
        await store.WriteBatchAsync(new[]
        {
            new LogRecord(
                ReceivedAt: DateTimeOffset.UtcNow,
                SourceAddress: "10.0.0.1",
                SourcePort: 514,
                Protocol: Protocol.Udp,
                ParseStatus: ParseStatus.Parsed,
                Hostname: longHostname,
                Message: "post-migration long hostname"),
        });

        var results = await store.QueryLatestAsync(limit: 1, timeout: TimeSpan.FromSeconds(5));
        await store.DisposeAsync();

        Assert.Single(results);
        Assert.Equal(longHostname, results[0].Hostname);
    }

    // ------------------------------------------------------------------
    // DB-6 一致規則の非 ASCII 保証集合（database.md §1.2。SQL Server 側のみ——
    // SQLite の非 ASCII 実装は DB-9 の性能実測後）
    // ------------------------------------------------------------------

    [SkippableFact]
    public async Task QueryAsync_SearchText_NonAsciiCaseFolding_MatchesGuaranteedSet()
    {
        // 正例（blocking）: Issue #146 の再現例——保存 CAFÉ・検索 café がヒットする
        // （database.md §1.2「Issue #146 の再現例（CAFÉ/café）を含むラテン基本の大小ペアを
        // 必ず含める」）。列 COLLATE Latin1_General_100_CI_AS_KS_WS_SC の CI は ASCII に
        // 限られない（database.md §5.4）。
        var store = await CreateInitializedStoreAsync();

        var baseline = DateTimeOffset.UtcNow;
        await store.WriteBatchAsync(new[]
        {
            CreateRecord(baseline.AddSeconds(-1), "CAFÉ terminal restarted"),
            CreateRecord(baseline, "unrelated heartbeat"),
        });

        var results = await store.QueryAsync(new LogQuery(Limit: 10, Timeout: TimeSpan.FromSeconds(5), SearchText: "café"));
        await store.DisposeAsync();

        Assert.Single(results);
        Assert.Equal("CAFÉ terminal restarted", results[0].Message);
    }

    [SkippableTheory]
    [InlineData("あいうえお受信", "アイウエオ", "かな種（ひらがな/カタカナ）")]
    [InlineData("Ａ１のセンサー", "A１のセンサー", "全角/半角")]
    [InlineData("café menu updated", "cafe", "アクセント記号")]
    public async Task QueryAsync_SearchText_DoesNotFoldBeyondCase(
        string storedMessage,
        string searchText,
        string dimension)
    {
        // 負例（blocking）: database.md §1.2 DB-6「折り畳むのは大文字小文字のみ」——かな種・
        // 全角/半角・アクセントは同一視してはならない。SQL Server 照合順序の KS/WS 省略が
        // 作り込む「規則を超えた過剰な同一視」（database.md §5.4 却下案——初稿の
        // Latin1_General_100_CI_AS_SC が持っていた欠陥と同種）の回帰を機械検出する。
        var store = await CreateInitializedStoreAsync();

        await store.WriteBatchAsync(new[] { CreateRecord(DateTimeOffset.UtcNow, storedMessage) });

        var results = await store.QueryAsync(new LogQuery(Limit: 10, Timeout: TimeSpan.FromSeconds(5), SearchText: searchText));
        await store.DisposeAsync();

        Assert.True(
            results.Count == 0,
            $"{dimension} が同一視された（『{storedMessage}』が検索語『{searchText}』にヒットした）——" +
            "DB-6 の規則「折り畳むのは大文字小文字のみ」への違反。");
    }

    // ------------------------------------------------------------------
    // ヘルパ
    // ------------------------------------------------------------------

    private static LogRecord CreateRecord(DateTimeOffset receivedAt, string message) =>
        new(
            ReceivedAt: receivedAt,
            SourceAddress: "10.0.0.1",
            SourcePort: 514,
            Protocol: Protocol.Udp,
            ParseStatus: ParseStatus.Parsed,
            Message: message);

    /// <summary>
    /// v1 スキーマのデータベースを作成し、その接続文字列を返す（スキップ判定を含む）。
    /// </summary>
    private async Task<string> CreateV1DatabaseAsync()
    {
        var connectionString = await CreateEmptyDatabaseAsync();
        await ExecuteAsync(connectionString, V1SchemaDdl);
        return connectionString;
    }

    /// <summary>
    /// 空のデータベースを作成して <see cref="SqlServerLogStore.InitializeAsync"/> 済みの
    /// ストアを返す（新規作成 = v2 直行の経路）。
    /// </summary>
    private async Task<SqlServerLogStore> CreateInitializedStoreAsync()
    {
        var connectionString = await CreateEmptyDatabaseAsync();
        var store = new SqlServerLogStore(connectionString);
        await store.InitializeAsync();
        return store;
    }

    private async Task<string> CreateEmptyDatabaseAsync()
    {
        var isAvailable = SqlServerTestConnection.IsAvailable();

        if (!isAvailable && !IsRunningInCi())
        {
            Skip.If(
                true,
                $"SQL Server に接続できないためスキップします（接続先: {SqlServerTestConnection.DescribeTarget()}）。" +
                $"環境変数 {SqlServerTestConnection.ConnectionStringEnvironmentVariable} に接続文字列を設定するか、" +
                "既定の LocalDB インスタンスを利用可能にしてください。");
        }

        // CI で isAvailable が false の場合はスキップせず先へ進み、接続試行の例外で素直に fail する
        // （SqlServerLogStoreConformanceTests と同じ偽装 green 防止——doc コメント参照）。

        var databaseName = $"yagura_migration_{Guid.NewGuid():N}";
        _databaseNames.Add(databaseName);

        await using var masterConnection = new SqlConnection(SqlServerTestConnection.GetMasterConnectionString());
        await masterConnection.OpenAsync();

        await using var createCommand = masterConnection.CreateCommand();
        createCommand.CommandText = $"CREATE DATABASE [{databaseName}];";
        await createCommand.ExecuteNonQueryAsync();

        return SqlServerTestConnection.BuildConnectionString(databaseName);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ExecuteScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task<bool> IndexExistsAsync(string connectionString, string indexName)
    {
        var result = await ExecuteScalarAsync(
            connectionString,
            $"SELECT COUNT(*) FROM sys.indexes WHERE name = N'{indexName}' AND object_id = OBJECT_ID(N'dbo.LogRecords');");
        return Convert.ToInt32(result) > 0;
    }

    /// <summary>
    /// GitHub Actions の既定環境変数 <c>CI</c> で CI 実行かどうかを判定する
    /// （<see cref="SqlServerLogStoreConformanceTests"/> と同一の判定）。
    /// </summary>
    private static bool IsRunningInCi() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));
}
