using System.Text;
using Yagura.Ingestion.Parsing;
using Yagura.Ingestion.Udp;
using Yagura.Storage;

namespace Yagura.Ingestion.Tests.Parsing;

public class MinimalSyslogParserTests
{
    private static readonly DateTimeOffset Baseline = DateTimeOffset.UtcNow;

    private static RawDatagram CreateDatagram(byte[] payload) =>
        new(
            ReceivedAt: Baseline,
            SourceAddress: "192.168.1.1",
            SourcePort: 514,
            Protocol: Protocol.Udp,
            Payload: payload);

    [Theory]
    [InlineData(0, 0, 0)] // 境界値: 最小 PRI
    [InlineData(191, 23, 7)] // 境界値: 最大 PRI
    [InlineData(34, 4, 2)] // RFC 5424 の例示値 (auth/crit)
    [InlineData(13, 1, 5)] // facility=user, severity=notice
    public void Parse_ValidPri_DecomposesFacilityAndSeverity(int priValue, int expectedFacility, int expectedSeverity)
    {
        var payload = Encoding.UTF8.GetBytes($"<{priValue}>hello world");
        var datagram = CreateDatagram(payload);

        var record = MinimalSyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal(expectedFacility, record.Facility);
        Assert.Equal(expectedSeverity, record.Severity);
        Assert.Equal("hello world", record.Message);
        Assert.Null(record.Raw);
    }

    [Fact]
    public void Parse_PriMissing_ReturnsParseFailedWithRawPreserved()
    {
        var payload = Encoding.UTF8.GetBytes("no pri here");
        var datagram = CreateDatagram(payload);

        var record = MinimalSyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.ParseFailed, record.ParseStatus);
        Assert.Null(record.Facility);
        Assert.Null(record.Severity);
        Assert.Null(record.Message);
        Assert.Equal(payload, record.Raw);
    }

    [Theory]
    [InlineData("<192>over max value")] // PRI 上限 191 を超える
    [InlineData("<>empty pri")] // 数字なし
    [InlineData("<-1>negative")] // 負号は数字でないため不正
    [InlineData("<12 no closing bracket")] // '>' が無い
    [InlineData("<abc>non numeric")] // 数字でない
    public void Parse_InvalidPri_ReturnsParseFailedWithRawPreserved(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var datagram = CreateDatagram(payload);

        var record = MinimalSyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.ParseFailed, record.ParseStatus);
        Assert.Equal(payload, record.Raw);
    }

    [Fact]
    public void Parse_NonUtf8MessageBytes_ReturnsParseFailedWithRawPreserved()
    {
        // PRI は正常だが、続くメッセージ部分に不正な UTF-8 バイト列 (単独の継続バイト) を含む。
        var priBytes = Encoding.UTF8.GetBytes("<34>");
        var invalidUtf8 = new byte[] { 0x80, 0x81, 0xFE, 0xFF };
        var payload = priBytes.Concat(invalidUtf8).ToArray();
        var datagram = CreateDatagram(payload);

        var record = MinimalSyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.ParseFailed, record.ParseStatus);
        Assert.Null(record.Facility);
        Assert.Null(record.Severity);
        Assert.Equal(payload, record.Raw);
    }

    [Fact]
    public void Parse_EmptyPayload_ReturnsParseFailedWithRawPreserved()
    {
        var payload = Array.Empty<byte>();
        var datagram = CreateDatagram(payload);

        var record = MinimalSyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.ParseFailed, record.ParseStatus);
        Assert.Equal(payload, record.Raw);
    }

    [Fact]
    public void Parse_PreservesEnvelopeFieldsRegardlessOfParseOutcome()
    {
        var payload = Encoding.UTF8.GetBytes("garbage");
        var datagram = CreateDatagram(payload);

        var record = MinimalSyslogParser.Parse(datagram);

        Assert.Equal(datagram.ReceivedAt, record.ReceivedAt);
        Assert.Equal(datagram.SourceAddress, record.SourceAddress);
        Assert.Equal(datagram.SourcePort, record.SourcePort);
        Assert.Equal(datagram.Protocol, record.Protocol);
    }

    // ------------------------------------------------------------------
    // Incomplete（database.md §2.1「不完全は解析失敗に優先する」。M4-1: TCP 切断由来）
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_IncompleteFlagSet_ReturnsIncompleteWithRawPreserved()
    {
        var payload = Encoding.UTF8.GetBytes("<34>never terminated");
        var datagram = new RawDatagram(
            ReceivedAt: Baseline,
            SourceAddress: "192.168.1.1",
            SourcePort: 514,
            Protocol: Protocol.Tcp,
            Payload: payload,
            Incomplete: true);

        var record = MinimalSyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Incomplete, record.ParseStatus);
        Assert.Equal(payload, record.Raw);
        Assert.Null(record.Message);
    }

    [Fact]
    public void Parse_IncompleteFlagSet_TakesPrecedenceOverOtherwiseValidPri()
    {
        // PRI 部だけは偶然揃っている（境界前で途切れた結果）が、Incomplete が優先される
        // ことを確認する（database.md §2.1 の排他 3 値のうち Incomplete が最優先）。
        var payload = Encoding.UTF8.GetBytes("<34>");
        var datagram = new RawDatagram(
            ReceivedAt: Baseline,
            SourceAddress: "192.168.1.1",
            SourcePort: 514,
            Protocol: Protocol.Tcp,
            Payload: payload,
            Incomplete: true);

        var record = MinimalSyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Incomplete, record.ParseStatus);
        Assert.Null(record.Facility);
        Assert.Null(record.Severity);
    }

    [Fact]
    public void Parse_IncompleteFlagNotSet_UdpDatagramBehavesAsBefore()
    {
        // RawDatagram.Incomplete の既定値 (false) が UDP 経路の挙動を変えないことの回帰確認。
        var payload = Encoding.UTF8.GetBytes("<34>hello");
        var datagram = CreateDatagram(payload);

        Assert.False(datagram.Incomplete);

        var record = MinimalSyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
    }
}
