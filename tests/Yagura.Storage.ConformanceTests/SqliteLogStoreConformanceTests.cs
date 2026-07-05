using Yagura.Storage;
using Yagura.Storage.Sqlite;

namespace Yagura.Storage.ConformanceTests;

/// <summary>
/// <see cref="SqliteLogStore"/> に対する適合テストスイートの実行（database.md §1.3）。
/// </summary>
/// <remarks>
/// 本クラス自体はテストロジックを持たない——<see cref="LogStoreConformanceTestBase"/> の
/// ファクトリメソッドを実装するのみで、契約 6 項目の機械検証テストはすべて基底クラスから
/// 継承される（xUnit の継承テストパターン）。
/// </remarks>
public sealed class SqliteLogStoreConformanceTests : LogStoreConformanceTestBase
{
    private string? _databasePath;

    protected override async Task<ILogStore> CreateStoreAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"yagura-conformance-{Guid.NewGuid():N}.db");
        var store = new SqliteLogStore(_databasePath);
        await store.InitializeAsync();
        return store;
    }

    protected override async Task DisposeStoreAsync(ILogStore store)
    {
        if (store is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }

        if (_databasePath is null)
        {
            return;
        }

        // WAL モードは -wal / -shm の補助ファイルも生成するため、まとめて削除する
        // （SqliteLogStoreTests と同じ後片付け——database.md §4 の運用特性）。
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
