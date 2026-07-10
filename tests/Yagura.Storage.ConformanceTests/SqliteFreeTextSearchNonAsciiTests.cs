using Xunit;
using Yagura.Storage;
using Yagura.Storage.Sqlite;

namespace Yagura.Storage.ConformanceTests;

/// <summary>
/// SQLite provider の自由文検索における DB-6 一致規則の非 ASCII 保証集合（database.md §1.2・§4）。
/// Issue #146 の SQLite 側の完結——DB-9（<c>tools/Yagura.Bench QueryLatency</c>。2026-07-10 実測）で
/// アプリ定義比較関数方式の性能が対話的検索のタイムアウト予算内に収まることを確認した後、
/// <see cref="SqliteLogStore.QueryAsync"/> のアプリ定義比較関数（<c>yagura_ci_contains</c>）実装で
/// 非 ASCII を含む大文字小文字非区別を実現した——本ファイルはその正例・負例を blocking で検証する
/// （<c>tests/Yagura.Storage.ConformanceTests/SqlServerSchemaMigrationTests.cs</c> の SQL Server 側
/// 検証と同一の保証集合。両 provider で同じ規則を検証することが database.md §1.3「特定 provider の
/// 挙動を暗黙の基準にしない」の実行形）。
/// </summary>
/// <remarks>
/// SQLite は接続確認・スキップ判定が不要（常にローカルファイルとして利用可能）なため、
/// <see cref="SqlServerLogStoreConformanceTests"/>・<see cref="SqlServerSchemaMigrationTests"/> と異なり
/// <c>[SkippableFact]</c>/<c>[SkippableTheory]</c> ではなく通常の <c>[Fact]</c>/<c>[Theory]</c> を使う。
/// </remarks>
public sealed class SqliteFreeTextSearchNonAsciiTests : IAsyncLifetime
{
    private readonly List<string> _databasePaths = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var path in _databasePaths)
        {
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task QueryAsync_SearchText_NonAsciiCaseFolding_MatchesGuaranteedSet()
    {
        // 正例（blocking）: Issue #146 の再現例——保存 CAFÉ・検索 café がヒットする
        // （database.md §1.2「Issue #146 の再現例（CAFÉ/café）を含むラテン基本の大小ペアを
        // 必ず含める」）。
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

    [Theory]
    [InlineData("あいうえお受信", "アイウエオ", "かな種（ひらがな/カタカナ）")]
    [InlineData("Ａ１のセンサー", "A１のセンサー", "全角/半角")]
    [InlineData("café menu updated", "cafe", "アクセント記号")]
    public async Task QueryAsync_SearchText_DoesNotFoldBeyondCase(
        string storedMessage,
        string searchText,
        string dimension)
    {
        // 負例（blocking）: database.md §1.2 DB-6「折り畳むのは大文字小文字のみ」——かな種・
        // 全角/半角・アクセントは同一視してはならない。SqliteLogStore.RegisterFreeTextComparisonFunction
        // の doc コメントが説明する OrdinalIgnoreCase の性質どおり、これらは一致しないはず。
        var store = await CreateInitializedStoreAsync();

        await store.WriteBatchAsync(new[] { CreateRecord(DateTimeOffset.UtcNow, storedMessage) });

        var results = await store.QueryAsync(new LogQuery(Limit: 10, Timeout: TimeSpan.FromSeconds(5), SearchText: searchText));
        await store.DisposeAsync();

        Assert.True(
            results.Count == 0,
            $"{dimension} が同一視された（『{storedMessage}』が検索語『{searchText}』にヒットした）——" +
            "DB-6 の規則「折り畳むのは大文字小文字のみ」への違反。");
    }

    [Fact]
    public async Task QueryAsync_SearchText_AsciiCaseFolding_StillWorks()
    {
        // 回帰防止: 比較関数方式への切替後も ASCII の大文字小文字非区別（Issue #146 の根幹
        // 「error で ERROR/Error を拾えるか」）が壊れていないことを確認する。
        var store = await CreateInitializedStoreAsync();

        await store.WriteBatchAsync(new[]
        {
            CreateRecord(DateTimeOffset.UtcNow.AddSeconds(-1), "Connection RESET by peer"),
            CreateRecord(DateTimeOffset.UtcNow, "normal heartbeat"),
        });

        var results = await store.QueryAsync(new LogQuery(Limit: 10, Timeout: TimeSpan.FromSeconds(5), SearchText: "reset"));
        await store.DisposeAsync();

        Assert.Single(results);
        Assert.Contains("RESET", results[0].Message);
    }

    private static LogRecord CreateRecord(DateTimeOffset receivedAt, string message) =>
        new(
            ReceivedAt: receivedAt,
            SourceAddress: "10.0.0.1",
            SourcePort: 514,
            Protocol: Protocol.Udp,
            ParseStatus: ParseStatus.Parsed,
            Message: message);

    private async Task<SqliteLogStore> CreateInitializedStoreAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"yagura-nonascii-{Guid.NewGuid():N}.db");
        _databasePaths.Add(path);

        var store = new SqliteLogStore(path);
        await store.InitializeAsync();
        return store;
    }
}
