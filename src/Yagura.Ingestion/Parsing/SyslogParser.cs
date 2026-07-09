using System.Globalization;
using System.Text;
using Yagura.Ingestion.Udp;
using Yagura.Storage;

namespace Yagura.Ingestion.Parsing;

/// <summary>
/// M4 時点の完全解析（architecture.md §2.1 解析段）。RFC 5424・RFC 3164 のヘッダを
/// 分解し、<see cref="LogRecord"/> の該当カラムへ写す。
/// </summary>
/// <remarks>
/// <para>
/// PRI 部（<c>&lt;N&gt;</c>）の直後が <c>1</c>（VERSION）+ SP であれば RFC 5424 として、
/// それ以外は RFC 3164 として解析する（RFC 5424 §6.1 HEADER の並び。旧リポジトリの
/// 判別ロジックを踏襲せず、本実装で ABNF から新たに導出した——CLAUDE.md「旧設計の
/// 踏襲を既定にしない」）。
/// </para>
/// <para>
/// 「解析に失敗したメッセージは破棄しない」（architecture.md §2.1）契約に従い、
/// PRI 不在・不正、5424 宣言なのに HEADER が ABNF に反する、または MSG/STRUCTURED-DATA が
/// 非 UTF-8（CP932/Shift-JIS 等を吐く機器の本文を含む）である場合は
/// <see cref="ParseStatus.ParseFailed"/> + <see cref="LogRecord.Raw"/> 保持で返す。
/// RFC 3164 は仕様上 HEADER の形式が緩いため、HOSTNAME・TAG が ABNF に厳密に沿わない
/// 場合でも解析失敗にはせず、取れた範囲だけを設定する best-effort とする（判断基準は
/// 本クラスの private メソッド群のコメントを参照）。
/// </para>
/// <para>
/// **解析失敗時のフィールド保持**（Issue #139）: ParseFailed になった原因より手前で既に
/// 確定した値は破棄せず <see cref="LogRecord"/> に載せる（Raw は常に保持）。対象は
/// CONTENT/MSG/STRUCTURED-DATA の非 UTF-8（このとき Message/StructuredData は null）に加え、
/// TIMESTAMP 値の RFC 3339 不正（確定済みの HOSTNAME 等は保持し DeviceTimestamp のみ未設定）、
/// STRUCTURED-DATA の不正・直後の構造違反（確定済みの HEADER は保持）を含む。一方、PRI 自体が
/// 不正・HEADER が ABNF 途中で途切れるなど、フィールドのトークン化そのものが失敗した場合は、
/// 従来通り該当フィールド以降を設定しない（database.md §2.1「解析失敗時のフィールド保持」）。
/// </para>
/// <para>
/// <see cref="RawDatagram.Incomplete"/> が立っている場合（TCP 接続が切断された時点で
/// メッセージ境界に届いていなかった読みかけデータ）は、他のどの解析結果より優先して
/// <see cref="ParseStatus.Incomplete"/> を返す（database.md §2.1「不完全は
/// 解析失敗に優先する」——排他 3 値のうち Incomplete が ParseFailed より優先される唯一の分岐）。
/// </para>
/// </remarks>
public static class SyslogParser
{
    private const int MinFacility = 0;
    private const int MaxPriValue = 191; // facility 23 * 8 + severity 7
    private const byte Utf8BomByte0 = 0xEF;
    private const byte Utf8BomByte1 = 0xBB;
    private const byte Utf8BomByte2 = 0xBF;

    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// 受信済みの生データグラムを解析し、<see cref="LogRecord"/>（挿入前・Id 未採番）を返す。
    /// </summary>
    public static LogRecord Parse(RawDatagram datagram)
    {
        ArgumentNullException.ThrowIfNull(datagram);

        var payload = datagram.Payload;

        if (datagram.Incomplete)
        {
            // database.md §2.1: 不完全は解析失敗に優先する排他 3 値。HEADER が偶然解析できて
            // しまう場合でも（境界前で途切れた結果 HEADER 部だけは揃っている等）、Incomplete を
            // 優先して返す——「なぜこの行が保存されているか」の理由を単一にするため。
            return Envelope(datagram, ParseStatus.Incomplete, raw: payload);
        }

        if (!TryParsePri(payload, out var facility, out var severity, out var afterPri))
        {
            // PRI 不在・不正——生データのまま保存する（「ログを失わない」原則。architecture.md §2.1）。
            return Envelope(datagram, ParseStatus.ParseFailed, raw: payload);
        }

        return IsRfc5424(payload, afterPri)
            ? ParseRfc5424(datagram, facility, severity, afterPri)
            : ParseRfc3164(datagram, facility, severity, afterPri);
    }

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
    /// PRI 直後が RFC 5424 の VERSION（"1" 固定。RFC 5424 §6.2.2 は将来のバージョン拡張を許すが、
    /// 本実装は既知の "1" のみを対象とする）+ SP であれば 5424 と判別する。それ以外は 3164。
    /// </summary>
    /// <remarks>
    /// RFC 5424 VERSION の ABNF は <c>NONZERO-DIGIT 0*2DIGIT</c> であり "1" 以外（"2" 等）も
    /// 文法上あり得るが、本実装が解釈できる版は "1" のみである。"<c>&lt;34&gt;2 ...</c>" のような
    /// 未知バージョンは 5424 の枠組みとして判別せず（<see cref="IsRfc5424"/> が false を返す）、
    /// RFC 3164 の best-effort 解析にそのまま委ねる——PRI 以降が 3164 の TIMESTAMP・TAG の
    /// いずれの形にも一致しなければ、取れる範囲が無いまま CONTENT 全体が Message に入るだけで、
    /// ParseFailed にはならない（判断表「未知 VERSION」参照。5424 として壊れているとみなして
    /// 即座に破棄するより、緩い 3164 側の受け皿に委ねる方が「ログを失わない」原則に沿う）。
    /// </remarks>
    private static bool IsRfc5424(byte[] payload, int afterPri) =>
        afterPri < payload.Length && payload[afterPri] == (byte)'1'
        && (afterPri + 1 == payload.Length || payload[afterPri + 1] == (byte)' ');

