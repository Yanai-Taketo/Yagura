using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Yagura.Storage.SqlServer;

public sealed partial class SqlServerLogStore
{
    /// <inheritdoc />
    /// <remarks>
    /// 分割実行（database.md §3）: <see cref="RetentionConstants.DeleteBatchMaxSize"/> 件ずつ
    /// <c>DELETE TOP (n)</c>（SQL Server 固有構文。SQLite の副問い合わせ形と等価な分割削除）を
    /// 繰り返し実行する。
    /// </remarks>
    public async Task<DeleteOlderThanResult> DeleteOlderThanAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        long totalDeleted = 0;
        var cutoffUtc = cutoff.UtcDateTime;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    DELETE TOP (@batchSize) FROM dbo.LogRecords
                    WHERE ReceivedAt < @cutoff;
                    """;
                command.Parameters.Add("@cutoff", System.Data.SqlDbType.DateTime2).Value = cutoffUtc;
                command.Parameters.Add("@batchSize", System.Data.SqlDbType.Int).Value = RetentionConstants.DeleteBatchMaxSize;

                var deletedInBatch = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                totalDeleted += deletedInBatch;

                if (deletedInBatch < RetentionConstants.DeleteBatchMaxSize)
                {
                    break;
                }
            }
        }
        catch (SqlException ex)
        {
            throw ex.ToLogStoreWriteException($"保持期間削除 (cutoff={cutoffUtc:O})");
        }

        return new DeleteOlderThanResult(totalDeleted, cutoff);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>SQL Server provider には「取得不能」の逃げ道を適用しない</b>（database.md §5.3）:
    /// DB サイズは常に値を返す（取得自体に失敗した場合は例外として扱い、統計取得全体を失敗させる）。
    /// </para>
    /// <para>
    /// <b>計測対象は割当ファイルサイズ</b>（<c>sys.database_files.size</c>。8-KB ページ単位。
    /// Microsoft Learn "sys.database_files (Transact-SQL)" の記載:
    /// "Current size of the file, in 8-KB pages"）。行データ・ログファイルの合計を返す。
    /// <b>DB-4 の実機検証で未確定の点</b>: 削除後にこの値が縮小するか（自動 shrink は既定で
    /// 行われないため、削除後も割当サイズは維持され続ける可能性が高い——実機確認は DB-4 に委ねる）。
    /// 使用ページ量（<c>FILEPROPERTY(name, 'SpaceUsed')</c>）との差分は「解放可能だが未解放」の
    /// 容量として別途観測できるが、v0.1 時点では割当サイズのみを <see cref="LogStoreStatistics.DatabaseSizeBytes"/>
    /// として返す（DB-4 で警告閾値・残り日数換算とあわせて設計を確定する）。
    /// </para>
    /// <para>
    /// <b>Express エディション検出</b>（database.md §5.3 の必須要件）:
    /// <c>SERVERPROPERTY('EngineEdition')</c> が <c>4</c>（Express。Microsoft Learn
    /// "SERVERPROPERTY (Transact-SQL)" の EngineEdition テーブル:
    /// "4 = Express (For Express, Express with Tools, and Express with Advanced Services)"）の
    /// 場合、<see cref="LogStoreStatistics.DatabaseSizeBytes"/> と
    /// <see cref="ExpressMaxDatabaseSizeBytes"/>（10 GB。database.md §5.3 出典 Microsoft Learn
    /// "Editions and supported features of SQL Server 2022"）から利用者が接近を判定できる。
    /// 接近警告そのもの（閾値・能動通知への配線）は architecture.md §4.6 の経路——本メソッドは
    /// 判定に必要な生データ（サイズ・上限）を提供するところまでを担う。
    /// </para>
    /// </remarks>
    public async Task<LogStoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            long recordCount;
            await using (var countCommand = connection.CreateCommand())
            {
                countCommand.CommandText = "SELECT COUNT_BIG(*) FROM dbo.LogRecords;";
                recordCount = (long)(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            }

            long databaseSizeBytes;
            await using (var sizeCommand = connection.CreateCommand())
            {
                // sys.database_files.size は 8-KB ページ単位（Microsoft Learn）。
                sizeCommand.CommandText = "SELECT SUM(CAST(size AS BIGINT)) * 8192 FROM sys.database_files;";
                var sizeResult = await sizeCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                databaseSizeBytes = sizeResult is null or DBNull ? 0 : Convert.ToInt64(sizeResult, CultureInfo.InvariantCulture);
            }

            return new LogStoreStatistics(
                RecordCount: recordCount,
                DatabaseSizeBytes: databaseSizeBytes,
                DatabaseSizeUnavailableReason: null,
                WalSizeBytes: null);
        }
        catch (SqlException ex)
        {
            throw ex.ToLogStoreWriteException("統計情報の取得");
        }
    }

    /// <summary>
    /// SQL Server Express の DB 最大サイズ（database.md §5.3。Microsoft Learn
    /// "Editions and supported features of SQL Server 2022" の記載）。
    /// </summary>
    public const long ExpressMaxDatabaseSizeBytes = 10L * 1024 * 1024 * 1024;

    /// <summary>
    /// 接続先インスタンスが SQL Server Express（Express with Tools / Express with Advanced Services
    /// を含む）かどうかを判定する（database.md §5.3 の必須要件）。
    /// </summary>
    /// <remarks>
    /// <c>SERVERPROPERTY('EngineEdition')</c> を用いる（<see cref="GetStatisticsAsync"/>
    /// のドキュメント参照）。LocalDB は SQL Server Express の一種として配布される実行形態だが、
    /// <c>EngineEdition</c> は LocalDB でも <c>4</c>（Express）を返す（LocalDB は Express の
    /// インストール不要な変種であり、エンジン自体は同一——本判定はテスト環境の LocalDB でも
    /// Express と同じ 10 GB 上限の警告対象として扱われることを意味する。これは安全側であり、
    /// LocalDB を本番相当として扱う縮退は許容できる）。
    /// </remarks>
    public async Task<bool> IsExpressEditionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT SERVERPROPERTY('EngineEdition');";
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return result is not (null or DBNull) && Convert.ToInt32(result, CultureInfo.InvariantCulture) == EngineEditionExpress;
        }
        catch (SqlException ex)
        {
            throw ex.ToLogStoreWriteException("エディション判別");
        }
    }
}
