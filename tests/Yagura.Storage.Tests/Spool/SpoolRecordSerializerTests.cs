using Yagura.Storage.Spool;

namespace Yagura.Storage.Tests.Spool;

/// <summary>
/// <see cref="SpoolRecordSerializer"/> のフレーム往復（architecture.md §3.2.1「レコード単位の
/// 破損検出」の前提となる、正常系の往復が全カラムを保つこと）。
/// </summary>
public class SpoolRecordSerializerTests
{
    [Fact]
    public void SerializeThenDeserialize_ParsedRecordWithAllColumns_RoundTripsExactly()
    {
        var baseline = DateTimeOffset.UtcNow;
        var original = new LogRecord(
            ReceivedAt: baseline,
            SourceAddress: "192.0.2.10",
            SourcePort: 51400,
            Protocol: Protocol.Tcp,
            ParseStatus: ParseStatus.Parsed,
            Id: null,
            DeviceTimestamp: baseline.AddSeconds(-3),
            Facility: 4,
            Severity: 2,
            Hostname: "host.example.com",
            AppName: "sshd",
            ProcId: "4321",
            MsgId: "ID47",
            StructuredData: "[exampleSDID@32473 iut=\"3\"]",
            Message: "syslog message body",
            Raw: null);

        var record = SpoolRecord.ForLog(original);
        var frame = SpoolRecordSerializer.SerializeFrame(record);

        var payload = ExtractPayload(frame);
        var restored = SpoolRecordSerializer.DeserializePayload(payload);

        Assert.Equal(SpoolRecordKind.Normal, restored.Kind);
        var restoredLog = restored.LogRecord!;

        Assert.Equal(original.ReceivedAt.UtcDateTime, restoredLog.ReceivedAt.UtcDateTime);
        Assert.Equal(original.SourceAddress, restoredLog.SourceAddress);
        Assert.Equal(original.SourcePort, restoredLog.SourcePort);
        Assert.Equal(original.Protocol, restoredLog.Protocol);
        Assert.Equal(original.ParseStatus, restoredLog.ParseStatus);
        Assert.Equal(original.DeviceTimestamp!.Value.UtcDateTime, restoredLog.DeviceTimestamp!.Value.UtcDateTime);
        Assert.Equal(original.Facility, restoredLog.Facility);
        Assert.Equal(original.Severity, restoredLog.Severity);
        Assert.Equal(original.Hostname, restoredLog.Hostname);
        Assert.Equal(original.AppName, restoredLog.AppName);
        Assert.Equal(original.ProcId, restoredLog.ProcId);
        Assert.Equal(original.MsgId, restoredLog.MsgId);
        Assert.Equal(original.StructuredData, restoredLog.StructuredData);
        Assert.Equal(original.Message, restoredLog.Message);
        Assert.Null(restoredLog.Raw);

        // Id は provider 採番前提のため往復対象に含めない（常に null で復元される）。
        Assert.Null(restoredLog.Id);
    }

    [Fact]
    public void SerializeThenDeserialize_ParseFailedRecordWithRawBytes_RoundTripsRawExactly()
    {
        var baseline = DateTimeOffset.UtcNow;
        var rawBytes = new byte[] { 0x00, 0x01, 0xFF, 0x7F, 0x80, 0x0A, 0x0D };

        var original = new LogRecord(
            ReceivedAt: baseline,
            SourceAddress: "198.51.100.7",
            SourcePort: 514,
            Protocol: Protocol.Udp,
            ParseStatus: ParseStatus.ParseFailed,
            Raw: rawBytes);

        var record = SpoolRecord.ForLog(original);
        var frame = SpoolRecordSerializer.SerializeFrame(record);
        var restored = SpoolRecordSerializer.DeserializePayload(ExtractPayload(frame));

        var restoredLog = restored.LogRecord!;
        Assert.Equal(ParseStatus.ParseFailed, restoredLog.ParseStatus);
        Assert.Equal(rawBytes, restoredLog.Raw);
        Assert.Null(restoredLog.Message);
        Assert.Null(restoredLog.Facility);
        Assert.Null(restoredLog.Severity);
    }