    /// <summary>
    /// Incomplete・ParseFailed 用の封筒を組み立てる。
    /// </summary>
    /// <remarks>
    /// 既定はフィールド未分解（PRI 不在・HEADER 途中断など、値そのものが確定していない失敗）
    /// 用途だが、オプション引数で PRI・HEADER 側の確定済み値を渡せる——本文/STRUCTURED-DATA の
    /// 非 UTF-8 のように、HEADER より後段の失敗で HEADER 側の確定値まで破棄しないため
    /// （Issue #139。database.md §2.1「解析失敗時のフィールド保持」）。
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
            // ため（Issue #139）。
            if (!TryParseRfc3339(timestampField, out var parsedTimestamp))
            {
                // TIMESTAMP の値だけが不正（RFC 3339 として解釈できない——時計設定が壊れた
                // 機器等）。この時点で HOSTNAME・APP-NAME・PROCID・MSGID の 4 フィールドは
                // 既に確定済みのため破棄しない（DeviceTimestamp のみ未設定。Issue #139・
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
            // HEADER の 5 フィールドと TIMESTAMP は確定済みのため破棄しない（Issue #139。
            // database.md §2.1「解析失敗時のフィールド保持」）。
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
                // 時点で HEADER と STRUCTURED-DATA は確定済みのため破棄しない（Issue #139・
                // database.md §2.1「解析失敗時のフィールド保持」）。
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
                // HEADER・STRUCTURED-DATA は確定済みのため破棄しない（Issue #139）。
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
    /// Message は設定せず、Raw は受信した生バイト列を保持する（Issue #139）。
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

    /// <summary>
    /// RFC 5424 §6.2.3 TIMESTAMP（RFC 3339 準拠の FULL-DATE "T" FULL-TIME）を解析する。
    /// うるう秒（60 秒）は RFC 5424 が明示的に禁じており（"Leap seconds MUST NOT be used"）、
    /// .NET の DateTimeOffset パーサも受理しないため自然に ParseFailed となる。
    /// </summary>
    private static bool TryParseRfc3339(string text, out DateTimeOffset value)
    {
        // "O"（round-trip）系ではなく、RFC 3339 が許す小数秒桁数（1〜6 桁）・"Z" と数値オフセットの
        // 両方を受理するため、DateTimeOffset.TryParse を不変カルチャ・厳格スタイルで用いる。
        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
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

    // ==================================================================
    // RFC 3164
    // ==================================================================

    /// <summary>
    /// RFC 3164 §4.1 HEADER（TIMESTAMP SP HOSTNAME）+ MSG（TAG + CONTENT）を best-effort で
    /// 解析する。RFC 3164 は「べき」規定が多く実装依存の逸脱が現実に多いため、ABNF に
    /// 完全一致しない場合でも即座に ParseFailed とはせず、認識できた範囲だけを設定する
    /// （判断基準は private メソッド群のコメントを参照）。
    /// </summary>
    private static LogRecord ParseRfc3164(RawDatagram datagram, int facility, int severity, int afterPri)
    {
        var payload = datagram.Payload;
        var pos = afterPri;

        DateTimeOffset? deviceTimestamp = null;
        string? hostname = null;

        if (TryReadRfc3164Timestamp(payload, ref pos, datagram.ReceivedAt, out var timestamp))
        {
            deviceTimestamp = timestamp;

            // TIMESTAMP の直後は SP + HOSTNAME（RFC 3164 §4.1.2）。HOSTNAME も取れなければ
            // 諦めて残り全体を CONTENT 側（Message）に委ねる——取れた範囲だけ設定する best-effort。
            if (pos < payload.Length && payload[pos] == (byte)' ')
            {
                var afterSp = pos + 1;
                if (TryReadRfc3164Hostname(payload, afterSp, out var host, out var afterHostname))
                {
                    hostname = host;
                    pos = afterHostname;
                }
            }
        }
        else
        {
            // TIMESTAMP が ABNF に沿わない——HEADER 全体を諦め、PRI 直後からを丸ごと
            // MSG（TAG + CONTENT）側の解析に委ねる。best-effort の中核（判断表 #3164-1）。
            pos = afterPri;
        }

        // MSG 部（RFC 3164 §4.1.3）: TAG（英数字のみ、非英数字で終端。最大 32 文字）+ CONTENT。
        var (appName, procId, contentStart) = TryReadRfc3164Tag(payload, pos);

        var messageBytes = payload.AsSpan(contentStart);
        if (!TryDecodeUtf8(messageBytes, out var message))
        {
            // RFC 3164 は文字コードを規定しないが、Yagura は Message を UTF-8 文字列として
            // 保存するスキーマである。CONTENT が非 UTF-8（例: CP932/Shift-JIS を吐く日本の
            // 旧来機器）の場合は本文を安全に保持できないため ParseFailed + Raw 保持とする
            // （判断表「3164 の CONTENT 非 UTF-8」）。ただし PRI・TIMESTAMP・HOSTNAME・TAG など
            // CONTENT より手前で既に確定した値は破棄しない（Issue #139。database.md §2.1
            // 「解析失敗時のフィールド保持」）——この失敗だけでホスト名すら検索できなくなる
            // 状態を避けるため。
            return Envelope(
                datagram,
                ParseStatus.ParseFailed,
                raw: payload,
                deviceTimestamp: deviceTimestamp,
                facility: facility,
                severity: severity,
                hostname: hostname,
                appName: appName,
                procId: procId);
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
            Hostname: hostname,
            AppName: appName,
            ProcId: procId,
            Message: message,
            Raw: null);
    }

    /// <summary>
    /// RFC 3164 §4.1.2 TIMESTAMP（<c>Mmm dd hh:mm:ss</c>。日が 1 桁の場合は先頭を空白埋めした
    /// 2 文字）を解析する。年・タイムゾーンを持たないため、年は <paramref name="referenceTime"/>
    /// （= ReceivedAt。基準時刻は 1 回だけ読み取り、以後使い回す——年またぎ判定の内部で複数回
    /// 現在時刻を読むと境界付近で矛盾した年を選びうるため）から補完し、タイムゾーンは UTC と
    /// みなす（本メソッドの戻り値は database.md §2.1 の DeviceTimestamp——「参考情報。基準軸に
    /// しない」——であり、この近似はその性質に依拠する。年またぎは §「年跨ぎ補完」を参照）。
    /// </summary>
    private static bool TryReadRfc3164Timestamp(
        byte[] payload,
        ref int pos,
        DateTimeOffset referenceTime,
        out DateTimeOffset timestamp)
    {
        timestamp = default;
        const int TimestampLength = 15; // "Mmm dd hh:mm:ss" = 3+1+2+1+2+1+2+1+2 = 15

        if (pos + TimestampLength > payload.Length)
        {
            return false;
        }

        var span = payload.AsSpan(pos, TimestampLength);
        if (span[3] != (byte)' ' || span[6] != (byte)' ' || span[9] != (byte)':' || span[12] != (byte)':')
        {
            return false;
        }

        if (!TryDecodeAsciiLetters(span[..3], out var monthAbbrev) || !TryGetMonthNumber(monthAbbrev, out var month))
        {
            return false;
        }

        // dd: 先頭が空白または数字、2 桁目は数字（RFC 3164 §4.1.2「1 桁なら空白 + 数字」）。
        var dayTens = span[4];
        var dayOnes = span[5];
        if ((dayTens != (byte)' ' && (dayTens is < (byte)'0' or > (byte)'9')) || dayOnes is < (byte)'0' or > (byte)'9')
        {
            return false;
        }

        var day = (dayTens == (byte)' ' ? 0 : (dayTens - (byte)'0') * 10) + (dayOnes - (byte)'0');
        if (day is < 1 or > 31)
        {
            return false;
        }

        if (!TryReadTwoDigits(span[7], span[8], out var hour) || hour > 23)
        {
            return false;
        }

        if (!TryReadTwoDigits(span[10], span[11], out var minute) || minute > 59)
        {
            return false;
        }

        if (!TryReadTwoDigits(span[13], span[14], out var second) || second > 59)
        {
            return false;
        }

        var year = ResolveYear(referenceTime, month, day);

        try
        {
            timestamp = new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            // 例: 2 月 30 日のような実在しない日付。TIMESTAMP 形式としては ABNF 通りでも
            // 暦として不正なため HEADER 解析を諦める。
            return false;
        }

        pos += TimestampLength;
        return true;
    }

    /// <summary>
    /// 年の補完（年・タイムゾーンを持たない RFC 3164 TIMESTAMP の近似。仕様は本メソッドの
    /// doc コメントの通り、呼び出し元 <see cref="TryReadRfc3164Timestamp"/> のコメントに集約）。
    /// <paramref name="referenceTime"/>（ReceivedAt）の年・前年・翌年の 3 候補のうち、
    /// 「月日を referenceTime と同じ年に置いたときの日数差」が最小になる年を選ぶ
    /// （年末年始の跨ぎ——例: 12 月に受信した 1 月付けメッセージ——では翌年側が選ばれる）。
    /// </summary>
    private static int ResolveYear(DateTimeOffset referenceTime, int month, int day)
    {
        var refYear = referenceTime.Year;
        var bestYear = refYear;
        var bestDiff = double.MaxValue;

        for (var candidateYear = refYear - 1; candidateYear <= refYear + 1; candidateYear++)
        {
            if (day > DateTime.DaysInMonth(candidateYear, month))
            {
                continue; // 例: うるう年でない年の 2/29 は候補から除外
            }

            var candidate = new DateTimeOffset(candidateYear, month, day, 0, 0, 0, TimeSpan.Zero);
            var diff = Math.Abs((candidate - referenceTime).TotalDays);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestYear = candidateYear;
            }
        }

        return bestYear;
    }

    private static bool TryReadTwoDigits(byte tens, byte ones, out int value)
    {
        value = 0;
        if (tens is < (byte)'0' or > (byte)'9' || ones is < (byte)'0' or > (byte)'9')
        {
            return false;
        }

        value = ((tens - (byte)'0') * 10) + (ones - (byte)'0');
        return true;
    }

    private static bool TryDecodeAsciiLetters(ReadOnlySpan<byte> bytes, out string text)
    {
        Span<char> chars = stackalloc char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b is < (byte)'A' or > (byte)'z' || (b > (byte)'Z' && b < (byte)'a'))
            {
                text = string.Empty;
                return false;
            }

            chars[i] = (char)b;
        }

