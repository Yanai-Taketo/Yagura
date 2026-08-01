using System.Text;
using Yagura.Ingestion.Udp;
using Yagura.Storage;

namespace Yagura.Ingestion.Parsing;

public static partial class SyslogParser
{
    private const int MinFacility = 0;
    private const int MaxPriValue = 191; // facility 23 * 8 + severity 7

    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // ------------------------------------------------------------------
    // PRI（RFC 5424 §6.2.1 / RFC 3164 §4.1.1 共通。両 RFC で同一の "<" 1*3DIGIT ">" 形式）
    // ------------------------------------------------------------------

    /// <summary>
    /// 先頭の <c>&lt;N&gt;</c> を分解する。N は 0〜191（facility 0〜23 * 8 + severity 0〜7）の範囲でなければならない。
    /// </summary>
    private static bool TryParsePri(byte[] payload, out int facility, out int severity, out int afterPri)
    {
        facility = 0;
        severity = 0;
        afterPri = 0;

        if (payload.Length < 3 || payload[0] != (byte)'<')
        {
            return false;
        }

        var closeIndex = Array.IndexOf(payload, (byte)'>', 1);
        // PRI は先頭付近の短い数値でなければならない（RFC 3164 §4.1.1 は最大 5 文字を想定）。
        // 極端に離れた '>' を誤検出しないよう探索範囲を制限する。
        if (closeIndex is < 2 or > 5)
        {
            return false;
        }

        var digitsLength = closeIndex - 1;
        Span<char> digitChars = stackalloc char[digitsLength];
        for (var i = 0; i < digitsLength; i++)
        {
            var b = payload[1 + i];
            if (b is < (byte)'0' or > (byte)'9')
            {
                return false;
            }

            digitChars[i] = (char)b;
        }

        if (!int.TryParse(digitChars, out var priValue) || priValue is < MinFacility or > MaxPriValue)
        {
            return false;
        }

        facility = priValue / 8;
        severity = priValue % 8;
        afterPri = closeIndex + 1;
        return true;
    }

    /// <summary>
    /// Incomplete・ParseFailed 用の封筒を組み立てる。
    /// </summary>
    /// <remarks>
    /// 既定はフィールド未分解（PRI 不在・HEADER 途中断など、値そのものが確定していない失敗）
    /// 用途だが、オプション引数で PRI・HEADER 側の確定済み値を渡せる——本文/STRUCTURED-DATA の
    /// 非 UTF-8 のように、HEADER より後段の失敗で HEADER 側の確定値まで破棄しないため
    /// （database.md §2.1「解析失敗時のフィールド保持」）。
    /// </remarks>
    private static LogRecord Envelope(
        RawDatagram datagram,
        ParseStatus status,
        byte[]? raw,
        DateTimeOffset? deviceTimestamp = null,
        int? facility = null,
        int? severity = null,
        string? hostname = null,
        string? appName = null,
        string? procId = null,
        string? msgId = null,
        string? structuredData = null) =>
        new(
            ReceivedAt: datagram.ReceivedAt,
            SourceAddress: datagram.SourceAddress,
            SourcePort: datagram.SourcePort,
            Protocol: datagram.Protocol,
            ParseStatus: status,
            DeviceTimestamp: deviceTimestamp,
            Facility: facility,
            Severity: severity,
            Hostname: hostname,
            AppName: appName,
            ProcId: procId,
            MsgId: msgId,
            StructuredData: structuredData,
            Raw: raw);

    // ==================================================================
    // 共通
    // ==================================================================

    private static bool TryDecodeUtf8(ReadOnlySpan<byte> bytes, out string message)
    {
        // Encoding.UTF8 の既定インスタンスは不正シーケンスを置換文字に置き換えて例外を
        // 出さないため、厳密検証には ThrowOnInvalidBytes を有効化したデコーダを使う
        // （不正 UTF-8 は解析失敗として Raw 保持したいため、黙って置換させない）。
        try
        {
            message = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            message = string.Empty;
            return false;
        }
    }
}
