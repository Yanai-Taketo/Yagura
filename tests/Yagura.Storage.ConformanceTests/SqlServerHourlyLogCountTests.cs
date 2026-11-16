using Microsoft.Data.SqlClient;
using Yagura.Storage.SqlServer;

namespace Yagura.Storage.ConformanceTests;

/// <summary>
/// SQL Server provider の optional 契約 <see cref="IHourlyLogCountReader"/>（database.md §1.2。§6.3 の時間帯ごとの照合）
/// が、SQLite provider と同じく UTC の正時ごとに数えることを確かめる。
/// </summary>
/// <remarks>
/// 既定の接続先は LocalDB（Windows 専用）のため、Windows 以外では接続文字列の環境変数が無い限りスキップする。
/// Windows で SQL Server に接続できない場合の扱いは <see cref="SqlServerLogStoreConformanceTests"/> と同じ
/// （CI ではスキップせずに失敗させ、偽装 green を防ぐ）。
/// </remarks>
public sealed class SqlServerHourlyLogCountTests
{
    [SkippableFact]
    public async Task CountByHourAsync_GroupsByUtcHour_RespectsBounds_AndOmitsEmptyHours()
    {
        Skip.IfNot(
            OperatingSystem.IsWindows()
                || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlServerTestConnection.ConnectionStringEnvironmentVariable)),
            $"既定の LocalDB は Windows 専用です（環境変数 {SqlServerTestConnection.ConnectionStringEnvironmentVariable} で接続先を指定できます）。");
        Skip.If(
            !SqlServerTestConnection.IsAvailable() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")),
            $"SQL Server に接続できないためスキップします（接続先: {SqlServerTestConnection.DescribeTarget()}）。");

        var databaseName = $"yagura_hourly_{Guid.NewGuid():N}";
        await ExecuteOnMasterAsync($"CREATE DATABASE [{databaseName}];");
        try
        {
            await using var store = new SqlServerLogStore(SqlServerTestConnection.BuildConnectionString(databaseName));
            await store.InitializeAsync();

            var hour0 = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
            await store.WriteBatchAsync(
            [
                Record(hour0, "h0-a"),
                Record(hour0.AddMinutes(59).AddSeconds(59), "h0-b"),
                Record(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.FromHours(9)), "h1"),
                Record(hour0.AddHours(3).AddMinutes(1), "h3"),
            ]);

            var all = await store.CountByHourAsync(null, null);
            Assert.Equal(
                new[] { (hour0, 2L), (hour0.AddHours(1), 1L), (hour0.AddHours(3), 1L) },
                all.Select(h => (h.HourStartUtc, h.Count)).ToArray());

            var bounded = await store.CountByHourAsync(hour0.AddMinutes(30), hour0.AddHours(3));
            Assert.Equal(
                new[] { (hour0, 1L), (hour0.AddHours(1), 1L) },
                bounded.Select(h => (h.HourStartUtc, h.Count)).ToArray());
        }
        finally
        {
            // 接続プールに残った接続があっても削除を妨げないよう、単一利用者にしてから削除する。
            await ExecuteOnMasterAsync(
                $"""
                IF DB_ID(N'{databaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{databaseName}];
                END;
                """);
        }
    }

    private static LogRecord Record(DateTimeOffset receivedAt, string message) => new(
        ReceivedAt: receivedAt,
        SourceAddress: "192.0.2.1",
        SourcePort: 514,
        Protocol: Protocol.Udp,
        ParseStatus: ParseStatus.Parsed,
        Facility: 3,
        Severity: 6,
        Message: message);

    private static async Task ExecuteOnMasterAsync(string sql)
    {
        await using var connection = new SqlConnection(SqlServerTestConnection.GetMasterConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
