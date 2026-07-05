using Yagura.Storage;
using Yagura.Storage.Sqlite;
using Yagura.Storage.SqlServer;

namespace Yagura.Bench.Verification;

/// <summary>
/// 保存件数の取得（Issue #60「保存件数は SQLite ファイルへの直接クエリ（または本体の検索
/// API）」）。<see cref="ILogStore.GetStatisticsAsync"/> は provider 契約の一部
/// （database.md §1.2 契約 6）であり、SQLite・SQL Server の両方で実装済みのためこれを使う——
/// SQLite ファイルへの生 SQL 直接クエリより、契約経由の方が provider 差し替えに対して堅牢。
/// </summary>
/// <remarks>
/// SQLite は WAL モードで読み取りが書き込みをブロックしない（<see cref="SqliteLogStore"/> の
/// doc コメント参照）ため、ベンチ実行中の本体プロセス（別プロセス）が書き込み中でも、
/// 本プロブが同じファイルを読み取り専用で開いて件数を取得できる。
/// </remarks>
public static class LogStoreProbe
{
    /// <summary>
    /// 子プロセス終了直後の一過性 I/O エラー（実機観測: SQLITE_IOERR/10。プロセス終了直後は
    /// WAL チェックポイント・ハンドル解放が完了しきっていない瞬間があり得るため）を吸収する
    /// リトライ回数・間隔。
    /// </summary>
    private const int MaxRetries = 5;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// SQLite データベースファイルの保存件数を取得する。子プロセス（Yagura.Host）の終了直後は
    /// 一過性の I/O エラーが起き得るため、少数回リトライしてから諦める。
    /// </summary>
    public static async Task<long> GetSqliteRecordCountAsync(string databasePath)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var store = new SqliteLogStore(databasePath);
                var stats = await store.GetStatisticsAsync().ConfigureAwait(false);
                return stats.RecordCount;
            }
            catch (LogStoreWriteException) when (attempt < MaxRetries)
            {
                // 子プロセス終了直後の一過性 I/O エラー（本クラスの remarks 参照）。
                // GetStatisticsAsync は失敗を LogStoreWriteException として報告する
                // （SqliteLogStore の契約。SqliteFailureClassifier 参照）。
                await Task.Delay(RetryDelay).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// SQL Server データベースの保存件数を取得する。
    /// </summary>
    public static async Task<long> GetSqlServerRecordCountAsync(string connectionString)
    {
        var store = new SqlServerLogStore(connectionString);
        var stats = await store.GetStatisticsAsync().ConfigureAwait(false);
        return stats.RecordCount;
    }
}
