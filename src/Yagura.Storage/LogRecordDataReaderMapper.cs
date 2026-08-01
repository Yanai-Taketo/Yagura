using System.Data.Common;

namespace Yagura.Storage;

/// <summary>
/// <see cref="DbDataReader"/> からログ関連レコードを組み立てる共通ヘルパ。
/// <c>Microsoft.Data.SqlClient</c> と <c>Microsoft.Data.Sqlite</c> がともに実装する
/// <see cref="DbDataReader"/> を介して provider 間で共有する（database.md §1.2）。
/// </summary>
/// <remarks>
/// 列順序は各 provider の SELECT 文で揃えてあることが前提——本ヘルパは列順序を検証しない
/// （SELECT 文のコメントに列順序を明記し、ここに合わせること）。時刻・真偽値の読み方だけが
/// provider ごとに異なる（SQL Server: ネイティブ型 / SQLite: TEXT 列からのパース、INTEGER
/// 0/1 の真偽値）ため、その 2 点だけをデリゲートで注入する。
/// </remarks>
internal static class LogRecordDataReaderMapper
{
    /// <summary>タイムスタンプ列（UTC 前提）を <see cref="DateTimeOffset"/> として読む。</summary>
    public delegate DateTimeOffset ReadTimestamp(DbDataReader reader, int ordinal);

    /// <summary>真偽値列を読む（SQLite は INTEGER 0/1、SQL Server は BIT で列の実体型が異なる）。</summary>
    public delegate bool ReadBoolean(DbDataReader reader, int ordinal);

    /// <summary>
    /// 一覧用の軽量射影を読む。列順序: Id, ReceivedAt, SourceAddress, SourcePort, Protocol,
    /// ParseStatus, DeviceTimestamp, Facility, Severity, Hostname, AppName, ProcId, MsgId,
    /// StructuredData, Message（0〜14）。QueryAsync の SELECT 列と一致させること。
    /// </summary>
    public static LogRecordSummary ReadSummary(DbDataReader reader, ReadTimestamp readTimestamp, int messageProjectionLength)
    {
        var message = reader.IsDBNull(14) ? null : reader.GetString(14);
        return new LogRecordSummary(
            Id: reader.GetInt64(0),
            ReceivedAt: readTimestamp(reader, 1),
            SourceAddress: reader.GetString(2),
            SourcePort: reader.GetInt32(3),
            Protocol: (Protocol)reader.GetInt32(4),
            ParseStatus: (ParseStatus)reader.GetInt32(5),
            DeviceTimestamp: reader.IsDBNull(6) ? null : readTimestamp(reader, 6),
            Facility: reader.IsDBNull(7) ? null : reader.GetInt32(7),
            Severity: reader.IsDBNull(8) ? null : reader.GetInt32(8),
            Hostname: reader.IsDBNull(9) ? null : reader.GetString(9),
            AppName: reader.IsDBNull(10) ? null : reader.GetString(10),
            ProcId: reader.IsDBNull(11) ? null : reader.GetString(11),
            MsgId: reader.IsDBNull(12) ? null : reader.GetString(12),
            StructuredData: reader.IsDBNull(13) ? null : reader.GetString(13),
            Message: MessageProjection.Truncate(message, messageProjectionLength));
    }

    /// <summary>
    /// 詳細表示・一括読み出し用のレコード全体を読む。列順序は <see cref="ReadSummary"/> と同じ
    /// 先頭 15 列に Raw（15）を加えたもの。
    /// </summary>
    public static LogRecord ReadRecord(DbDataReader reader, ReadTimestamp readTimestamp) =>
        new LogRecord(
            Id: reader.GetInt64(0),
            ReceivedAt: readTimestamp(reader, 1),
            SourceAddress: reader.GetString(2),
            SourcePort: reader.GetInt32(3),
            Protocol: (Protocol)reader.GetInt32(4),
            ParseStatus: (ParseStatus)reader.GetInt32(5),
            DeviceTimestamp: reader.IsDBNull(6) ? null : readTimestamp(reader, 6),
            Facility: reader.IsDBNull(7) ? null : reader.GetInt32(7),
            Severity: reader.IsDBNull(8) ? null : reader.GetInt32(8),
            Hostname: reader.IsDBNull(9) ? null : reader.GetString(9),
            AppName: reader.IsDBNull(10) ? null : reader.GetString(10),
            ProcId: reader.IsDBNull(11) ? null : reader.GetString(11),
            MsgId: reader.IsDBNull(12) ? null : reader.GetString(12),
            StructuredData: reader.IsDBNull(13) ? null : reader.GetString(13),
            Message: reader.IsDBNull(14) ? null : reader.GetString(14),
            Raw: reader.IsDBNull(15) ? null : (byte[])reader.GetValue(15));

    /// <summary>列順序: Id, Kind, StartAt, EndAt, Approximate, Details（0〜5）。</summary>
    public static SystemEvent ReadSystemEvent(DbDataReader reader, ReadTimestamp readTimestamp, ReadBoolean readBoolean) =>
        new SystemEvent(
            Id: reader.GetInt64(0),
            Kind: reader.GetString(1),
            StartAt: readTimestamp(reader, 2),
            EndAt: readTimestamp(reader, 3),
            Approximate: readBoolean(reader, 4),
            Details: reader.IsDBNull(5) ? null : reader.GetString(5));

    /// <summary>列順序: SourceAddress, LastReceivedAt, RecordCount（0〜2）。</summary>
    public static SourceActivity ReadSourceActivity(DbDataReader reader, ReadTimestamp readTimestamp) =>
        new SourceActivity(
            SourceAddress: reader.GetString(0),
            LastReceivedAt: readTimestamp(reader, 1),
            RecordCount: reader.GetInt64(2));

    /// <summary>列順序: Severity, Count（0〜1）。時刻を含まないため provider 間で読み方も同一。</summary>
    public static SeverityCount ReadSeverityCount(DbDataReader reader) =>
        new SeverityCount(
            Severity: reader.IsDBNull(0) ? null : reader.GetInt32(0),
            Count: reader.GetInt64(1));
}
