using System.Globalization;
using Yagura.Storage;
using Yagura.Web.Components.Common;

namespace Yagura.Web.Export;

/// <summary>
/// ログ検索結果（<see cref="LogRecordSummary"/>）の CSV 出力（Issue #157）。
/// RFC 4180 準拠の行組み立て（<see cref="CsvField"/>）を担う。UTF-8 BOM の付与は本クラスの責務
/// ではなく、呼び出し側が BOM 付与を既定する <see cref="System.Text.Encoding"/>（既定の
/// <c>Encoding.UTF8</c>）を持つ <see cref="StreamWriter"/> を渡すことで実現する
/// （呼び出し側 = <c>YaguraWebViewerExtensions.MapLogSearchCsvExport</c>）。
/// </summary>
/// <remarks>
/// <b>ストリーミング書き出し</b>: CSV 全体を文字列に組み立ててから書き出すのではなく、
/// レコード 1 件ごとに <paramref name="writer"/> へ直接書き出す（メモリ節約。<see cref="WriteAsync"/>
/// 参照）。ただし <see cref="ILogStore.QueryAsync"/> 自体は一括結果（<see cref="IReadOnlyList{T}"/>）
/// を返す契約であり（database.md §1.2）、DB からの読み出し自体をストリーミングするものではない
/// ——本クラスが節約するのは「応答本文の組み立て」の分のみである。
/// </remarks>
public static class LogRecordCsvWriter
{
    /// <summary>ヘッダー行の列名（この順序で本文行も出力する）。</summary>
    private static readonly string[] Headers =
    [
        "受信時刻",
        "送信元アドレス",
        "送信元ポート",
        "プロトコル",
        "重大度",
        "ファシリティ",
        "ホスト名(ログ記載)",
        "アプリ名",
        "ProcId",
        "MsgId",
        "構造化データ",
        "メッセージ",
        "解析状態",
        "送信元時刻",
        "レコードID",
    ];

    /// <summary>
    /// レコード集合を CSV として <paramref name="writer"/> へ書き出す。行区切りは RFC 4180 のとおり
    /// CRLF とする（<see cref="TextWriter.NewLine"/> の既定値に依存しない——実行環境（Windows/Linux）
    /// による改行コードの揺れを避ける）。
    /// </summary>
    public static async Task WriteAsync(
        TextWriter writer,
        IEnumerable<LogRecordSummary> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(records);

        await WriteRowAsync(writer, Headers).ConfigureAwait(false);

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteRowAsync(writer, BuildRow(record)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 1 レコード分の列値（<see cref="Headers"/> と同じ順序）を組み立てる。重大度・ファシリティは
    /// <see cref="UiText.FormatSeverityLong"/> / <see cref="UiText.FormatFacility"/> を再利用する
    /// ——画面表示の対応表（重大度ラベル・ファシリティ名）を二重管理しないためであり、値なし
    /// （<see langword="null"/>）は画面表示と同じプレースホルダ「—」になる（意図的な選択。
    /// それ以外の任意項目は素直な空セルとする——LogRecordCsvWriterTests 参照）。
    /// </summary>
    private static string[] BuildRow(LogRecordSummary record) =>
    [
        YaguraTimeFormatter.FormatLocal(record.ReceivedAt),
        record.SourceAddress,
        record.SourcePort.ToString(CultureInfo.InvariantCulture),
        record.Protocol.ToString(),
        UiText.FormatSeverityLong(record.Severity),
        UiText.FormatFacility(record.Facility),
        record.Hostname ?? string.Empty,
        record.AppName ?? string.Empty,
        record.ProcId ?? string.Empty,
        record.MsgId ?? string.Empty,
        record.StructuredData ?? string.Empty,
        record.Message ?? string.Empty,
        FormatParseStatus(record.ParseStatus),
        record.DeviceTimestamp is { } deviceTimestamp ? YaguraTimeFormatter.FormatLocal(deviceTimestamp) : string.Empty,
        record.Id.ToString(CultureInfo.InvariantCulture),
    ];

    /// <summary>
    /// 解析状態の短形ラベル（CSV の 1 セルに収める表形式向け。<see cref="UiText.ParseFailedLabel"/> /
    /// <see cref="UiText.IncompleteLabel"/>・検索条件の選択肢ラベル
    /// （<see cref="UiText.ParseStatusOptionParseFailed"/> 等。Issue #148）は括弧書きの説明を
    /// 含む長文であり、CSV セルには先頭の短語のみを使う——用語は選択肢ラベルと一致させる）。
    /// </summary>
    private static string FormatParseStatus(ParseStatus status) => status switch
    {
        ParseStatus.Parsed => "解析済み",
        ParseStatus.ParseFailed => "解析失敗",
        ParseStatus.Incomplete => "不完全",
        _ => status.ToString(),
    };

    private static async Task WriteRowAsync(TextWriter writer, IReadOnlyList<string> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                await writer.WriteAsync(',').ConfigureAwait(false);
            }

            await writer.WriteAsync(CsvField.Escape(fields[i])).ConfigureAwait(false);
        }

        await writer.WriteAsync("\r\n").ConfigureAwait(false);
    }
}
