using System.Globalization;
using System.Text;
using Yagura.Ingestion.Udp;
using Yagura.Storage;

namespace Yagura.Ingestion.Parsing;

public static partial class SyslogParser
{
    private const byte Utf8BomByte0 = 0xEF;
    private const byte Utf8BomByte1 = 0xBB;
    private const byte Utf8BomByte2 = 0xBF;

    // ==================================================================
    // RFC 5424
    // ==================================================================

    /// <summary>
    /// RFC 5424 §6.1 HEADER（VERSION SP TIMESTAMP SP HOSTNAME SP APP-NAME SP PROCID SP MSGID）
    /// 以降を解析する。VERSION は <see cref="IsRfc5424"/> で確認済みのため 1 バイト + 区切り SP を
    /// 読み飛ばした地点（<paramref name="afterPri"/>）から開始する。
    /// </summary>
    private static LogRecord ParseRfc5424(RawDatagram datagram, int facility, int severity, int afterPri)
    {
        var payload = datagram.Payload;

        // VERSION（"1"）+ SP を読み飛ばす。IsRfc5424 は afterPri+1 が SP か終端であることまで
        // 確認済み。終端の場合（VERSION の直後で切れている）は HEADER が続かないため失敗。
        var pos = afterPri + 1;
        if (pos >= payload.Length || payload[pos] != (byte)' ')
        {
            return Envelope(datagram, ParseStatus.ParseFailed, raw: payload);
        }

        pos++; // SP を消費

        if (!TryReadField(payload, ref pos, out var timestampField)) return Fail5424(datagram);
        if (!TryReadField(payload, ref pos, out var hostnameField)) return Fail5424(datagram);
        if (!TryReadField(payload, ref pos, out var appNameField)) return Fail5424(datagram);
        if (!TryReadField(payload, ref pos, out var procIdField)) return Fail5424(datagram);
        if (!TryReadLastHeaderField(payload, ref pos, out var msgIdField)) return Fail5424(datagram);

        DateTimeOffset? deviceTimestamp = null;
        if (timestampField is not null)
        {
            // RFC 5424 §6.2.3 TIMESTAMP は NILVALUE か FULL-DATE "T" FULL-TIME（RFC 3339 準拠）。
            // ABNF に反する TIMESTAMP は HEADER 不正として ParseFailed とする（判断表参照）。
            // STRUCTURED-DATA より前に変換する——STRUCTURED-DATA/MSG の非 UTF-8 で ParseFailed
            // になった場合でも、確定済みの HEADER 値（本値を含む）を Envelope に渡せるようにする
            // ため。
            if (!TryParseRfc3339(timestampField, out var parsedTimestamp))
            {
                // TIMESTAMP の値だけが不正（RFC 3339 として解釈できない——時計設定が壊れた
                // 機器等）。この時点で HOSTNAME・APP-NAME・PROCID・MSGID の 4 フィールドは
                // 既に確定済みのため破棄しない（DeviceTimestamp のみ未設定。
                // database.md §2.1「解析失敗時のフィールド保持」——この失敗だけでホスト名
                // 検索が成立しなくなる状態を避ける）。
                return Fail5424WithHeader(
                    datagram, deviceTimestamp: null, facility, severity, hostnameField, appNameField,
                    procIdField, msgIdField);
            }

            deviceTimestamp = parsedTimestamp;
        }

        // STRUCTURED-DATA（RFC 5424 §6.3）。NILVALUE 単体、または 1*SD-ELEMENT。
        if (!TryReadStructuredData(payload, ref pos, out var structuredData))
        {
            // SD-ELEMENT 内が非 UTF-8 で境界検出後のデコードに失敗した場合を含む。この時点で
            // HEADER の 5 フィールドと TIMESTAMP は確定済みのため破棄しない
            // （database.md §2.1「解析失敗時のフィールド保持」）。
            return Fail5424WithHeader(
                datagram, deviceTimestamp, facility, severity, hostnameField, appNameField, procIdField, msgIdField);
        }

        // MSG（RFC 5424 §6.4）: SP を挟んで残り全部。MSG 自体は省略可能（STRUCTURED-DATA で終端）。
        string? message = null;
        if (pos < payload.Length)
        {
            if (payload[pos] != (byte)' ')
            {
                // STRUCTURED-DATA の直後は SP + MSG か終端でなければならない。この構造違反の
                // 時点で HEADER と STRUCTURED-DATA は確定済みのため破棄しない
                // （database.md §2.1「解析失敗時のフィールド保持」）。
                return Fail5424WithHeader(
                    datagram, deviceTimestamp, facility, severity, hostnameField, appNameField, procIdField,
                    msgIdField, structuredData);
            }

            pos++; // SP を消費
            var msgBytes = payload.AsSpan(pos);
            if (!TryDecodeMessage(msgBytes, out message))
            {
                // 非 UTF-8（BOM 明示時に限らず、本実装は MSG 全体を UTF-8 として扱う。
                // 判断表「MSG 非 UTF-8」参照）——ログを失わないため ParseFailed + Raw 保持とするが、
                // HEADER・STRUCTURED-DATA は確定済みのため破棄しない。
                return Fail5424WithHeader(
                    datagram, deviceTimestamp, facility, severity, hostnameField, appNameField, procIdField,
                    msgIdField, structuredData);
            }
        }

        return new LogRecord(
            ReceivedAt: datagram.ReceivedAt,
            SourceAddress: datagram.SourceAddress,
            SourcePort: datagram.SourcePort,
            Protocol: datagram.Protocol,
            ParseStatus: ParseStatus.Parsed,
            DeviceTimestamp: deviceTimestamp,
            Facility: facility,
            Severity: severity,
            Hostname: hostnameField,
            AppName: appNameField,
            ProcId: procIdField,
            MsgId: msgIdField,
            StructuredData: structuredData,
            Message: message,
            Raw: null);
    }

