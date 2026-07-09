using System.Text;
using Yagura.Ingestion.Parsing;
using Yagura.Ingestion.Udp;
using Yagura.Storage;

namespace Yagura.Ingestion.Tests.Parsing;

public class SyslogParserTests
{
    private static readonly DateTimeOffset Baseline = DateTimeOffset.UtcNow;
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private static RawDatagram CreateDatagram(byte[] payload, DateTimeOffset? receivedAt = null) =>
        new(
            ReceivedAt: receivedAt ?? Baseline,
            SourceAddress: "192.168.1.1",
            SourcePort: 514,
            Protocol: Protocol.Udp,
            Payload: payload);

    // ------------------------------------------------------------------
    // PRI 分解（RFC 3164 §4.1.1 / RFC 5424 §6.2.1 共通の "<" 1*3DIGIT ">"）
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0, 0, 0)] // 境界値: 最小 PRI
    [InlineData(191, 23, 7)] // 境界値: 最大 PRI
    [InlineData(34, 4, 2)] // RFC 5424 の例示値 (auth/crit)
    [InlineData(13, 1, 5)] // facility=user, severity=notice
    public void Parse_ValidPri_DecomposesFacilityAndSeverity(int priValue, int expectedFacility, int expectedSeverity)
    {
        var payload = Encoding.UTF8.GetBytes($"<{priValue}>hello world");
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

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

        var record = SyslogParser.Parse(datagram);

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

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.ParseFailed, record.ParseStatus);
        Assert.Equal(payload, record.Raw);
    }

    [Fact]
    public void Parse_NonUtf8MessageBytes_ReturnsParseFailedWithPriHeaderPreserved()
    {
        // PRI は正常だが、続くメッセージ部分に不正な UTF-8 バイト列 (単独の継続バイト) を含む。
        // "<34>" の直後は 3164 判別 (VERSION "1" + SP ではない) となるため 3164 の CONTENT として扱われ、
        // 非 UTF-8 CONTENT は ParseFailed になる。ただし PRI は CONTENT より手前で確定済みのため、
        // Facility/Severity は破棄しない（Issue #139）。
        var priBytes = Encoding.UTF8.GetBytes("<34>");
        var invalidUtf8 = new byte[] { 0x80, 0x81, 0xFE, 0xFF };
        var payload = priBytes.Concat(invalidUtf8).ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.ParseFailed, record.ParseStatus);
        Assert.Equal(4, record.Facility);
        Assert.Equal(2, record.Severity);
        Assert.Null(record.Message);
        Assert.Equal(payload, record.Raw);
    }

    [Fact]
    public void Parse_EmptyPayload_ReturnsParseFailedWithRawPreserved()
    {
        var payload = Array.Empty<byte>();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.ParseFailed, record.ParseStatus);
        Assert.Equal(payload, record.Raw);
    }

    [Fact]
    public void Parse_PreservesEnvelopeFieldsRegardlessOfParseOutcome()
    {
        var payload = Encoding.UTF8.GetBytes("garbage");
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

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

        var record = SyslogParser.Parse(datagram);

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

        var record = SyslogParser.Parse(datagram);

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

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
    }

    [Fact]
    public void Parse_IncompleteFlagSet_TakesPrecedenceOverOtherwiseValidRfc5424Header()
    {
        // RFC 5424 として完全に解析可能な HEADER であっても、Incomplete が優先される。
        var payload = Encoding.UTF8.GetBytes("<34>1 2003-10-11T22:14:15.003Z mymachine.example.com su - ID47 - hello");
        var datagram = new RawDatagram(
            ReceivedAt: Baseline,
            SourceAddress: "192.168.1.1",
            SourcePort: 514,
            Protocol: Protocol.Tcp,
            Payload: payload,
            Incomplete: true);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Incomplete, record.ParseStatus);
        Assert.Null(record.Hostname);
    }

    // ==================================================================
    // RFC 5424 §6.5 Example 1〜4（原文をそのままテストデータに使用。当日 WebFetch で確認）
    // ==================================================================

    [Fact]
    public void Parse_Rfc5424Example1_NoStructuredData_DecodesAllHeaderFields()
    {
        // <34>1 2003-10-11T22:14:15.003Z mymachine.example.com su - ID47 - BOM'su root' failed for lonvick on /dev/pts/8
        var text = "<34>1 2003-10-11T22:14:15.003Z mymachine.example.com su - ID47 - "u8.ToArray()
            .Concat(Bom)
            .Concat("'su root' failed for lonvick on /dev/pts/8"u8.ToArray())
            .ToArray();
        var datagram = CreateDatagram(text);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal(4, record.Facility);
        Assert.Equal(2, record.Severity);
        Assert.Equal(
            new DateTimeOffset(2003, 10, 11, 22, 14, 15, 3, TimeSpan.Zero),
            record.DeviceTimestamp);
        Assert.Equal("mymachine.example.com", record.Hostname);
        Assert.Equal("su", record.AppName);
        Assert.Null(record.ProcId); // NILVALUE
        Assert.Equal("ID47", record.MsgId);
        Assert.Null(record.StructuredData); // NILVALUE
        Assert.Equal("'su root' failed for lonvick on /dev/pts/8", record.Message);
        Assert.Null(record.Raw);
    }

    [Fact]
    public void Parse_Rfc5424Example2_IpHostnameAndSubSecondOffset_DecodesAllHeaderFields()
    {
        // <165>1 2003-08-24T05:14:15.000003-07:00 192.0.2.1 myproc 8710 - - %% It's time to make the do-nuts.
        var payload = "<165>1 2003-08-24T05:14:15.000003-07:00 192.0.2.1 myproc 8710 - - %% It's time to make the do-nuts."u8.ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal(20, record.Facility);
        Assert.Equal(5, record.Severity);
        Assert.Equal(
            new DateTimeOffset(2003, 8, 24, 5, 14, 15, TimeSpan.FromHours(-7)).AddTicks(30), // .000003 秒 = 30 ticks
            record.DeviceTimestamp);
        Assert.Equal("192.0.2.1", record.Hostname);
        Assert.Equal("myproc", record.AppName);
        Assert.Equal("8710", record.ProcId);
        Assert.Null(record.MsgId); // NILVALUE
        Assert.Null(record.StructuredData); // NILVALUE
        Assert.Equal("%% It's time to make the do-nuts.", record.Message);
        Assert.Null(record.Raw);
    }

    [Fact]
    public void Parse_Rfc5424Example3_WithStructuredDataAndBomMessage_DecodesAllHeaderFields()
    {
        // <165>1 2003-10-11T22:14:15.003Z mymachine.example.com evntslog - ID47 [exampleSDID@32473 iut="3" eventSource="Application" eventID="1011"] BOMAn application event log entry...
        var payload = "<165>1 2003-10-11T22:14:15.003Z mymachine.example.com evntslog - ID47 [exampleSDID@32473 iut=\"3\" eventSource=\"Application\" eventID=\"1011\"] "u8.ToArray()
            .Concat(Bom)
            .Concat("An application event log entry..."u8.ToArray())
            .ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal(20, record.Facility);
        Assert.Equal(5, record.Severity);
        Assert.Equal(
            new DateTimeOffset(2003, 10, 11, 22, 14, 15, 3, TimeSpan.Zero),
            record.DeviceTimestamp);
        Assert.Equal("mymachine.example.com", record.Hostname);
        Assert.Equal("evntslog", record.AppName);
        Assert.Null(record.ProcId); // NILVALUE
        Assert.Equal("ID47", record.MsgId);
        Assert.Equal("[exampleSDID@32473 iut=\"3\" eventSource=\"Application\" eventID=\"1011\"]", record.StructuredData);
        Assert.Equal("An application event log entry...", record.Message);
        Assert.Null(record.Raw);
    }

    [Fact]
    public void Parse_Rfc5424Example4_StructuredDataOnlyNoMsg_DecodesAllHeaderFields()
    {
        // <165>1 2003-10-11T22:14:15.003Z mymachine.example.com evntslog - ID47 [exampleSDID@32473 iut="3" eventSource="Application" eventID="1011"][examplePriority@32473 class="high"]
        var payload = "<165>1 2003-10-11T22:14:15.003Z mymachine.example.com evntslog - ID47 [exampleSDID@32473 iut=\"3\" eventSource=\"Application\" eventID=\"1011\"][examplePriority@32473 class=\"high\"]"u8.ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal(20, record.Facility);
        Assert.Equal(5, record.Severity);
        Assert.Equal("mymachine.example.com", record.Hostname);
        Assert.Equal("evntslog", record.AppName);
        Assert.Null(record.ProcId);
        Assert.Equal("ID47", record.MsgId);
        Assert.Equal(
            "[exampleSDID@32473 iut=\"3\" eventSource=\"Application\" eventID=\"1011\"][examplePriority@32473 class=\"high\"]",
            record.StructuredData);
        Assert.Null(record.Message); // MSG 省略（STRUCTURED-DATA で終端）
        Assert.Null(record.Raw);
    }

    // ------------------------------------------------------------------
    // RFC 5424: NILVALUE・TIMESTAMP・STRUCTURED-DATA の個別確認
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_Rfc5424_AllNilHeaderFields_LeavesColumnsNull()
    {
        var payload = "<34>1 - - - - - -"u8.ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Null(record.DeviceTimestamp);
        Assert.Null(record.Hostname);
        Assert.Null(record.AppName);
        Assert.Null(record.ProcId);
        Assert.Null(record.MsgId);
        Assert.Null(record.StructuredData);
        Assert.Null(record.Message); // MSG 部が無い（STRUCTURED-DATA "-" で終端）
    }

    [Fact]
    public void Parse_Rfc5424_StructuredDataWithEscapedCloseBracket_PreservesEscapeInBoundaryDetection()
    {
        // PARAM-VALUE 内の \] は SD-ELEMENT の終端 ']' ではない（RFC 5424 §6.3 エスケープ規則）。
        var payload = "<34>1 - - - - - [sdid@1 note=\"contains \\] bracket\"] message body"u8.ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal("[sdid@1 note=\"contains \\] bracket\"]", record.StructuredData);
        Assert.Equal("message body", record.Message);
    }

    [Fact]
    public void Parse_Rfc5424_StructuredDataWithMultipleElements_CapturesEntireRawSpan()
    {
        var payload = "<34>1 - - - - - [a@1 x=\"1\"][b@1 y=\"2\"] msg"u8.ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal("[a@1 x=\"1\"][b@1 y=\"2\"]", record.StructuredData);
        Assert.Equal("msg", record.Message);
    }

    [Fact]
    public void Parse_Rfc5424_UnterminatedStructuredData_ReturnsParseFailed()
    {
        var payload = "<34>1 - - - - - [sdid@1 note=\"unterminated"u8.ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.ParseFailed, record.ParseStatus);
        Assert.Equal(payload, record.Raw);
    }

    [Fact]
    public void Parse_Rfc5424_BomOnlyMessage_DecodesAsEmptyMessage()
    {
        var payload = "<34>1 - - - - - - "u8.ToArray().Concat(Bom).ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal(string.Empty, record.Message);
    }

    [Fact]
    public void Parse_Rfc5424_UnknownVersion2_FallsBackToRfc3164BestEffort()
    {
        // VERSION は ABNF 上 "1" 以外もあり得るが、本実装が解釈できるのは "1" のみ。
        // "2" は 5424 の枠組みとして認識せず、3164 の best-effort（TAG 無し）として解析する。
        var payload = "<34>2 not a valid 5424 header"u8.ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal(4, record.Facility);
        Assert.Equal(2, record.Severity);
        Assert.Null(record.Hostname);
        Assert.Equal("2 not a valid 5424 header", record.Message);
    }

    [Fact]
    public void Parse_Rfc5424_HeaderTruncatedBeforeStructuredData_ReturnsParseFailed()
    {
        // MSGID までは揃っているが STRUCTURED-DATA（少なくとも NILVALUE）が無い。
        var payload = "<34>1 2003-10-11T22:14:15.003Z mymachine.example.com su - ID47"u8.ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.ParseFailed, record.ParseStatus);
        Assert.Equal(payload, record.Raw);
    }

    [Fact]
    public void Parse_Rfc5424_MalformedTimestamp_ReturnsParseFailedWithHeaderPreserved()
    {
        // TIMESTAMP の値だけが RFC 3339 として不正（時計設定が壊れた機器等）。TIMESTAMP の
        // 変換より前に HOSTNAME・APP-NAME・PROCID・MSGID の 4 フィールドは確定済みのため
        // 破棄しない（Issue #139。DeviceTimestamp のみ未設定）。
        var payload = "<34>1 not-a-timestamp mymachine.example.com su - ID47 -"u8.ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.ParseFailed, record.ParseStatus);
        Assert.Equal(4, record.Facility);
        Assert.Equal(2, record.Severity);
        Assert.Null(record.DeviceTimestamp); // 値が不正のため未設定
        Assert.Equal("mymachine.example.com", record.Hostname);
        Assert.Equal("su", record.AppName);
        Assert.Null(record.ProcId); // NILVALUE
        Assert.Equal("ID47", record.MsgId);
        Assert.Null(record.StructuredData); // 失敗より後段のため未確定
        Assert.Null(record.Message);
        Assert.Equal(payload, record.Raw);
    }

    [Fact]
    public void Parse_Rfc5424_NonSpaceAfterStructuredData_ReturnsParseFailedWithHeaderPreserved()
    {
        // STRUCTURED-DATA の直後は SP + MSG か終端でなければならない（RFC 5424 §6.1）。
        // この構造違反の時点で HEADER と STRUCTURED-DATA は確定済みのため破棄しない
        // （Issue #139）。
        var payload = "<34>1 2003-10-11T22:14:15.003Z host app 123 ID1 [sdid@1 x=\"1\"]junk"u8.ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.ParseFailed, record.ParseStatus);
        Assert.Equal(4, record.Facility);
        Assert.Equal(2, record.Severity);
        Assert.Equal(
            new DateTimeOffset(2003, 10, 11, 22, 14, 15, 3, TimeSpan.Zero),
            record.DeviceTimestamp);
        Assert.Equal("host", record.Hostname);
        Assert.Equal("app", record.AppName);
        Assert.Equal("123", record.ProcId);
        Assert.Equal("ID1", record.MsgId);
        Assert.Equal("[sdid@1 x=\"1\"]", record.StructuredData);
        Assert.Null(record.Message);
        Assert.Equal(payload, record.Raw);
    }

    [Fact]
    public void Parse_Rfc5424_NonUtf8MessageBytes_ReturnsParseFailedWithRawPreserved()
    {
        var header = "<34>1 - - - - - - "u8.ToArray();
        var invalidUtf8 = new byte[] { 0x80, 0x81, 0xFE, 0xFF };
        var payload = header.Concat(invalidUtf8).ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.ParseFailed, record.ParseStatus);
        Assert.Equal(payload, record.Raw);
    }

    // ------------------------------------------------------------------
    // Issue #139: 非 UTF-8（CP932/Shift-JIS 等）本文でも解析済み HEADER を破棄しない
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_Rfc5424_NonUtf8MessageBytesWithFullHeader_PreservesHeaderFields()
    {
        // MSG のみが非 UTF-8（"テスト" の CP932/Shift-JIS バイト列）で、他の HEADER フィールドは
        // すべて確定済み。従来はこの Envelope が Raw のみを保持し、確定済みの HOSTNAME 等が
        // 全て失われていた（Issue #139）。
        var header = "<34>1 2003-10-11T22:14:15.003Z host app 123 ID1 - "u8.ToArray();
        var cp932Bytes = new byte[] { 0x83, 0x65, 0x83, 0x58, 0x83, 0x67 }; // "テスト" (CP932/Shift-JIS)
        var payload = header.Concat(cp932Bytes).ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.ParseFailed, record.ParseStatus);
        Assert.Equal(4, record.Facility);
        Assert.Equal(2, record.Severity);
        Assert.Equal(
            new DateTimeOffset(2003, 10, 11, 22, 14, 15, 3, TimeSpan.Zero),
            record.DeviceTimestamp);
        Assert.Equal("host", record.Hostname);
        Assert.Equal("app", record.AppName);
        Assert.Equal("123", record.ProcId);
        Assert.Equal("ID1", record.MsgId);
        Assert.Null(record.StructuredData); // NILVALUE
        Assert.Null(record.Message); // 非 UTF-8 のため安全に保持できず null
        Assert.Equal(payload, record.Raw);
    }

    [Fact]
    public void Parse_Rfc5424_NonUtf8StructuredData_PreservesHeaderFieldsButNotStructuredData()
    {
        // STRUCTURED-DATA の PARAM-NAME 部分に非 UTF-8 バイト列を含む。境界検出自体は
        // '[' ']' '"' '\\' だけを見るため成功するが、切り出した SD の UTF-8 デコードには失敗する。
        // この時点で HEADER の 5 フィールド + TIMESTAMP は確定済みのため破棄しない（Issue #139）。
        var headerPrefix = "<34>1 2003-10-11T22:14:15.003Z host app 123 ID1 [sdid@1 "u8.ToArray();
        var cp932Bytes = new byte[] { 0x83, 0x65, 0x83, 0x58, 0x83, 0x67 }; // "テスト" (CP932/Shift-JIS)
        var headerSuffix = "=\"1\"]"u8.ToArray();
        var payload = headerPrefix.Concat(cp932Bytes).Concat(headerSuffix).ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.ParseFailed, record.ParseStatus);
        Assert.Equal(4, record.Facility);
        Assert.Equal(2, record.Severity);
        Assert.Equal(
            new DateTimeOffset(2003, 10, 11, 22, 14, 15, 3, TimeSpan.Zero),
            record.DeviceTimestamp);
        Assert.Equal("host", record.Hostname);
        Assert.Equal("app", record.AppName);
        Assert.Equal("123", record.ProcId);
        Assert.Equal("ID1", record.MsgId);
        Assert.Null(record.StructuredData); // 非 UTF-8 のため安全に保持できず null
        Assert.Null(record.Message);
        Assert.Equal(payload, record.Raw);
    }

    // ==================================================================
    // RFC 3164 §5.4 の例（原文をそのままテストデータに使用）
    // ==================================================================

    [Fact]
    public void Parse_Rfc3164Example1_TagWithColonNoSpace_DecodesHeaderAndTag()
    {
        // <34>Oct 11 22:14:15 mymachine su: 'su root' failed for lonvick on /dev/pts/8
        var payload = "<34>Oct 11 22:14:15 mymachine su: 'su root' failed for lonvick on /dev/pts/8"u8.ToArray();
        var receivedAt = new DateTimeOffset(2003, 10, 15, 0, 0, 0, TimeSpan.Zero);
        var datagram = CreateDatagram(payload, receivedAt);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal(4, record.Facility);
        Assert.Equal(2, record.Severity);
        Assert.Equal(
            new DateTimeOffset(2003, 10, 11, 22, 14, 15, TimeSpan.Zero),
            record.DeviceTimestamp);
        Assert.Equal("mymachine", record.Hostname);
        Assert.Equal("su", record.AppName);
        Assert.Null(record.ProcId); // pid 無し
        Assert.Equal("'su root' failed for lonvick on /dev/pts/8", record.Message);
        Assert.Null(record.Raw);
    }

    [Fact]
    public void Parse_Rfc3164Example2_RelayedWithSingleDigitDayAndIpHostname_DecodesHeader()
    {
        // 中継後: <13>Feb  5 17:32:18 10.0.0.99 Use the BFG!
        // RFC 3164 §5.4 はこの例について「メッセージ全体が MSG の CONTENT 部分として扱われた」
        // (TAG を認識しない) と明記している。"Use" は英数字トークンだが直後が ':' でも '[' でも
        // ないため、本実装は TAG として確定させず CONTENT 全体を Message に残す（判断表 #3164-3）。
        var payload = "<13>Feb  5 17:32:18 10.0.0.99 Use the BFG!"u8.ToArray();
        var receivedAt = new DateTimeOffset(1987, 2, 10, 0, 0, 0, TimeSpan.Zero);
        var datagram = CreateDatagram(payload, receivedAt);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal(1, record.Facility);
        Assert.Equal(5, record.Severity);
        Assert.Equal(
            new DateTimeOffset(1987, 2, 5, 17, 32, 18, TimeSpan.Zero),
            record.DeviceTimestamp);
        Assert.Equal("10.0.0.99", record.Hostname);
        Assert.Null(record.AppName); // TAG は認識しない（RFC 本文の記述どおり）
        Assert.Null(record.ProcId);
        Assert.Equal("Use the BFG!", record.Message);
    }

    [Fact]
    public void Parse_Rfc3164Example3_TagWithPidAndPercentContent_DecodesHeaderAndTag()
    {
        // <165>Aug 24 05:34:00 CST 1987 mymachine myproc[10]: %% It's time to make the do-nuts. ...
        // "CST 1987" は RFC 3164 の TIMESTAMP/HOSTNAME 定義には含まれない拡張情報であり、
        // HOSTNAME フィールドは素朴な「次の SP まで」規則により "CST" を HOSTNAME として読む。
        var payload = "<165>Aug 24 05:34:00 CST 1987 mymachine myproc[10]: %% It's time to make the do-nuts. %% Ingredients: Mix=OK, Jelly=OK"u8.ToArray();
        var receivedAt = new DateTimeOffset(1987, 8, 28, 0, 0, 0, TimeSpan.Zero);
        var datagram = CreateDatagram(payload, receivedAt);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal(20, record.Facility);
        Assert.Equal(5, record.Severity);
        Assert.Equal(
            new DateTimeOffset(1987, 8, 24, 5, 34, 0, TimeSpan.Zero),
            record.DeviceTimestamp);
        Assert.Equal("CST", record.Hostname);
        Assert.StartsWith("1987 mymachine myproc[10]:", record.Message);
    }

    [Fact]
    public void Parse_Rfc3164Example4_ZeroFacilityWithNonStandardYearPrefixedTimestamp_FallsBackToBestEffort()
    {
        // <0>1990 Oct 22 10:52:01 TZ-6 scapegoat.dmz.example.org 10.1.2.3 sched[0]: That's All Folks!
        // TIMESTAMP が "1990 Oct 22" のように年から始まっており RFC 3164 の "Mmm dd hh:mm:ss" に
        // 一致しないため、TIMESTAMP 解析自体を諦め HEADER 全体を MSG 側（TAG 認識）に委ねる
        // best-effort（判断表 #3164-1）。先頭トークン "1990" は数字のみで構成され TAG の英数字条件は
        // 満たすが、直後が ':' でも '[' でもなく単なる空白のため TAG としては確定させない
        // （判断表 #3164-3）。CONTENT 全体（"1990" を含む）を Message に残す。
        var payload = "<0>1990 Oct 22 10:52:01 TZ-6 scapegoat.dmz.example.org 10.1.2.3 sched[0]: That's All Folks!"u8.ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal(0, record.Facility);
        Assert.Equal(0, record.Severity);
        Assert.Null(record.DeviceTimestamp);
        Assert.Null(record.Hostname);
        Assert.Null(record.AppName);
        Assert.StartsWith("1990 Oct 22 10:52:01 TZ-6", record.Message);
    }

    // ------------------------------------------------------------------
    // RFC 3164: TAG バリエーション
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_Rfc3164_TagWithPidAndColon_DecodesAppNameAndProcId()
    {
        var payload = "<13>Jan  1 00:00:00 host myapp[1234]: something happened"u8.ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal("myapp", record.AppName);
        Assert.Equal("1234", record.ProcId);
        Assert.Equal("something happened", record.Message);
    }

    [Fact]
    public void Parse_Rfc3164_TagWithPidNoColon_DecodesAppNameAndProcId()
    {
        var payload = "<13>Jan  1 00:00:00 host myapp[1234] something happened"u8.ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal("myapp", record.AppName);
        Assert.Equal("1234", record.ProcId);
        Assert.Equal("something happened", record.Message);
    }

    [Fact]
    public void Parse_Rfc3164_BareAlphanumericTokenWithoutPidOrColon_DoesNotRecognizeTag()
    {
        // "myapp" の直後が ':' でも '[' でもなく単なる空白——RFC 3164 §5.4 "Use the BFG!" 例と
        // 構造的に区別が付かない自由形式本文の可能性があるため、TAG として確定させない
        // （判断表 #3164-3）。CONTENT 全体（"myapp" を含む）を Message に残す safe-side。
        var payload = "<13>Jan  1 00:00:00 host myapp something happened"u8.ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Null(record.AppName);
        Assert.Null(record.ProcId);
        Assert.Equal("myapp something happened", record.Message);
    }

    [Fact]
    public void Parse_Rfc3164_TagExceeding32CharsWithPidTerminator_TruncatesTagAtMaxLength()
    {
        // RFC 3164 §4.1.3: TAG は最大 32 文字。33 文字目以降は CONTENT 側に含まれる。
        // ただし本実装は TAG を「'[' または ':' で終端される英数字列」としてのみ確定させる
        // （判断表 #3164-3）ため、32 文字で打ち切った直後に '[' が続く形で検証する。
        var longTag = new string('a', 32);
        var payload = Encoding.ASCII.GetBytes($"<13>Jan  1 00:00:00 host {longTag}[99]: msg");
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal(longTag, record.AppName);
        Assert.Equal("99", record.ProcId);
        Assert.Equal("msg", record.Message);
    }

    [Fact]
    public void Parse_Rfc3164_TagLongerThan32CharsWithNoRecognizedTerminator_LeavesAppNameNull()
    {
        // 33 文字以上の英数字列が続いた直後に ':' も '[' も現れない場合、32 文字で打ち切った
        // としても後続が英数字のままであり「TAG が明確に終端した」とは言えないため、
        // 本実装は TAG として確定させない（低頻度な境界ケースであり、確定を急がず
        // CONTENT 全体を残す safe-side の判断——判断表 #3164-3 の延長）。
        var longToken = new string('a', 40);
        var payload = Encoding.ASCII.GetBytes($"<13>Jan  1 00:00:00 host {longToken} msg");
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Null(record.AppName);
        Assert.Equal($"{longToken} msg", record.Message);
    }

    [Fact]
    public void Parse_Rfc3164_NoTagNonAlphanumericStart_LeavesAppNameNull()
    {
        var payload = "<13>Jan  1 00:00:00 host : leading colon"u8.ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Null(record.AppName);
        Assert.Equal(": leading colon", record.Message);
    }

    [Fact]
    public void Parse_Rfc3164_TimestampMissing_FallsBackToHeaderlessBestEffort()
    {
        // TIMESTAMP が ABNF に一致しない（先頭が数字ではない自由形式のメッセージ）場合、
        // HEADER 解析を諦め TAG 認識だけを試みる best-effort（判断表 #3164-1）。
        var payload = "<13>totally free-form message without a timestamp"u8.ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Null(record.DeviceTimestamp);
        Assert.Null(record.Hostname);
        Assert.Null(record.AppName); // "totally" の直後が ':' '[' どちらでもないため TAG として確定させない
        Assert.Equal("totally free-form message without a timestamp", record.Message);
    }

    [Fact]
    public void Parse_Rfc3164_NonUtf8Content_ReturnsParseFailedWithRawPreserved()
    {
        var header = "<13>Jan  1 00:00:00 host app: "u8.ToArray();
        var invalidUtf8 = new byte[] { 0x80, 0x81, 0xFE, 0xFF };
        var payload = header.Concat(invalidUtf8).ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.ParseFailed, record.ParseStatus);
        Assert.Equal(payload, record.Raw);
    }

    [Fact]
    public void Parse_Rfc3164_Cp932Content_ReturnsParseFailedWithHeaderPreserved()
    {
        // Issue #139: 日本の旧来機器（古い L2/L3 スイッチ・UPS・複合機等）は syslog 本文を
        // CP932/Shift-JIS で送ることが今なお現役。CONTENT が非 UTF-8 のため Message は安全に
        // 保持できず ParseFailed になるが、TIMESTAMP・HOSTNAME・TAG（AppName）は CONTENT より
        // 手前で確定済みのため破棄しない——従来はここも含め Raw のみが残り、特定ホストの
        // CP932 ログを絞り込む検索すら不可能だった。
        var header = "<13>Jan  1 00:00:00 host app: "u8.ToArray();
        var cp932Bytes = new byte[] { 0x83, 0x65, 0x83, 0x58, 0x83, 0x67 }; // "テスト" (CP932/Shift-JIS)
        var payload = header.Concat(cp932Bytes).ToArray();
        var receivedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var datagram = CreateDatagram(payload, receivedAt);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.ParseFailed, record.ParseStatus);
        Assert.Equal(1, record.Facility);
        Assert.Equal(5, record.Severity);
        Assert.Equal(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            record.DeviceTimestamp);
        Assert.Equal("host", record.Hostname);
        Assert.Equal("app", record.AppName);
        Assert.Null(record.Message); // 非 UTF-8 のため安全に保持できず null
        Assert.Equal(payload, record.Raw);
    }

    // ------------------------------------------------------------------
    // RFC 3164: 年跨ぎ補完（年を持たない TIMESTAMP。基準時刻は ReceivedAt を 1 回だけ取得）
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_Rfc3164_JanuaryMessageReceivedInDecember_ResolvesToNextYear()
    {
        // 12/31 に受信した「1/1」付けメッセージ（送信元との遅延・時計ずれ）は、
        // ReceivedAt に最も近い年——翌年——を選ぶ。
        var payload = "<13>Jan  1 00:00:00 host app: msg"u8.ToArray();
        var receivedAt = new DateTimeOffset(2025, 12, 31, 23, 0, 0, TimeSpan.Zero);
        var datagram = CreateDatagram(payload, receivedAt);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal(2026, record.DeviceTimestamp!.Value.Year);
    }

    [Fact]
    public void Parse_Rfc3164_DecemberMessageReceivedInJanuary_ResolvesToPreviousYear()
    {
        // 1/1 に受信した「12/31」付けメッセージは、ReceivedAt に最も近い年——前年——を選ぶ。
        var payload = "<13>Dec 31 23:59:59 host app: msg"u8.ToArray();
        var receivedAt = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);
        var datagram = CreateDatagram(payload, receivedAt);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal(2025, record.DeviceTimestamp!.Value.Year);
    }

    [Fact]
    public void Parse_Rfc3164_SameYearMessage_ResolvesToReceivedAtYear()
    {
        var payload = "<13>Jun 15 12:00:00 host app: msg"u8.ToArray();
        var receivedAt = new DateTimeOffset(2026, 6, 15, 12, 0, 5, TimeSpan.Zero);
        var datagram = CreateDatagram(payload, receivedAt);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal(2026, record.DeviceTimestamp!.Value.Year);
    }

    // ==================================================================
    // Issue #134: RFC 3164 の既定タイムゾーン（送信元付記が無い場合に適用）
    // ==================================================================

    [Fact]
    public void Parse_Rfc3164_NoDefaultTimeZoneArgument_InterpretsAsUtc()
    {
        // 既存呼び出し元との後方互換: 第 2 引数省略時は従来どおり UTC 固定。
        var payload = "<13>Jan  1 00:00:00 host app: msg"u8.ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(TimeSpan.Zero, record.DeviceTimestamp!.Value.Offset);
    }

    [Fact]
    public void Parse_Rfc3164_DefaultTimeZoneConfigured_AppliesConfiguredOffset()
    {
        // Issue #134: 既定タイムゾーンを設定すると、送信元付記の無い TIMESTAMP に適用される。
        var payload = "<13>Jan  1 09:00:00 host app: msg"u8.ToArray();
        var receivedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var datagram = CreateDatagram(payload, receivedAt);
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

        var record = SyslogParser.Parse(datagram, tokyo);

        Assert.Equal(TimeSpan.FromHours(9), record.DeviceTimestamp!.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.FromHours(9)), record.DeviceTimestamp);
    }

    [Fact]
    public void Parse_Rfc3164_DefaultTimeZoneAcceptsIanaId()
    {
        // configuration.md: Windows タイムゾーン ID と IANA タイムゾーン ID の両方を受理する
        // （.NET 6 以降、TimeZoneInfo.FindSystemTimeZoneById は Windows 上でも IANA ID を解決できる）。
        var payload = "<13>Jan  1 09:00:00 host app: msg"u8.ToArray();
        var receivedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var datagram = CreateDatagram(payload, receivedAt);
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

        var record = SyslogParser.Parse(datagram, tokyo);

        Assert.Equal(TimeSpan.FromHours(9), record.DeviceTimestamp!.Value.Offset);
    }

    // ==================================================================
    // Issue #135: RFC 3164 のベンダ拡張（Cisco 等）を取りこぼさない
    // ==================================================================

    [Fact]
    public void Parse_Rfc3164_TimestampWithMilliseconds_ParsesFractionalSecondsAndHeaderFields()
    {
        // "Mar  1 00:01:02.345" 形式。従来は '.' で HOSTNAME/TAG 分解が崩れていた。
        var payload = "<13>Mar  1 00:01:02.345 host app: msg"u8.ToArray();
        var receivedAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var datagram = CreateDatagram(payload, receivedAt);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal(
            new DateTimeOffset(2026, 3, 1, 0, 1, 2, TimeSpan.Zero).AddTicks(3_450_000),
            record.DeviceTimestamp);
        Assert.Equal("host", record.Hostname);
        Assert.Equal("app", record.AppName);
        Assert.Equal("msg", record.Message);
    }

    [Fact]
    public void Parse_Rfc3164_TimestampWithSixDigitMicroseconds_ParsesFractionalSeconds()
    {
        var payload = "<13>Mar  1 00:01:02.123456 host app: msg"u8.ToArray();
        var receivedAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var datagram = CreateDatagram(payload, receivedAt);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal(
            new DateTimeOffset(2026, 3, 1, 0, 1, 2, TimeSpan.Zero).AddTicks(1_234_560),
            record.DeviceTimestamp);
        Assert.Equal("host", record.Hostname);
    }

    [Fact]
    public void Parse_Rfc3164_LeadingSequenceNumber_SkipsSequenceAndParsesTimestamp()
    {
        // Cisco `service sequence-numbers`: "<189>45: Mar  1 ..."。
        var payload = "<189>45: Mar  1 00:01:02 host app: msg"u8.ToArray();
        var receivedAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var datagram = CreateDatagram(payload, receivedAt);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal(
            new DateTimeOffset(2026, 3, 1, 0, 1, 2, TimeSpan.Zero),
            record.DeviceTimestamp);
        Assert.Equal("host", record.Hostname);
        Assert.Equal("app", record.AppName);
        Assert.Equal("msg", record.Message);
    }

    [Fact]
    public void Parse_Rfc3164_SequenceNumberWithoutValidTimestamp_RollsBackToBestEffort()
    {
        // "45: " の直後が TIMESTAMP として成立しない場合、シーケンス番号スキップを含めて
        // ロールバックし（pos は書き換えられない）、PRI 直後からの通常の best-effort に委ねる。
        // "45:" 自体は TAG の一般規則（英数字 + ':'）に合致するため AppName として認識される——
        // これは本 Issue で変更していない既存の TAG 判定ロジックの帰結であり、数字始まりの
        // 自由記述本文を誤ってシーケンス番号と解釈して本文を破壊しないことの確認が本テストの主眼。
        var payload = "<13>45: not a timestamp at all"u8.ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Null(record.DeviceTimestamp);
        Assert.Null(record.Hostname);
        Assert.Equal("45", record.AppName);
        Assert.Equal("not a timestamp at all", record.Message);
    }

    [Fact]
    public void Parse_Rfc3164_FourDigitYearVariant_ParsesExplicitYear()
    {
        // "Mmm dd yyyy hh:mm:ss" 形式（一部ベンダの 4 桁年変種）。
        var payload = "<13>Mar  1 2030 00:01:02 host app: msg"u8.ToArray();
        var receivedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var datagram = CreateDatagram(payload, receivedAt);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal(
            new DateTimeOffset(2030, 3, 1, 0, 1, 2, TimeSpan.Zero),
            record.DeviceTimestamp);
        Assert.Equal("host", record.Hostname);
        Assert.Equal("app", record.AppName);
    }

    [Fact]
    public void Parse_Rfc3164_VendorNumericTimeZoneOffset_TakesPrecedenceOverDefault()
    {
        // 送信元付記の数値 TZ（+HH:MM）は、既定タイムゾーン設定より優先される（Issue #134・#135 の統合設計）。
        var payload = "<13>Mar  1 00:01:02 +09:00 host app: msg"u8.ToArray();
        var receivedAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var datagram = CreateDatagram(payload, receivedAt);
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); // UTC+1/+2 (DST)

        var record = SyslogParser.Parse(datagram, berlin);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal(
            new DateTimeOffset(2026, 3, 1, 0, 1, 2, TimeSpan.FromHours(9)),
            record.DeviceTimestamp);
        Assert.Equal("host", record.Hostname);
        Assert.Equal("app", record.AppName);
    }

    [Fact]
    public void Parse_Rfc3164_VendorNumericTimeZoneOffsetCompact_IsRecognized()
    {
        // +HHMM（コロン無し）形式。
        var payload = "<13>Mar  1 00:01:02 -0500 host app: msg"u8.ToArray();
        var receivedAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var datagram = CreateDatagram(payload, receivedAt);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(
            new DateTimeOffset(2026, 3, 1, 0, 1, 2, TimeSpan.FromHours(-5)),
            record.DeviceTimestamp);
        Assert.Equal("host", record.Hostname);
    }

    [Fact]
    public void Parse_Rfc3164_VendorAbbreviationJst_TakesPrecedenceOverDefault()
    {
        // Issue #135 の具体例（Cisco show-timezone）。既定タイムゾーンより優先される。
        var payload = "<13>Mar  1 00:01:02 JST host app: msg"u8.ToArray();
        var receivedAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var datagram = CreateDatagram(payload, receivedAt);
        var utcMinus5 = TimeZoneInfo.CreateCustomTimeZone("UTC-5", TimeSpan.FromHours(-5), "UTC-5", "UTC-5");

        var record = SyslogParser.Parse(datagram, utcMinus5);

        Assert.Equal(
            new DateTimeOffset(2026, 3, 1, 0, 1, 2, TimeSpan.FromHours(9)),
            record.DeviceTimestamp);
        Assert.Equal("host", record.Hostname);
        Assert.Equal("app", record.AppName);
    }

    [Fact]
    public void Parse_Rfc3164_AmbiguousUsAbbreviationCst_IsNotResolvedAsTimeZone()
    {
        // 回帰確認（RFC 3164 §5.4 Example3 相当）: "CST" は曖昧な略号のため TZ として解決せず、
        // 従来どおり HOSTNAME として読まれる——Cisco の clock timezone は任意の名称を任意の
        // オフセットに割り当てられるため、略号から一意にオフセットを断定できない。
        var payload = "<165>Aug 24 05:34:00 CST 1987 mymachine myproc[10]: %% do-nuts"u8.ToArray();
        var receivedAt = new DateTimeOffset(1987, 8, 28, 0, 0, 0, TimeSpan.Zero);
        var datagram = CreateDatagram(payload, receivedAt);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(
            new DateTimeOffset(1987, 8, 24, 5, 34, 0, TimeSpan.Zero),
            record.DeviceTimestamp);
        Assert.Equal("CST", record.Hostname);
    }

    [Fact]
    public void Parse_Rfc3164_UnrecognizedTrailingToken_FallsBackToHostnameParsing()
    {
        // 数値でも既知略号でもないトークンは TZ として消費されず、従来どおり HOSTNAME として読まれる。
        var payload = "<13>Mar  1 00:01:02 relay1 host app: msg"u8.ToArray();
        var receivedAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var datagram = CreateDatagram(payload, receivedAt);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(TimeSpan.Zero, record.DeviceTimestamp!.Value.Offset);
        Assert.Equal("relay1", record.Hostname);
    }

    // ------------------------------------------------------------------
    // 組み合わせ: 実機形式（Cisco IOS 相当）を想定した複合ケース
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_Rfc3164_CiscoStyleSequenceMsecAndVendorTimeZone_DecodesAllFields()
    {
        // シーケンス番号 + msec + 送信元 TZ（空白区切り） + ホスト名 + '%' 始まりの
        // mnemonic 本文（TAG としては認識されず CONTENT 全体が Message に残る best-effort）の組み合わせ。
        var payload = "<189>45: Mar  1 00:01:02.345 JST router1 %SYS-5-CONFIG_I: msg body"u8.ToArray();
        var receivedAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var datagram = CreateDatagram(payload, receivedAt);
        var defaultUtc = TimeZoneInfo.Utc;

        var record = SyslogParser.Parse(datagram, defaultUtc);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Equal(
            new DateTimeOffset(2026, 3, 1, 0, 1, 2, TimeSpan.FromHours(9)).AddTicks(3_450_000),
            record.DeviceTimestamp);
        Assert.Equal("router1", record.Hostname);
        Assert.Null(record.AppName); // '%' は非英数字のため TAG として認識しない
        Assert.Equal("%SYS-5-CONFIG_I: msg body", record.Message);
    }

    [Fact]
    public void Parse_Rfc3164_FourDigitYearWithMilliseconds_DecodesBoth()
    {
        var payload = "<13>Mar  1 2030 00:01:02.500 host app: msg"u8.ToArray();
        var receivedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var datagram = CreateDatagram(payload, receivedAt);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(
            new DateTimeOffset(2030, 3, 1, 0, 1, 2, TimeSpan.Zero).AddTicks(5_000_000),
            record.DeviceTimestamp);
        Assert.Equal("host", record.Hostname);
        Assert.Equal("app", record.AppName);
    }

    [Fact]
    public void Parse_Rfc3164_SequenceNumberAndFourDigitYearAndVendorOffset_DecodesAllFields()
    {
        var payload = "<13>7: Mar  1 2030 00:01:02 +09:00 host app: msg"u8.ToArray();
        var receivedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var datagram = CreateDatagram(payload, receivedAt);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(
            new DateTimeOffset(2030, 3, 1, 0, 1, 2, TimeSpan.FromHours(9)),
            record.DeviceTimestamp);
        Assert.Equal("host", record.Hostname);
        Assert.Equal("app", record.AppName);
        Assert.Equal("msg", record.Message);
    }

    [Fact]
    public void Parse_Rfc3164_InvalidCalendarDateWithMilliseconds_FallsBackToBestEffort()
    {
        // 存在しない日付（2 月 30 日）は従来どおり TIMESTAMP 解析全体を諦める。
        var payload = "<13>Feb 30 00:01:02.345 host app: msg"u8.ToArray();
        var datagram = CreateDatagram(payload);

        var record = SyslogParser.Parse(datagram);

        Assert.Equal(ParseStatus.Parsed, record.ParseStatus);
        Assert.Null(record.DeviceTimestamp);
        Assert.Null(record.Hostname);
    }
}