    [Fact]
    public void SerializeThenDeserialize_IncompleteRecordWithAllNullableColumnsNull_RoundTrips()
    {
        var original = new LogRecord(
            ReceivedAt: DateTimeOffset.UtcNow,
            SourceAddress: "203.0.113.99",
            SourcePort: 12345,
            Protocol: Protocol.Tcp,
            ParseStatus: ParseStatus.Incomplete);

        var record = SpoolRecord.ForLog(original);
        var frame = SpoolRecordSerializer.SerializeFrame(record);
        var restored = SpoolRecordSerializer.DeserializePayload(ExtractPayload(frame));

        var restoredLog = restored.LogRecord!;
        Assert.Equal(ParseStatus.Incomplete, restoredLog.ParseStatus);
        Assert.Null(restoredLog.DeviceTimestamp);
        Assert.Null(restoredLog.Facility);
        Assert.Null(restoredLog.Severity);
        Assert.Null(restoredLog.Hostname);
        Assert.Null(restoredLog.AppName);
        Assert.Null(restoredLog.ProcId);
        Assert.Null(restoredLog.MsgId);
        Assert.Null(restoredLog.StructuredData);
        Assert.Null(restoredLog.Message);
        Assert.Null(restoredLog.Raw);
    }

    [Fact]
    public void SerializeThenDeserialize_SelfTestRecord_RoundTripsMarker()
    {
        var record = SpoolRecord.ForSelfTest("self-test-marker-12345");
        var frame = SpoolRecordSerializer.SerializeFrame(record);
        var restored = SpoolRecordSerializer.DeserializePayload(ExtractPayload(frame));

        Assert.Equal(SpoolRecordKind.SelfTest, restored.Kind);
        Assert.Equal("self-test-marker-12345", restored.SelfTestMarker);
        Assert.Null(restored.LogRecord);
    }

    [Fact]
    public void SerializeFrame_CorruptedPayloadByte_FailsCrcCheck()
    {
        var original = new LogRecord(
            ReceivedAt: DateTimeOffset.UtcNow,
            SourceAddress: "10.0.0.1",
            SourcePort: 514,
            Protocol: Protocol.Udp,
            ParseStatus: ParseStatus.Parsed,
            Message: "hello");

        var frame = SpoolRecordSerializer.SerializeFrame(SpoolRecord.ForLog(original));

        // payload 部分（先頭 4 バイトの長さプレフィックスの直後）を 1 バイト破壊する。
        var corrupted = (byte[])frame.Clone();
        corrupted[4] ^= 0xFF;

        var payload = corrupted.AsSpan(4, corrupted.Length - 8).ToArray();
        var storedCrc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(corrupted.AsSpan(corrupted.Length - 4, 4));

        var actualCrc = ComputeCrc32(payload);
        Assert.NotEqual(storedCrc, actualCrc);
    }

    private static byte[] ExtractPayload(byte[] frame)
    {
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(0, 4));
        return frame.AsSpan(4, length).ToArray();
    }

    private static uint ComputeCrc32(byte[] data)
    {
        // Crc32 は internal のため、テストからは InternalsVisibleTo 経由で直接呼べる
        // （Yagura.Storage.csproj の InternalsVisibleTo Yagura.Storage.Tests 参照）。
        return Crc32Accessor.Compute(data);
    }
}

/// <summary>
/// <see cref="Crc32"/>（internal）へのテスト用アクセサ。同一アセンブリ内の internal 型を
/// 直接参照できない場合の迂回は不要（InternalsVisibleTo 済み）だが、テストファイル冒頭の
/// using 文を汚さないよう小さなラッパーに留める。
/// </summary>
internal static class Crc32Accessor
{
    public static uint Compute(byte[] data) => Crc32.Compute(data);
}
