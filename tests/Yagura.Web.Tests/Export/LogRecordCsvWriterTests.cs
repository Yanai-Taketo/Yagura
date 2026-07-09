using Yagura.Storage;
using Yagura.Web.Export;

namespace Yagura.Web.Tests.Export;

/// <summary>
/// <see cref="LogRecordCsvWriter"/> の行組み立ての単体テスト（Issue #157）。
/// UTF-8 BOM の付与は呼び出し側（<c>YaguraWebViewerExtensions.MapLogSearchCsvExport</c>）の責務
/// であり、本クラスの単体テストの対象外——BOM を含む end-to-end の確認は
/// <c>LogSearchCsvExportEndpointTests</c> が担う。
/// </summary>
public sealed class LogRecordCsvWriterTests
{
    [Fact]
    public async Task WriteAsync_NoRecords_WritesHeaderOnly()
    {
        using var stringWriter = new StringWriter();

        await LogRecordCsvWriter.WriteAsync(stringWriter, []);

        var expectedHeader = "受信時刻,送信元アドレス,送信元ポート,プロトコル,重大度,ファシリティ," +
            "ホスト名(ログ記載),アプリ名,ProcId,MsgId,構造化データ,メッセージ,解析状態,送信元時刻,レコードID\r\n";
        Assert.Equal(expectedHeader, stringWriter.ToString());
    }

    [Fact]
    public async Task WriteAsync_UsesCrLfLineTerminator()
    {
        using var stringWriter = new StringWriter();

        await LogRecordCsvWriter.WriteAsync(stringWriter, [Sample()]);

        var lines = stringWriter.ToString().Split("\r\n", StringSplitOptions.None);
        // 末尾は最終行の後の空文字列(行終端が CRLF であることの裏付け)。
        Assert.Equal(string.Empty, lines[^1]);
        Assert.Equal(3, lines.Length); // ヘッダー + 1 レコード + 末尾の空要素
    }

    [Fact]
    public async Task WriteAsync_FullRecord_MapsAllColumnsInOrder()
    {
        using var stringWriter = new StringWriter();
        var record = new LogRecordSummary(
            Id: 42,
            ReceivedAt: new DateTimeOffset(2026, 7, 9, 3, 4, 5, TimeSpan.Zero),
            SourceAddress: "192.0.2.10",
            SourcePort: 51514,
            Protocol: Protocol.Udp,
            ParseStatus: ParseStatus.Parsed,
            DeviceTimestamp: new DateTimeOffset(2026, 7, 9, 3, 4, 0, TimeSpan.Zero),
            Facility: 4,
            Severity: 3,
            Hostname: "device-01",
            AppName: "sshd",
            ProcId: "1234",
            MsgId: "ID47",
            StructuredData: "[ex@0 a=\"1\"]",
            Message: "認証に失敗しました");

        await LogRecordCsvWriter.WriteAsync(stringWriter, [record]);

        var lines = stringWriter.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);

        var dataLine = lines[1];
        Assert.Contains("192.0.2.10", dataLine, StringComparison.Ordinal);
        Assert.Contains("51514", dataLine, StringComparison.Ordinal);
        Assert.Contains("Udp", dataLine, StringComparison.Ordinal);
        Assert.Contains("3: エラー (Error)", dataLine, StringComparison.Ordinal);
        Assert.Contains("4: 認証", dataLine, StringComparison.Ordinal);
        Assert.Contains("device-01", dataLine, StringComparison.Ordinal);
        Assert.Contains("sshd", dataLine, StringComparison.Ordinal);
        Assert.Contains("1234", dataLine, StringComparison.Ordinal);
        Assert.Contains("ID47", dataLine, StringComparison.Ordinal);
        Assert.Contains("認証に失敗しました", dataLine, StringComparison.Ordinal);
        Assert.Contains("解析済み", dataLine, StringComparison.Ordinal);
        Assert.Contains("42", dataLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_NullOptionalFields_ProducesEmptyOrUiPlaceholderCells()
    {
        using var stringWriter = new StringWriter();
        var record = Sample() with
        {
            DeviceTimestamp = null,
            Facility = null,
            Severity = null,
            Hostname = null,
            AppName = null,
            ProcId = null,
            MsgId = null,
            StructuredData = null,
            Message = null,
        };

        await LogRecordCsvWriter.WriteAsync(stringWriter, [record]);

        var dataLine = stringWriter.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[1];
        var cells = dataLine.Split(',');

        // 重大度・ファシリティは UiText.FormatSeverityLong/FormatFacility を再利用するため、
        // 画面表示と同じプレースホルダ "—" になる(意図的な選択——独自の第 3 の表現を持たない)。
        Assert.Equal("—", cells[4]); // 重大度
        Assert.Equal("—", cells[5]); // ファシリティ

        // それ以外の任意項目(文字列型)は素直に空セル。
        Assert.Equal(string.Empty, cells[6]); // ホスト名
        Assert.Equal(string.Empty, cells[7]); // アプリ名
        Assert.Equal(string.Empty, cells[8]); // ProcId
        Assert.Equal(string.Empty, cells[9]); // MsgId
        Assert.Equal(string.Empty, cells[10]); // 構造化データ
        Assert.Equal(string.Empty, cells[11]); // メッセージ
        Assert.Equal(string.Empty, cells[13]); // 送信元時刻
    }

    [Fact]
    public async Task WriteAsync_MessageContainingCommaQuoteAndNewline_IsRfc4180Escaped()
    {
        using var stringWriter = new StringWriter();
        var record = Sample() with { Message = "line1,\"quoted\"\nline2" };

        await LogRecordCsvWriter.WriteAsync(stringWriter, [record]);

        var output = stringWriter.ToString();
        Assert.Contains("\"line1,\"\"quoted\"\"\nline2\"", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_MessageStartingWithFormulaChar_GetsInjectionPrefix()
    {
        using var stringWriter = new StringWriter();
        var record = Sample() with { Message = "=cmd|'/c calc'!A1" };

        await LogRecordCsvWriter.WriteAsync(stringWriter, [record]);

        var dataLine = stringWriter.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[1];
        Assert.Contains("'=cmd|'/c calc'!A1", dataLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_MultipleRecords_WritesOneRowEach()
    {
        using var stringWriter = new StringWriter();
        var records = new[]
        {
            Sample() with { Id = 1 },
            Sample() with { Id = 2 },
            Sample() with { Id = 3 },
        };

        await LogRecordCsvWriter.WriteAsync(stringWriter, records);

        var lines = stringWriter.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length); // ヘッダー + 3 レコード
    }

    private static LogRecordSummary Sample() => new(
        Id: 1,
        ReceivedAt: new DateTimeOffset(2026, 7, 9, 3, 0, 0, TimeSpan.Zero),
        SourceAddress: "192.0.2.1",
        SourcePort: 514,
        Protocol: Protocol.Udp,
        ParseStatus: ParseStatus.Parsed,
        DeviceTimestamp: null,
        Facility: 3,
        Severity: 5,
        Hostname: null,
        AppName: null,
        ProcId: null,
        MsgId: null,
        StructuredData: null,
        Message: "sample message");
}
