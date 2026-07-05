using System.Buffers.Binary;

namespace Yagura.Storage.Spool;

/// <summary>
/// <see cref="SpoolRecord"/> ↔ バイト列の相互変換（architecture.md §3.2.1 の構造要件）。
/// </summary>
/// <remarks>
/// <para>
/// <b>バイトレイアウト（1 フレーム）</b>:
/// <code>
/// [Int32 LE: payloadLength]      4 バイト
/// [byte[payloadLength]: payload] payloadLength バイト
/// [UInt32 LE: crc32]             4 バイト（payload 部分のみの CRC-32）
/// </code>
/// 長さプレフィックス方式を選ぶ理由: セグメントファイルは追記専用であり、クラッシュ・
/// 強制終了で発生し得る破損は「末尾が中途半端に切れる」形（部分書き込み）に限られる
/// （追記のみで途中を書き換えないため、既存の完全なフレームが後から壊れることはない）。
/// 長さプレフィックスがあれば「次のフレームがどこまでか」を事前に知ることができ、
/// 万が一 payload の途中でファイルが終端していても、実際に読める範囲との突合で
/// 即座に「このフレームは不完全」と判定できる（区切り文字方式だと、対象データに
/// 区切り文字と同じバイト列が現れた場合の曖昧性を避けるためエスケープ処理が必要になり、
/// 実装が複雑化する）。CRC-32 は「長さは読めたが中身が torn write で破損している」
/// 稀なケース（OS のページキャッシュ書き戻し順序次第で理論上あり得る）を追加で検出する。
/// </para>
/// <para>
/// <b>payload のレイアウト</b>（<see cref="SpoolRecord"/> 1 件分。<see cref="BinaryWriter"/> /
/// <see cref="BinaryReader"/> の既定エンコーディング規則に従う——<c>Write(string)</c> は
/// 7-bit エンコード長プレフィックス + UTF-8 本体）:
/// <code>
/// byte Kind                          (SpoolRecordKind)
/// -- Kind == Normal の場合 --
/// long ReceivedAt                    (UTC Ticks)
/// string SourceAddress
/// int SourcePort
/// byte Protocol
/// byte ParseStatus
/// bool hasDeviceTimestamp, [long DeviceTimestampUtcTicks]
/// bool hasFacility, [int Facility]
/// bool hasSeverity, [int Severity]
/// bool hasHostname, [string Hostname]
/// bool hasAppName, [string AppName]
/// bool hasProcId, [string ProcId]
/// bool hasMsgId, [string MsgId]
/// bool hasStructuredData, [string StructuredData]
/// bool hasMessage, [string Message]
/// bool hasRaw, [int rawLength, byte[] raw]
/// -- Kind == SelfTest の場合 --
/// string SelfTestMarker
/// </code>
/// <see cref="LogRecord.Id"/> は書かない（provider 採番前提であり、常に <c>null</c> のまま
/// drain されるため往復対象に含めない）。
/// </para>
/// </remarks>
internal static class SpoolRecordSerializer
{
    /// <summary>
    /// 1 レコードをフレーム（長さプレフィックス + payload + CRC-32）としてシリアライズする。
    /// </summary>
    public static byte[] SerializeFrame(SpoolRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var payloadStream = new MemoryStream();
        using (var writer = new BinaryWriter(payloadStream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WritePayload(writer, record);
        }

        var payload = payloadStream.ToArray();
        var crc = Crc32.Compute(payload);

        var frame = new byte[4 + payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), payload.Length);
        payload.CopyTo(frame.AsSpan(4, payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4 + payload.Length, 4), crc);

        return frame;
    }

