using Microsoft.Data.SqlClient;

namespace Yagura.Storage.SqlServer;

public sealed partial class SqlServerLogStore
{
    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>SqlBulkCopy 採用可否の判断（本 Issue の設計判断）</b>: <c>SqlBulkCopy</c> は採用しない。
    /// 理由: (1) <c>SqlBulkCopy</c> は既定でトランザクションログへの記録を最小化する一括ロード
    /// 経路であり、行単位のエラー詳細（どの行が失敗したか）を返さない——本設計は
    /// database.md §1.2「部分成功の扱い」を明確にする必要があり、パラメータ化 INSERT を
    /// 1 トランザクションにまとめる方式の方が失敗の分類（<see cref="SqlServerFailureClassifier"/>）と
    /// 整合しやすい。(2) 保持期間削除（<see cref="DeleteOlderThanAsync"/>）が分割実行である一方、
    /// バッチ挿入の想定件数（受信段のバッチ粒度）は <c>SqlBulkCopy</c> の性能優位が効く規模
    /// （数万〜数十万行）に達しない見込みであり、複雑さに見合わない。(3) <c>SqlBulkCopy</c> は
    /// 既定でスキーマ制約（CHECK 等）を一部バイパスする設計であり、将来スキーマに制約を
    /// 追加する余地を狭める。性能上の必要性が実測で確認された場合は再評価する
    /// （M5-3 の設計判断として最終報告に明記する）。
    /// </para>
    /// <para>
    /// パラメータ化 INSERT を 1 トランザクションにまとめて実行する
    /// （<see cref="Sqlite.SqliteLogStore.WriteBatchAsync"/> と同じ方式）。
    /// </para>
    /// </remarks>
    public async Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        if (records.Count == 0)
        {
            return;
        }

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var transaction = (SqlTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO dbo.LogRecords
                    (ReceivedAt, SourceAddress, SourcePort, Protocol, DeviceTimestamp,
                     Facility, Severity, Hostname, AppName, ProcId, MsgId,
                     StructuredData, Message, Raw, ParseStatus)
                VALUES
                    (@receivedAt, @sourceAddress, @sourcePort, @protocol, @deviceTimestamp,
                     @facility, @severity, @hostname, @appName, @procId, @msgId,
                     @structuredData, @message, @raw, @parseStatus);
                """;

            var receivedAt = command.Parameters.Add("@receivedAt", System.Data.SqlDbType.DateTime2);
            var sourceAddress = command.Parameters.Add("@sourceAddress", System.Data.SqlDbType.NVarChar, 255);
            var sourcePort = command.Parameters.Add("@sourcePort", System.Data.SqlDbType.Int);
            var protocol = command.Parameters.Add("@protocol", System.Data.SqlDbType.Int);
            var deviceTimestamp = command.Parameters.Add("@deviceTimestamp", System.Data.SqlDbType.DateTime2);
            var facility = command.Parameters.Add("@facility", System.Data.SqlDbType.Int);
            var severity = command.Parameters.Add("@severity", System.Data.SqlDbType.Int);
            // Hostname/AppName/ProcId/MsgId は NVARCHAR(MAX) 列（v2 スキーマ）のため
            // パラメータの Size 指定も撤廃する（Size=255 のまま残すと、DDL 側を MAX 化しても
            // パラメータ側で 255 文字に黙って切り詰められる——ADO.NET のパラメータ長は列長と独立に
            // 効くため、両方を揃える必要がある）。
            var hostname = command.Parameters.Add("@hostname", System.Data.SqlDbType.NVarChar, -1);
            var appName = command.Parameters.Add("@appName", System.Data.SqlDbType.NVarChar, -1);
            var procId = command.Parameters.Add("@procId", System.Data.SqlDbType.NVarChar, -1);
            var msgId = command.Parameters.Add("@msgId", System.Data.SqlDbType.NVarChar, -1);
            var structuredData = command.Parameters.Add("@structuredData", System.Data.SqlDbType.NVarChar, -1);
            var message = command.Parameters.Add("@message", System.Data.SqlDbType.NVarChar, -1);
            var raw = command.Parameters.Add("@raw", System.Data.SqlDbType.VarBinary, -1);
            var parseStatus = command.Parameters.Add("@parseStatus", System.Data.SqlDbType.Int);

            foreach (var record in records)
            {
                receivedAt.Value = record.ReceivedAt.UtcDateTime;
                sourceAddress.Value = record.SourceAddress;
                sourcePort.Value = record.SourcePort;
                protocol.Value = (int)record.Protocol;
                deviceTimestamp.Value = (object?)record.DeviceTimestamp?.UtcDateTime ?? DBNull.Value;
                facility.Value = (object?)record.Facility ?? DBNull.Value;
                severity.Value = (object?)record.Severity ?? DBNull.Value;
                hostname.Value = (object?)record.Hostname ?? DBNull.Value;
                appName.Value = (object?)record.AppName ?? DBNull.Value;
                procId.Value = (object?)record.ProcId ?? DBNull.Value;
                msgId.Value = (object?)record.MsgId ?? DBNull.Value;
                structuredData.Value = (object?)record.StructuredData ?? DBNull.Value;
                message.Value = (object?)record.Message ?? DBNull.Value;
                raw.Value = (object?)record.Raw ?? DBNull.Value;
                parseStatus.Value = (int)record.ParseStatus;

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            // 統合認証フラグを渡すのは本経路のみ: 1031 の発火点（PersistenceWriter——恒久障害の
            // 抑制窓を持つ場所）が消費するのは WriteBatchAsync の失敗だけであり、閲覧系の失敗に
            // 分類を付けても読み手がいない。
            throw ex.ToLogStoreWriteException(
                $"ログレコードのバッチ書き込み ({records.Count} 件)", _integratedAuthentication);
        }
    }

    /// <inheritdoc />
    public async Task WriteSystemEventAsync(SystemEvent systemEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(systemEvent);

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO dbo.SystemEvents (Kind, StartAt, EndAt, Approximate, Details)
                VALUES (@kind, @startAt, @endAt, @approximate, @details);
                """;

            command.Parameters.Add("@kind", System.Data.SqlDbType.NVarChar, 255).Value = systemEvent.Kind;
            command.Parameters.Add("@startAt", System.Data.SqlDbType.DateTime2).Value = systemEvent.StartAt.UtcDateTime;
            command.Parameters.Add("@endAt", System.Data.SqlDbType.DateTime2).Value = systemEvent.EndAt.UtcDateTime;
            command.Parameters.Add("@approximate", System.Data.SqlDbType.Bit).Value = systemEvent.Approximate;
            command.Parameters.Add("@details", System.Data.SqlDbType.NVarChar, -1).Value = (object?)systemEvent.Details ?? DBNull.Value;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            throw ex.ToLogStoreWriteException("システムイベントの書き込み");
        }
    }
}
