using Microsoft.Data.Sqlite;
using Yagura.Storage.Sqlite;

namespace Yagura.Storage.Tests;

/// <summary>
/// 容量枯渇（SQLITE_FULL）まわりの決定的なテスト（database.md §1.2 契約 3・§4。M5-1）。
/// </summary>
/// <remarks>
/// <para>
/// ストア経由で SQLITE_FULL を再現する手段はない: <c>PRAGMA max_page_count</c> は
/// 接続ごとの設定であり、操作ごとに接続を開く <see cref="SqliteLogStore"/> の接続には
/// 波及しない。実ディスク満杯の再現は環境依存で CI に持ち込めない。そのため本ファイルは
/// (1) SqliteException → 3 分類のマッピング（分類器の単体テスト）と、
/// (2) 「削除がページを解放し再書き込みを可能にする」という自走復旧設計の前提
/// （エンジンレベル。単一接続 + raw SQL で max_page_count が効く形）に分けて検証する。
/// ストア一気通貫での容量枯渇 → 自走復旧の実機確認は DB-8（実ディスク満杯環境）として
/// 未検証のまま残る（database.md §8 の確定待ち一覧）。
/// </para>
/// </remarks>
public sealed class SqliteCapacityTests
{
    // ------------------------------------------------------------------
    // (1) 失敗分類器（SqliteFailureClassifier）の単体テスト
    //     result code の値と意味は SQLite 公式 "Result and Error Codes"
    //     （www.sqlite.org/rescode.html）に基づく（SqliteFailureClassifier の doc コメント参照）。
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(13, LogStoreFailureKind.CapacityExhausted)] // SQLITE_FULL
    [InlineData(5, LogStoreFailureKind.Transient)]          // SQLITE_BUSY
    [InlineData(6, LogStoreFailureKind.Transient)]          // SQLITE_LOCKED
    [InlineData(3, LogStoreFailureKind.Permanent)]          // SQLITE_PERM
    [InlineData(8, LogStoreFailureKind.Permanent)]          // SQLITE_READONLY
    [InlineData(11, LogStoreFailureKind.Permanent)]         // SQLITE_CORRUPT
    [InlineData(9999, LogStoreFailureKind.Permanent)]       // 未知コードは安全側で Permanent
    public void Classify_MapsSqliteErrorCodesToFailureKinds(int sqliteErrorCode, LogStoreFailureKind expected)
    {
        var exception = new SqliteException("test", sqliteErrorCode);

        Assert.Equal(expected, SqliteFailureClassifier.Classify(exception));
    }

    // ------------------------------------------------------------------
    // (2) エンジンレベルのページ再利用検証（自走復旧設計の前提）
    // ------------------------------------------------------------------

    [Fact]
    public void EngineLevel_DeleteFreesPagesForReuse_UnderMaxPageCount()
    {
        // 単一接続なら PRAGMA max_page_count が効く。SQLITE_FULL 発生 → DELETE →
        // 同一接続で INSERT が再度成功する（解放ページの再利用）ことを確認する。
        // これは §4「保持期間削除の前倒し実行を自走復旧として試みる」設計の
        // エンジンレベルの前提の検証であり、ストア一気通貫の検証（DB-8）ではない。
        var databasePath = Path.Combine(Path.GetTempPath(), $"yagura-capacity-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false, // 後片付けのファイル削除をプールに邪魔させない
        }.ToString();

        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            void Execute(string sql)
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }

            Execute("CREATE TABLE t (id INTEGER PRIMARY KEY, body TEXT NOT NULL);");
            Execute("PRAGMA max_page_count = 20;");

            var bigBody = new string('x', 2000);
            var fullHit = false;
            for (var i = 0; i < 1000; i++)
            {
                try
                {
                    using var insert = connection.CreateCommand();
                    insert.CommandText = "INSERT INTO t (body) VALUES ($body);";
                    insert.Parameters.AddWithValue("$body", bigBody + i);
                    insert.ExecuteNonQuery();
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 13) // SQLITE_FULL
                {
                    fullHit = true;
                    break;
                }
            }

            Assert.True(fullHit, "max_page_count = 20 で SQLITE_FULL に到達しなかった（前提が崩れている）。");

            // 削除でページを解放すると、同じ上限のまま新規挿入が成功する（ページ再利用）。
            Execute("DELETE FROM t;");

            using var reinsert = connection.CreateCommand();
            reinsert.CommandText = "INSERT INTO t (body) VALUES ($body);";
            reinsert.Parameters.AddWithValue("$body", "after-recovery");
            var affected = reinsert.ExecuteNonQuery();

            Assert.Equal(1, affected);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }
}