    private static void WritePayload(BinaryWriter writer, SpoolRecord record)
    {
        writer.Write((byte)record.Kind);

        if (record.Kind == SpoolRecordKind.SelfTest)
        {
            writer.Write(record.SelfTestMarker ?? string.Empty);
            return;
        }

        var log = record.LogRecord ?? throw new InvalidOperationException(
            "SpoolRecordKind.Normal のレコードは LogRecord を必須とする。");

        writer.Write(log.ReceivedAt.UtcDateTime.Ticks);
        writer.Write(log.SourceAddress);
        writer.Write(log.SourcePort);
        writer.Write((byte)log.Protocol);
        writer.Write((byte)log.ParseStatus);

        WriteNullable(writer, log.DeviceTimestamp, static (w, v) => w.Write(v.UtcDateTime.Ticks));
        WriteNullable(writer, log.Facility, static (w, v) => w.Write(v));
        WriteNullable(writer, log.Severity, static (w, v) => w.Write(v));
        WriteNullableString(writer, log.Hostname);
        WriteNullableString(writer, log.AppName);
        WriteNullableString(writer, log.ProcId);
        WriteNullableString(writer, log.MsgId);
        WriteNullableString(writer, log.StructuredData);
        WriteNullableString(writer, log.Message);
        WriteNullableBytes(writer, log.Raw);
    }

    private static void WriteNullable<T>(BinaryWriter writer, T? value, Action<BinaryWriter, T> writeValue)
        where T : struct
    {
        writer.Write(value.HasValue);
        if (value.HasValue)
        {
            writeValue(writer, value.Value);
        }
    }

    private static void WriteNullableString(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            writer.Write(value);
        }
    }

    private static void WriteNullableBytes(BinaryWriter writer, byte[]? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            writer.Write(value.Length);
            writer.Write(value);
        }
    }

    /// <summary>
    /// payload バイト列（フレームの長さプレフィックス・CRC を除いた部分）を
    /// <see cref="SpoolRecord"/> へ復元する。CRC 検証は呼び出し側
    /// （<see cref="SpoolSegmentReader"/>）が行う——本メソッドは正常な payload の
    /// 解釈のみを担う。
    /// </summary>
    public static SpoolRecord DeserializePayload(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        var kind = (SpoolRecordKind)reader.ReadByte();

        if (kind == SpoolRecordKind.SelfTest)
        {
            var marker = reader.ReadString();
            return SpoolRecord.ForSelfTest(marker);
        }

        var receivedAtTicks = reader.ReadInt64();
        var sourceAddress = reader.ReadString();
        var sourcePort = reader.ReadInt32();
        var protocol = (Protocol)reader.ReadByte();
        var parseStatus = (ParseStatus)reader.ReadByte();

        var deviceTimestamp = ReadNullable(reader, static r => new DateTimeOffset(r.ReadInt64(), TimeSpan.Zero));
        var facility = ReadNullable(reader, static r => r.ReadInt32());
        var severity = ReadNullable(reader, static r => r.ReadInt32());
        var hostname = ReadNullableString(reader);
        var appName = ReadNullableString(reader);
        var procId = ReadNullableString(reader);
        var msgId = ReadNullableString(reader);
        var structuredData = ReadNullableString(reader);
        var message = ReadNullableString(reader);
        var raw = ReadNullableBytes(reader);

        var logRecord = new LogRecord(
            ReceivedAt: new DateTimeOffset(receivedAtTicks, TimeSpan.Zero),
            SourceAddress: sourceAddress,
            SourcePort: sourcePort,
            Protocol: protocol,
            ParseStatus: parseStatus,
            Id: null,
            DeviceTimestamp: deviceTimestamp,
            Facility: facility,
            Severity: severity,
            Hostname: hostname,
            AppName: appName,
            ProcId: procId,
            MsgId: msgId,
            StructuredData: structuredData,
            Message: message,
            Raw: raw);

        return SpoolRecord.ForLog(logRecord);
    }

    private static T? ReadNullable<T>(BinaryReader reader, Func<BinaryReader, T> readValue)
        where T : struct
    {
        var hasValue = reader.ReadBoolean();
        return hasValue ? readValue(reader) : null;
    }

    private static string? ReadNullableString(BinaryReader reader)
    {
        var hasValue = reader.ReadBoolean();
        return hasValue ? reader.ReadString() : null;
    }

    private static byte[]? ReadNullableBytes(BinaryReader reader)
    {
        var hasValue = reader.ReadBoolean();
        if (!hasValue)
        {
            return null;
        }

        var length = reader.ReadInt32();
        return reader.ReadBytes(length);
    }
}