        text = new string(chars);
        return true;
    }

    private static readonly string[] MonthAbbreviations =
    [
        "Jan", "Feb", "Mar", "Apr", "May", "Jun",
        "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
    ];

    private static bool TryGetMonthNumber(string abbrev, out int month)
    {
        var index = Array.IndexOf(MonthAbbreviations, abbrev);
        month = index + 1;
        return index >= 0;
    }

    /// <summary>
    /// RFC 3164 §4.1.2 HOSTNAME を読み取る。ABNF 上は「次の SP まで」だが、RFC 3164 は
    /// HOSTNAME が省略された中継メッセージ（RFC §5.4 Example 2 のような、本来 HOSTNAME を
    /// 持たない相手から届く行）も現実に存在する。ここでは単純に「次の SP まで」を HOSTNAME
    /// として読み取り、続く TAG 解析（<see cref="TryReadRfc3164Tag"/>）が英数字 + ':' の形に
    /// マッチしない場合は HOSTNAME ではなく CONTENT の一部だった可能性があるが、本実装は
    /// 「TIMESTAMP の直後の 1 トークンは HOSTNAME」という単純な規則に留め、誤判定は
    /// AppName/ProcId が null のまま Message 側に取り込まれる形で吸収する（判断表 #3164-2）。
    /// </summary>
    private static bool TryReadRfc3164Hostname(byte[] payload, int start, out string? hostname, out int afterHostname)
    {
        hostname = null;
        afterHostname = start;

        if (start >= payload.Length)
        {
            return false;
        }

        var spIndex = Array.IndexOf(payload, (byte)' ', start);
        var end = spIndex < 0 ? payload.Length : spIndex;
        var length = end - start;
        if (length == 0)
        {
            return false;
        }

        if (!TryDecodeUtf8(payload.AsSpan(start, length), out var text))
        {
            return false;
        }

        hostname = text;
        afterHostname = spIndex < 0 ? end : end + 1; // 続く SP も消費する
        return true;
    }

    /// <summary>
    /// RFC 3164 §4.1.3 MSG の TAG を読み取る。TAG は英数字のみで構成され、非英数字（典型的には
    /// <c>[</c> または <c>:</c>）で終端する（最大 32 文字。RFC 3164 §4.1.3）。
    /// <para>
    /// 典型形式 <c>app[pid]:</c>・<c>app:</c>・<c>app[pid]</c>（コロン無し）は TAG として
    /// 認識する。一方、英数字トークンの直後が SP・ペイロード終端など <c>[</c> '<c>:</c>' 以外で
    /// 終わる場合は TAG として確定させない（判断表 #3164-3）——RFC 3164 §5.4 の
    /// "Use the BFG!" 例で "Use the BFG!" 全体が CONTENT として扱われる（TAG 無しとして
    /// 明記されている）ことに倣う。この境界を採らないと、TAG を持たない自由形式の本文
    /// （例: "integration-test-&lt;guid&gt;"）の先頭語が誤って AppName に吸われ、CONTENT の
    /// 一部を失う。該当しない場合は AppName・ProcId を設定せず、CONTENT の開始位置を pos の
    /// まま返す——「TAG が取れなくても Message 全体は残す」best-effort。
    /// </para>
    /// </summary>
    private static (string? AppName, string? ProcId, int ContentStart) TryReadRfc3164Tag(byte[] payload, int pos)
    {
        const int MaxTagLength = 32;

        var start = pos;
        var cursor = pos;
        while (cursor < payload.Length && cursor - start < MaxTagLength && IsAsciiAlphanumeric(payload[cursor]))
        {
            cursor++;
        }

        if (cursor == start)
        {
            return (null, null, pos); // TAG 無し（先頭が非英数字）——Message 側に委ねる
        }

        // TAG は英数字のみ（上のループで判定済み）なので ASCII デコードで足りる。
        var appName = Encoding.ASCII.GetString(payload, start, cursor - start);

        if (cursor < payload.Length && payload[cursor] == (byte)'[')
        {
            var pidStart = cursor + 1;
            var closeIndex = Array.IndexOf(payload, (byte)']', pidStart);
            if (closeIndex > pidStart)
            {
                var procIdBytes = payload.AsSpan(pidStart, closeIndex - pidStart);
                var isAllDigits = true;
                foreach (var b in procIdBytes)
                {
                    if (b is < (byte)'0' or > (byte)'9')
                    {
                        isAllDigits = false;
                        break;
                    }
                }

                if (isAllDigits)
                {
                    var procId = Encoding.ASCII.GetString(payload, pidStart, closeIndex - pidStart);
                    cursor = closeIndex + 1;

                    if (cursor < payload.Length && payload[cursor] == (byte)':')
                    {
                        cursor++;
                    }

                    if (cursor < payload.Length && payload[cursor] == (byte)' ')
                    {
                        cursor++;
                    }

                    return (appName, procId, cursor);
                }
            }
        }

        if (cursor < payload.Length && payload[cursor] == (byte)':')
        {
            cursor++;
            if (cursor < payload.Length && payload[cursor] == (byte)' ')
            {
                cursor++;
            }

            return (appName, null, cursor);
        }

        // 英数字トークンの直後が '[' でも ':' でもない——TAG として確定させない
        // （判断表 #3164-3。RFC 3164 §5.4 "Use the BFG!" 例に倣い、CONTENT 全体を残す）。
        return (null, null, pos);
    }

    private static bool IsAsciiAlphanumeric(byte b) =>
        b is >= (byte)'0' and <= (byte)'9'
            or >= (byte)'A' and <= (byte)'Z'
            or >= (byte)'a' and <= (byte)'z';

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
