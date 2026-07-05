using Microsoft.Data.Sqlite;

namespace Yagura.Storage.Sqlite;

/// <summary>
/// <see cref="SqliteException"/> を database.md §1.2 契約 3 の 3 分類
/// （<see cref="LogStoreFailureKind"/>）へ写像する。
/// </summary>
/// <remarks>
/// <para>
/// 対応表の出典は SQLite 公式ドキュメント「Result and Error Codes」
/// （www.sqlite.org/rescode.html。確認日 2026-07-05）の primary result code 一覧。
/// <see cref="SqliteException.SqliteErrorCode"/> は SQLite の primary result code
/// （下位 8 ビット。extended result code ではない）を返す
/// （Microsoft.Data.Sqlite 公式ドキュメント "SqliteException.SqliteErrorCode" の記載どおり。
/// 確認日 2026-07-05）。
/// </para>
/// <para>
/// | primary result code | 値 | 意味（SQLite 公式) | 本分類 |
/// |---|---|---|---|
/// | SQLITE_BUSY | 5 | "The database file could not be written to because of concurrent activity by some other database connection" | Transient |
/// | SQLITE_LOCKED | 6 | "A write operation could not continue because of a conflict within the same database connection" | Transient |
/// | SQLITE_FULL | 13 | "A write to the database failed because the disk is full" | CapacityExhausted |
/// | SQLITE_PERM | 3 | "the requested access mode for a newly created database could not be provided" 相当のアクセス権拒否 | Permanent |
/// | SQLITE_READONLY | 8 | "an attempt was made to alter some data for which the current database connection does not have write permission" | Permanent |
/// | SQLITE_CORRUPT | 11 | "the database file has been corrupted" | Permanent |
/// | SQLITE_CANTOPEN | 14 | "SQLite was unable to open a file" | Permanent |
/// | SQLITE_NOTADB | 26 | "the file being opened does not appear to be an SQLite database" | Permanent |
/// | SQLITE_IOERR および派生 (10) | 10 | "some disk I/O operation failed" | Permanent（下記「IOERR の扱い」参照） |
/// </para>
/// <para>
/// <b>IOERR の扱い</b>: SQLITE_IOERR の拡張コード群（IOERR_READ・IOERR_SHORT_READ 等）には
/// 一時的なもの（ネットワークドライブの瞬断等）と恒久的なもの（不良セクタ等）が混在し、
/// primary result code だけでは区別できない。安全側（呼び出し側に「設定・環境を確認させる」
/// 方向）に倒し、本分類では Permanent として扱う——Transient に誤分類すると、恒久的な
/// I/O 障害をスプール退避の無限リトライへ押し流し、警告が強まらないまま静かに劣化し続ける
/// 害の方が大きいと判断した（独自判断。実機での発生頻度を見て再評価する）。
/// </para>
/// <para>
/// <b>上記以外（未知の result code）</b>: 安全側に倒し Permanent として扱う
/// （分類できない失敗を Transient 扱いにして無限リトライへ落とさない）。
/// </para>
/// </remarks>
internal static class SqliteFailureClassifier
{
    // SQLite 公式 "Result and Error Codes"（www.sqlite.org/rescode.html）の primary result code。
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteFull = 13;

    public static LogStoreFailureKind Classify(SqliteException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.SqliteErrorCode switch
        {
            SqliteFull => LogStoreFailureKind.CapacityExhausted,
            SqliteBusy or SqliteLocked => LogStoreFailureKind.Transient,
            _ => LogStoreFailureKind.Permanent,
        };
    }

    public static LogStoreWriteException ToLogStoreWriteException(this SqliteException exception, string operationDescription)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var failureKind = Classify(exception);
        var message = failureKind switch
        {
            LogStoreFailureKind.CapacityExhausted =>
                $"{operationDescription}: SQLite のディスク容量が枯渇しました (SQLITE_FULL)。",
            LogStoreFailureKind.Transient =>
                $"{operationDescription}: 一時的な競合により失敗しました (SqliteErrorCode={exception.SqliteErrorCode})。",
            _ =>
                $"{operationDescription}: 恒久的な障害により失敗しました (SqliteErrorCode={exception.SqliteErrorCode})。",
        };

        return new LogStoreWriteException(failureKind, message, exception);
    }
}