    private static LogRecord Fail5424(RawDatagram datagram) =>
        Envelope(datagram, ParseStatus.ParseFailed, raw: datagram.Payload);

    /// <summary>
    /// HEADER の一部または全部が既に確定した後の失敗（TIMESTAMP 値の RFC 3339 不正・
    /// STRUCTURED-DATA の不正または非 UTF-8・MSG の非 UTF-8・STRUCTURED-DATA 直後の構造違反）
    /// 用の ParseFailed 封筒。確定済みの値のみを渡し、失敗した項目より後は設定しない。
    /// Message は設定せず、Raw は受信した生バイト列を保持する。
    /// </summary>
    private static LogRecord Fail5424WithHeader(
        RawDatagram datagram,
        DateTimeOffset? deviceTimestamp,
        int facility,
        int severity,
        string? hostname,
        string? appName,
        string? procId,
        string? msgId,
        string? structuredData = null) =>
        Envelope(
            datagram,
            ParseStatus.ParseFailed,
            raw: datagram.Payload,
            deviceTimestamp: deviceTimestamp,
            facility: facility,
            severity: severity,
            hostname: hostname,
            appName: appName,
            procId: procId,
            msgId: msgId,
            structuredData: structuredData);

    /// <summary>
    /// HEADER の 1 フィールド（TIMESTAMP・HOSTNAME・APP-NAME・PROCID）を読み取り、末尾の SP
    /// 区切りまで消費する。NILVALUE（<c>-</c>）は null として返す（database.md §2.1・RFC 5424 §6.2）。
    /// </summary>
    private static bool TryReadField(byte[] payload, ref int pos, out string? value)
    {
        value = null;
        var start = pos;
        var spIndex = Array.IndexOf(payload, (byte)' ', start);
        if (spIndex < 0)
        {
            // MSGID より前のフィールドは必ず後続 SP を伴う（HEADER にはこの後 MSGID +
            // STRUCTURED-DATA が続くため）。SP が無ければ HEADER が途中で切れている。
            return false;
        }

        var length = spIndex - start;
        if (length == 0)
        {
            return false;
        }

        if (!TryDecodePrintUsAscii(payload.AsSpan(start, length), out value))
        {
            return false;
        }

        pos = spIndex + 1;
        return true;
    }

