using System.Text;
using Yagura.Ingestion.Udp;
using Yagura.Storage;

namespace Yagura.Ingestion.Parsing;

/// <summary>
/// M2 時点の最小解析（architecture.md §2.1 解析段）。
/// </summary>
/// <remarks>
/// <para>
/// PRI 部（<c>&lt;N&gt;</c>）のみを分解し、facility = N / 8・severity = N % 8 を得る。
/// 残りのバイト列は UTF-8 として Message へデコードする。RFC 3164 / RFC 5424 の
/// ヘッダ分解（HOSTNAME・APP-NAME・PROCID・MSGID・STRUCTURED-DATA・タイムスタンプ等）
/// は M4 の完全解析まで行わない——本パーサはそれらを常に未設定のまま返す。
/// </para>
/// <para>
/// 「解析に失敗したメッセージは破棄しない」（architecture.md §2.1）契約に従い、
/// PRI 不在・不正、または UTF-8 として不正なバイト列は
/// <see cref="ParseStatus.ParseFailed"/> + <see cref="LogRecord.Raw"/> 保持で返す。
/// </para>
/// <para>
/// <see cref="RawDatagram.Incomplete"/> が立っている場合（TCP 接続が切断された時点で
/// メッセージ境界に届いていなかった読みかけデータ）は、PRI が解析できるか否かに関わらず
/// <see cref="ParseStatus.Incomplete"/> を最優先で返す（database.md §2.1「不完全は
/// 解析失敗に優先する」——排他 3 値のうち Incomplete が ParseFailed より優先される唯一の分岐）。
/// </para>
/// </remarks>
public static class MinimalSyslogParser
{
    private const int MinFacility = 0;
    private const int MaxPriValue = 191; // facility 23 * 8 + severity 7

    /// <summary>
    /// 受信済みの生データグラムを解析し、<see cref="LogRecord"/>（挿入前・Id 未採番）を返す。
    /// </summary>
    public static LogRecord Parse(RawDatagram datagram)
    {
        ArgumentNullException.ThrowIfNull(datagram);

        var payload = datagram.Payload;

        if (datagram.Incomplete)
        {
            // database.md §2.1: 不完全は解析失敗に優先する排他 3 値。PRI が偶然解析できて
            // しまう場合でも（境界前で途切れた結果 PRI 部だけは揃っている等）、Incomplete を
            // 優先して返す——「なぜこの行が保存されているか」の理由を単一にするため。
            return new LogRecord(
                ReceivedAt: datagram.ReceivedAt,
                SourceAddress: datagram.SourceAddress,
                SourcePort: datagram.SourcePort,
                Protocol: datagram.Protocol,
                ParseStatus: ParseStatus.Incomplete,
                Raw: payload);
        }

        if (TryParsePri(payload, out var facility, out var severity, out var messageStart))
        {
            var messageBytes = payload.AsSpan(messageStart);
            if (TryDecodeUtf8(messageBytes, out var message))
            {
                return new LogRecord(
                    ReceivedAt: datagram.ReceivedAt,
                    SourceAddress: datagram.SourceAddress,
                    SourcePort: datagram.SourcePort,
                    Protocol: datagram.Protocol,
                    ParseStatus: ParseStatus.Parsed,
                    Facility: facility,
                    Severity: severity,
                    Message: message,
                    Raw: null);
            }
        }

        // PRI 不在・不正、または UTF-8 として不正——生データのまま保存する
        // （「ログを失わない」原則。architecture.md §2.1）。
        return new LogRecord(
            ReceivedAt: datagram.ReceivedAt,
            SourceAddress: datagram.SourceAddress,
            SourcePort: datagram.SourcePort,
            Protocol: datagram.Protocol,
            ParseStatus: ParseStatus.ParseFailed,
            Raw: payload);
    }

    /// <summary>
    /// 先頭の <c>&lt;N&gt;</c> を分解する。N は 0〜191（facility 0〜23 * 8 + severity 0〜7）の範囲でなければならない。
    /// </summary>
    private static bool TryParsePri(byte[] payload, out int facility, out int severity, out int messageStart)
    {
        facility = 0;
        severity = 0;
        messageStart = 0;

        if (payload.Length < 3 || payload[0] != (byte)'<')
        {
            return false;
        }

        var closeIndex = Array.IndexOf(payload, (byte)'>', 1);
        // PRI は先頭付近の短い数値でなければならない（RFC 3164 は最大 3 桁を想定）。
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
        messageStart = closeIndex + 1;
        return true;
    }

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

    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}