    /// <summary>
    /// HEADER 最後のフィールド（MSGID）を読み取る。後続は STRUCTURED-DATA のため SP は必須では
    /// なく、MSGID の終端はペイロード終端または次の SP。
    /// </summary>
    private static bool TryReadLastHeaderField(byte[] payload, ref int pos, out string? value)
    {
        value = null;
        var start = pos;
        var spIndex = Array.IndexOf(payload, (byte)' ', start);
        var end = spIndex < 0 ? payload.Length : spIndex;
        var length = end - start;
        if (length == 0)
        {
            return false;
        }

        if (!TryDecodePrintUsAscii(payload.AsSpan(start, length), out value))
        {
            return false;
        }

        pos = end;
        if (pos < payload.Length)
        {
            // STRUCTURED-DATA が続く前提の SP を消費する（無ければ HEADER 直後に STRUCTURED-DATA
            // が続かず不正）。ただし MSGID がペイロード末尾に到達した場合はここに来ない。
            if (payload[pos] != (byte)' ')
            {
                return false;
            }

            pos++;
        }
        else
        {
            // MSGID の後に STRUCTURED-DATA（少なくとも NILVALUE "-"）が必須（RFC 5424 §6.1）。
            return false;
        }

        return true;
    }

    /// <summary>
    /// PRINTUSASCII（RFC 5424 §6.1: %d33-126）としてデコードし、NILVALUE（"-" 単体）は
    /// null に正規化する。
    /// </summary>
    private static bool TryDecodePrintUsAscii(ReadOnlySpan<byte> bytes, out string? value)
    {
        value = null;

        if (bytes.Length == 1 && bytes[0] == (byte)'-')
        {
            return true; // NILVALUE
        }

        foreach (var b in bytes)
        {
            if (b is < (byte)'!' or > (byte)'~') // %d33-126
            {
                return false;
            }
        }

        value = Encoding.ASCII.GetString(bytes);
        return true;
    }

    // RFC 5424 §6.2.3 TIMESTAMP の ABNF を書式として列挙する（小数秒 0〜6 桁 × オフセット表現 2 系）。
    // TIME-SECFRAC は RFC 3339 では桁数無制限だが、RFC 5424 が 1*6DIGIT に制限している。
    private static readonly string[] Rfc3339UtcFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.f'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.ff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.ffff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.fffff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
    ];

    private static readonly string[] Rfc3339NumOffsetFormats =
    [
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.fzzz",
        "yyyy-MM-dd'T'HH:mm:ss.ffzzz",
        "yyyy-MM-dd'T'HH:mm:ss.fffzzz",
        "yyyy-MM-dd'T'HH:mm:ss.ffffzzz",
        "yyyy-MM-dd'T'HH:mm:ss.fffffzzz",
        "yyyy-MM-dd'T'HH:mm:ss.ffffffzzz",
    ];

    /// <summary>
    /// RFC 5424 §6.2.3 TIMESTAMP（RFC 3339 準拠の FULL-DATE "T" FULL-TIME。同節の追加制約——
    /// "T"・"Z" は大文字必須・小数秒は 1〜6 桁・うるう秒禁止——を含む）を解析する。
    /// </summary>
    /// <remarks>
    /// ABNF を書式配列で明示した TryParseExact で解析する（従来の
    /// DateTimeOffset.TryParse は日付のみ・オフセット欠落・前後空白・独自日付形式など
    /// ABNF 非適合の入力まで受理し、クラス契約「HEADER が ABNF 違反なら ParseFailed」に
    /// 反していた）。うるう秒（60 秒）は RFC 5424 が明示的に禁じており（"Leap seconds
    /// MUST NOT be used"）、.NET の DateTimeOffset パーサも秒 60 を受理しない（SyslogParserTests
    /// で実測固定）ため ParseFailed となる。
    /// </remarks>
    private static bool TryParseRfc3339(string text, out DateTimeOffset value)
    {
        // TIME-OFFSET = "Z" / (("+" / "-") TIME-HOUR ":" TIME-MINUTE)。書式リテラルの 'Z' は
        // 時刻情報を運ばないため、"Z" 終端の系のみ AssumeUniversal で UTC を確定させる
        // （数値オフセット系に AssumeUniversal を混ぜると、オフセット欠落入力が UTC として
        // 受理される従来の欠陥が再発する）。
        if (text.EndsWith('Z'))
        {
            return DateTimeOffset.TryParseExact(
                text,
                Rfc3339UtcFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out value);
        }

        // 書式指定子 "zzz" はコロン欠落の "+0900" も受理する（実測。SyslogParserTests で固定）
        // ため、TIME-NUMOFFSET の構造（("+" / "-") TIME-HOUR ":" TIME-MINUTE）は書式適用前に
        // 検査する。桁の妥当性（2DIGIT・オフセット範囲）は TryParseExact 側が検証する。
        if (text.Length < 6)
        {
            value = default;
            return false;
        }

        var offsetPart = text.AsSpan(text.Length - 6);
        if ((offsetPart[0] != '+' && offsetPart[0] != '-') || offsetPart[3] != ':')
        {
            value = default;
            return false;
        }

        return DateTimeOffset.TryParseExact(
            text,
            Rfc3339NumOffsetFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out value);
    }

    /// <summary>
    /// STRUCTURED-DATA（RFC 5424 §6.3）の境界のみを特定し、原文のまま切り出す
    /// （database.md §2.1「原文のまま保存する。要素分解はしない」）。
    /// </summary>
    /// <remarks>
    /// 要素分解はしないが、境界検出には SD-PARAM の PARAM-VALUE 内エスケープ
    /// （<c>\"</c>・<c>\\</c>・<c>\]</c>。RFC 5424 §6.3）を考慮する必要がある——エスケープされた
    /// <c>]</c> を無視すると SD-ELEMENT の終端 <c>]</c> を誤検出するため。ダブルクォート区間内
    /// でのみエスケープを解釈し、区間外の <c>]</c> は無条件に SD-ELEMENT の終端とする。
    /// </remarks>
    private static bool TryReadStructuredData(byte[] payload, ref int pos, out string? value)
    {
        value = null;

        if (pos >= payload.Length)
        {
            return false; // STRUCTURED-DATA は必須（NILVALUE 単体でも存在しなければならない）
        }

        if (payload[pos] == (byte)'-')
        {
            // NILVALUE。ただし次に PRINTUSASCII が続くと "-foo" のような別トークンの可能性がある
            // ため、直後がペイロード終端または SP であることを要求する。
            var next = pos + 1;
            if (next < payload.Length && payload[next] != (byte)' ')
            {
                return false;
            }

            pos = next;
            return true;
        }

        if (payload[pos] != (byte)'[')
        {
            return false;
        }

        var start = pos;
        var cursor = pos;
        while (cursor < payload.Length && payload[cursor] == (byte)'[')
        {
            cursor++; // '[' を消費
            var inQuotes = false;
            var closed = false;
            while (cursor < payload.Length)
            {
                var b = payload[cursor];
                if (inQuotes)
                {
                    if (b == (byte)'\\' && cursor + 1 < payload.Length)
                    {
                        // \" \\ \] のエスケープ（RFC 5424 §6.3）——次の 1 バイトを無条件に
                        // スキップし、その文字がクォート/ブラケット終端と誤認されないようにする。
                        cursor += 2;
                        continue;
                    }

                    if (b == (byte)'"')
                    {
                        inQuotes = false;
                    }

                    cursor++;
                    continue;
                }

                if (b == (byte)'"')
                {
                    inQuotes = true;
                    cursor++;
                    continue;
                }

                if (b == (byte)']')
                {
                    cursor++;
                    closed = true;
                    break;
                }

                cursor++;
            }

            if (!closed || inQuotes)
            {
                return false; // 未終端の SD-ELEMENT または未終端のクォート区間
            }
        }

        var length = cursor - start;
        if (!TryDecodeUtf8(payload.AsSpan(start, length), out var text))
        {
            return false;
        }

        value = text;
        pos = cursor;
        return true;
    }

    /// <summary>
    /// RFC 5424 §6.4 MSG。先頭 3 バイトが UTF-8 BOM（<c>EF BB BF</c>）であれば除去してから
    /// UTF-8 としてデコードする。MSG-ANY（BOM 無し・エンコーディング不定）と MSG-UTF8（BOM 明示）
    /// のいずれも本実装では UTF-8 として解釈する——Yagura は Message を UTF-8 文字列として保存する
    /// スキーマ（database.md §2.1）であり、BOM の有無に関わらず UTF-8 として読めるものは読む。
    /// </summary>
    private static bool TryDecodeMessage(ReadOnlySpan<byte> bytes, out string message)
    {
        var content = bytes.Length >= 3
            && bytes[0] == Utf8BomByte0 && bytes[1] == Utf8BomByte1 && bytes[2] == Utf8BomByte2
            ? bytes[3..]
            : bytes;

        return TryDecodeUtf8(content, out message);
    }
}
