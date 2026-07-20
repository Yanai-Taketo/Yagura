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
    /// <param name="datagram">受信済みの生データグラム。</param>
    /// <param name="defaultRfc3164TimeZone">
    /// RFC 3164 TIMESTAMP（年・タイムゾーンを持たない）の解釈に使う既定タイムゾーン
    /// （Issue #134。configuration.md の <c>Ingestion:Rfc3164:DefaultTimeZone</c>）。
    /// <see langword="null"/> は UTC（<see cref="TimeZoneInfo.Utc"/>）——本引数を省略した既存の
    /// 呼び出し元との後方互換を保つ既定値。<b>優先順位</b>: TIMESTAMP に送信元付記の
    /// タイムゾーン（Issue #135。Cisco の <c>show-timezone</c> 等）が取れた場合はそちらを優先し、
    /// 取れない場合にのみ本引数を適用する（RFC 5424 の TIMESTAMP は ISO 8601 でタイムゾーンを
    /// 自己完結して持つため本引数の対象外）。
    /// </param>
    public static LogRecord Parse(RawDatagram datagram, TimeZoneInfo? defaultRfc3164TimeZone = null)
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
            : ParseRfc3164(datagram, facility, severity, afterPri, defaultRfc3164TimeZone);
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
    /// ABNF を書式配列で明示した TryParseExact で解析する（Issue #361。従来の
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

    // ==================================================================
    // RFC 3164
    // ==================================================================

    /// <summary>
    /// RFC 3164 §4.1 HEADER（TIMESTAMP SP HOSTNAME）+ MSG（TAG + CONTENT）を best-effort で
    /// 解析する。RFC 3164 は「べき」規定が多く実装依存の逸脱が現実に多いため、ABNF に
    /// 完全一致しない場合でも即座に ParseFailed とはせず、認識できた範囲だけを設定する
    /// （判断基準は private メソッド群のコメントを参照）。
    /// </summary>
    private static LogRecord ParseRfc3164(
        RawDatagram datagram, int facility, int severity, int afterPri, TimeZoneInfo? defaultRfc3164TimeZone)
    {
        var payload = datagram.Payload;
        var pos = afterPri;

        DateTimeOffset? deviceTimestamp = null;
        string? hostname = null;

        if (TryReadRfc3164Timestamp(payload, ref pos, datagram.ReceivedAt, defaultRfc3164TimeZone, out var timestamp))
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
    /// 2 文字）を寛容リーダとして解析する（Issue #135）。年・タイムゾーンを持たないため、年は
    /// 4 桁年変種（後述）が無い限り <paramref name="referenceTime"/>（= ReceivedAt。基準時刻は
    /// 1 回だけ読み取り、以後使い回す——年またぎ判定の内部で複数回現在時刻を読むと境界付近で
    /// 矛盾した年を選びうるため）から補完する（年またぎは <see cref="ResolveYear"/> 参照）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>受理する逸脱（Issue #135。Cisco IOS 等のベンダ拡張）</b>: いずれも「厳密な
    /// <c>Mmm dd hh:mm:ss</c> のみ受理」だった旧実装では HOSTNAME/TAG 分解ごと崩れていた形式。
    /// <list type="bullet">
    /// <item><b>先頭シーケンス番号</b>（<c>service sequence-numbers</c>。例: <c>45: Mar  1 ...</c>）:
    /// <see cref="SkipLeadingSequenceNumber"/> が数字列 + <c>:</c> + SP の並びを検出した場合にのみ
    /// 読み飛ばす。後続が有効な TIMESTAMP でなければ全体を諦める（数字始まりの自由記述本文を
    /// 誤ってシーケンス番号と判定しないよう、後続の TIMESTAMP 成立を条件にする——ロールバックは
    /// 呼び出し元に <paramref name="pos"/> を書き戻さないことで実現する）。</item>
    /// <item><b>4 桁年変種</b>（例: <c>Mmm dd yyyy hh:mm:ss</c>）: 日の直後が 4 桁の数字であれば
    /// 年として読み取り、以後の年推定（<see cref="ResolveYear"/>）を使わない。</item>
    /// <item><b>秒の小数部</b>（msec 付き。例: <c>hh:mm:ss.345</c>）: <c>.</c> に続く 1 桁以上の
    /// 数字を tick 精度（7 桁）へ正規化して <see cref="DateTimeOffset"/> に加算する。</item>
    /// <item><b>TIMESTAMP 直後の TZ 付記</b>（<c>show-timezone</c>。例: <c>... 00:01:02 JST</c>）:
    /// <see cref="TryReadVendorTimeZone"/> が数値オフセット（<c>+HH:MM</c>/<c>+HHMM</c>）または
    /// 既知の曖昧でない略号（<c>UTC</c>/<c>GMT</c>/<c>JST</c>。曖昧な略号——米国 4 系統の
    /// <c>CST</c>/<c>EST</c>/<c>PST</c>/<c>MST</c> 等——は Cisco の <c>clock timezone</c> が
    /// 任意の名称をオフセットと無関係に設定できるため意図的に解決対象に含めない。未知の略号は
    /// TZ として消費せず、従来どおり HOSTNAME 側の解析に委ねる——RFC 3164 §5.4 の実例
    /// （後述の Example3 相当）で "CST" が HOSTNAME として読まれる既存挙動を壊さないため）を
    /// 検出できた場合のみ、その場でオフセットとして採用する。
    /// </item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>タイムゾーンの優先順位（Issue #134・#135 の統合設計）</b>: ①TIMESTAMP に送信元付記の
    /// TZ が取れた場合はそれを最優先で使う。②取れない場合は
    /// <paramref name="defaultRfc3164TimeZone"/>（configuration.md
    /// <c>Ingestion:Rfc3164:DefaultTimeZone</c>。既定は UTC = 現状互換）を適用する。
    /// <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/> で当該 TIMESTAMP の年月日時刻に対する
    /// オフセットを都度計算するため、DST を持つゾーンでも季節に応じた正しいオフセットになる。
    /// 本メソッドの戻り値は database.md §2.1 の DeviceTimestamp——「参考情報。基準軸にしない」——
    /// であり、この近似はその性質に依拠する。
    /// </para>
    /// </remarks>
    private static bool TryReadRfc3164Timestamp(
        byte[] payload,
        ref int pos,
        DateTimeOffset referenceTime,
        TimeZoneInfo? defaultRfc3164TimeZone,
        out DateTimeOffset timestamp)
    {
        timestamp = default;

        var cursor = pos;

        // 先頭シーケンス番号（Cisco service sequence-numbers）の任意スキップ。後続が TIMESTAMP
        // として成立しなければ、pos を書き換えずに返すことで自動的にロールバックされる。
        SkipLeadingSequenceNumber(payload, ref cursor);

        if (!TryReadMonthAbbrev(payload, ref cursor, out var month)) return false;
        if (!TryConsumeByte(payload, ref cursor, (byte)' ')) return false;
        if (!TryReadDay(payload, ref cursor, out var day)) return false;
        if (!TryConsumeByte(payload, ref cursor, (byte)' ')) return false;

        // 4 桁年変種（例: "Mmm dd yyyy hh:mm:ss"）。数字 4 桁が続けば年として確定させる——
        // 通常形式の "hh:mm:ss" は 3 文字目が ':' のため誤って年と解釈されることはない。
        int? explicitYear = null;
        if (TryReadFourDigitYear(payload, ref cursor, out var year4))
        {
            explicitYear = year4;
            if (!TryConsumeByte(payload, ref cursor, (byte)' ')) return false;
        }

        if (!TryReadTimeOfDay(payload, ref cursor, out var hour, out var minute, out var second, out var fractionTicks))
        {
            return false;
        }

        var vendorOffset = TryReadVendorTimeZone(payload, ref cursor);

        var year = explicitYear ?? ResolveYear(referenceTime, month, day);

        try
        {
            TimeSpan offset;
            if (vendorOffset is { } vendor)
            {
                // ①送信元付記の TZ が最優先（Issue #134・#135）。
                offset = vendor;
            }
            else
            {
                // ②既定タイムゾーン（未設定時は UTC = 現状互換）。DST を考慮するため、
                // 固定オフセットではなく該当日時に対する GetUtcOffset を都度計算する。
                var zone = defaultRfc3164TimeZone ?? TimeZoneInfo.Utc;
                var localWallClock = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
                offset = zone.GetUtcOffset(localWallClock);
            }

            var baseTimestamp = new DateTimeOffset(year, month, day, hour, minute, second, offset);
            timestamp = fractionTicks == 0 ? baseTimestamp : baseTimestamp.AddTicks(fractionTicks);
        }
        catch (ArgumentOutOfRangeException)
        {
            // 例: 2 月 30 日のような実在しない日付。TIMESTAMP 形式としては ABNF 通りでも
            // 暦として不正なため HEADER 解析を諦める。
            return false;
        }

        pos = cursor;
        return true;
    }

    /// <summary>
    /// 先頭の数字列 + <c>:</c> + SP（例: <c>45: </c>）をシーケンス番号として読み飛ばす
    /// （Cisco <c>service sequence-numbers</c>。Issue #135）。パターンに一致しない場合は
    /// <paramref name="cursor"/> を変更しない——呼び出し元が TIMESTAMP 解析に失敗した際、
    /// 本メソッドで進めた分だけを含めて丸ごとロールバックできるようにするため。
    /// </summary>
    private static void SkipLeadingSequenceNumber(byte[] payload, ref int cursor)
    {
        const int MaxSequenceDigits = 10;

        var start = cursor;
        var digitsEnd = start;
        while (digitsEnd < payload.Length && digitsEnd - start < MaxSequenceDigits
            && payload[digitsEnd] is >= (byte)'0' and <= (byte)'9')
        {
            digitsEnd++;
        }

        if (digitsEnd == start || digitsEnd >= payload.Length || payload[digitsEnd] != (byte)':')
        {
            return; // シーケンス番号ではない（数字が無い、または区切りが ':' でない）
        }

        var afterColon = digitsEnd + 1;
        cursor = afterColon < payload.Length && payload[afterColon] == (byte)' ' ? afterColon + 1 : afterColon;
    }

    private static bool TryReadMonthAbbrev(byte[] payload, ref int cursor, out int month)
    {
        month = 0;
        if (cursor + 3 > payload.Length)
        {
            return false;
        }

        if (!TryDecodeAsciiLetters(payload.AsSpan(cursor, 3), out var abbrev) || !TryGetMonthNumber(abbrev, out month))
        {
            return false;
        }

        cursor += 3;
        return true;
    }

    private static bool TryConsumeByte(byte[] payload, ref int cursor, byte expected)
    {
        if (cursor >= payload.Length || payload[cursor] != expected)
        {
            return false;
        }

        cursor++;
        return true;
    }

    /// <summary>
    /// dd: 先頭が空白または数字、2 桁目は数字（RFC 3164 §4.1.2「1 桁なら空白 + 数字」）。
    /// </summary>
    private static bool TryReadDay(byte[] payload, ref int cursor, out int day)
    {
        day = 0;
        if (cursor + 2 > payload.Length)
        {
            return false;
        }

        var dayTens = payload[cursor];
        var dayOnes = payload[cursor + 1];
        if ((dayTens != (byte)' ' && (dayTens is < (byte)'0' or > (byte)'9')) || dayOnes is < (byte)'0' or > (byte)'9')
        {
            return false;
        }

        day = (dayTens == (byte)' ' ? 0 : (dayTens - (byte)'0') * 10) + (dayOnes - (byte)'0');
        if (day is < 1 or > 31)
        {
            return false;
        }

        cursor += 2;
        return true;
    }

    /// <summary>
    /// 4 桁の数字を年として読み取る（Issue #135 の 4 桁年変種）。数字 4 個の連続が見えなければ
    /// <paramref name="cursor"/> を変更せず false を返す（通常形式の <c>hh:mm:ss</c> は 3 文字目が
    /// <c>:</c> のため取り違えない）。
    /// </summary>
    private static bool TryReadFourDigitYear(byte[] payload, ref int cursor, out int year)
    {
        year = 0;
        if (cursor + 4 > payload.Length)
        {
            return false;
        }

        if (!AllDigits(payload.AsSpan(cursor, 4)))
        {
            return false;
        }

        year = ((payload[cursor] - (byte)'0') * 1000)
            + ((payload[cursor + 1] - (byte)'0') * 100)
            + ((payload[cursor + 2] - (byte)'0') * 10)
            + (payload[cursor + 3] - (byte)'0');
        cursor += 4;
        return true;
    }

    /// <summary>
    /// <c>hh:mm:ss</c> + 任意の小数秒（<c>.</c> + 1 桁以上の数字。Issue #135 の msec 拡張）を読み取る。
    /// 小数部は tick 精度（7 桁）へ正規化する——7 桁を超える分は切り捨て、7 桁未満は末尾を 0 埋め
    /// する（例: <c>.345</c> → 3,450,000 ticks = 0.345 秒）。
    /// </summary>
    private static bool TryReadTimeOfDay(
        byte[] payload, ref int cursor, out int hour, out int minute, out int second, out long fractionTicks)
    {
        hour = minute = second = 0;
        fractionTicks = 0;

        if (cursor + 8 > payload.Length)
        {
            return false;
        }

        if (payload[cursor + 2] != (byte)':' || payload[cursor + 5] != (byte)':')
        {
            return false;
        }

        if (!TryReadTwoDigits(payload[cursor], payload[cursor + 1], out hour) || hour > 23)
        {
            return false;
        }

        if (!TryReadTwoDigits(payload[cursor + 3], payload[cursor + 4], out minute) || minute > 59)
        {
            return false;
        }

        if (!TryReadTwoDigits(payload[cursor + 6], payload[cursor + 7], out second) || second > 59)
        {
            return false;
        }

        cursor += 8;

        if (cursor >= payload.Length || payload[cursor] != (byte)'.')
        {
            return true; // 小数秒なし
        }

        var fracStart = cursor + 1;
        var fracCursor = fracStart;
        while (fracCursor < payload.Length && payload[fracCursor] is >= (byte)'0' and <= (byte)'9')
        {
            fracCursor++;
        }

        var digitCount = fracCursor - fracStart;
        if (digitCount == 0)
        {
            return true; // '.' 直後が数字でない——小数秒とみなさず '.' も消費しない
        }

        const int TickDigits = 7; // 1 tick = 100ns = 10^-7 秒
        var usedDigits = Math.Min(digitCount, TickDigits);
        long fracValue = 0;
        for (var i = 0; i < usedDigits; i++)
        {
            fracValue = (fracValue * 10) + (payload[fracStart + i] - (byte)'0');
        }

        for (var i = usedDigits; i < TickDigits; i++)
        {
            fracValue *= 10;
        }

        fractionTicks = fracValue;
        cursor = fracCursor; // 桁あふれ分（8 桁目以降）も含め小数部全体を消費する
        return true;
    }

    /// <summary>
    /// TIMESTAMP 直後の SP + トークンを送信元付記のタイムゾーンとして解決を試みる
    /// （Issue #135。Cisco <c>show-timezone</c>）。数値オフセット（<c>+HH:MM</c>/<c>+HHMM</c>/
    /// <c>Z</c>）または <see cref="KnownTimeZoneAbbreviations"/> に載る曖昧でない略号のみを
    /// 解決対象とする。認識できないトークンは TZ とみなさず <paramref name="cursor"/> を変更しない
    /// ——SP を含め未消費のまま返すことで、既存の HOSTNAME 解析（次の SP まで）にそのまま委ねる
    /// （判断表 #3164-2 の据え置き。RFC 3164 §5.4 Example3 の "CST" が HOSTNAME として読まれる
    /// 既存挙動を壊さない）。
    /// </summary>
    private static TimeSpan? TryReadVendorTimeZone(byte[] payload, ref int cursor)
    {
        if (cursor >= payload.Length || payload[cursor] != (byte)' ')
        {
            return null;
        }

        var tokenStart = cursor + 1;
        var tokenEnd = tokenStart;
        while (tokenEnd < payload.Length && payload[tokenEnd] != (byte)' ')
        {
            tokenEnd++;
        }

        if (tokenEnd == tokenStart)
        {
            return null;
        }

        var token = payload.AsSpan(tokenStart, tokenEnd - tokenStart);

        if (TryParseNumericTimeZoneOffset(token, out var numericOffset))
        {
            cursor = tokenEnd;
            return numericOffset;
        }

        if (TryParseKnownTimeZoneAbbreviation(token, out var abbreviationOffset))
        {
            cursor = tokenEnd;
            return abbreviationOffset;
        }

        return null;
    }

    /// <summary>
    /// <c>+HH:MM</c> / <c>+HHMM</c> / <c>Z</c> 形式の数値 UTC オフセットを解析する。
    /// </summary>
    private static bool TryParseNumericTimeZoneOffset(ReadOnlySpan<byte> token, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;

        if (token.Length == 1 && (token[0] == (byte)'Z' || token[0] == (byte)'z'))
        {
            return true;
        }

        if (token.Length is not (5 or 6))
        {
            return false;
        }

        var sign = token[0];
        if (sign != (byte)'+' && sign != (byte)'-')
        {
            return false;
        }

        int hours, minutes;
        if (token.Length == 5) // +HHMM
        {
            if (!AllDigits(token[1..5]))
            {
                return false;
            }

            hours = ((token[1] - (byte)'0') * 10) + (token[2] - (byte)'0');
            minutes = ((token[3] - (byte)'0') * 10) + (token[4] - (byte)'0');
        }
        else // +HH:MM
        {
            if (token[3] != (byte)':' || !AllDigits(token[1..3]) || !AllDigits(token[4..6]))
            {
                return false;
            }

            hours = ((token[1] - (byte)'0') * 10) + (token[2] - (byte)'0');
            minutes = ((token[4] - (byte)'0') * 10) + (token[5] - (byte)'0');
        }

        if (hours > 14 || minutes > 59) // UTC オフセットの実在範囲（RFC 3339 と同じ安全弁）
        {
            return false;
        }

        var magnitude = new TimeSpan(hours, minutes, 0);
        offset = sign == (byte)'-' ? -magnitude : magnitude;
        return true;
    }

    /// <summary>
    /// 曖昧でない TZ 略号のみを既知集合とする（Issue #135）。<b>意図的に含めない</b>: 米国の
    /// <c>CST</c>/<c>EST</c>/<c>PST</c>/<c>MST</c> 等は Cisco <c>clock timezone &lt;name&gt; &lt;offset&gt;</c>
    /// が任意の名称を任意のオフセットに割り当てられるため、略号だけからオフセットを断定できない
    /// （CLAUDE.md「技術的な同等性の主張は推測で書かない」の適用——確証のない略号を推測で
    /// マッピングしない）。未知の略号は <see cref="TryReadVendorTimeZone"/> が TZ として消費せず、
    /// 既存の HOSTNAME 解析にそのまま委ねる。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, TimeSpan> KnownTimeZoneAbbreviations =
        new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
        {
            ["UTC"] = TimeSpan.Zero,
            ["GMT"] = TimeSpan.Zero,
            ["JST"] = TimeSpan.FromHours(9), // Issue #135 の具体例（Cisco show-timezone）。DST 無し。
        };

    private static bool TryParseKnownTimeZoneAbbreviation(ReadOnlySpan<byte> token, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;

        if (token.Length is 0 or > 6)
        {
            return false;
        }

        foreach (var b in token)
        {
            if (b is < (byte)'A' or > (byte)'Z')
            {
                return false; // 大文字のみを候補とする（Cisco show-timezone の出力形式）
            }
        }

        return KnownTimeZoneAbbreviations.TryGetValue(Encoding.ASCII.GetString(token), out offset);
    }

    private static bool AllDigits(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b is < (byte)'0' or > (byte)'9')
            {
                return false;
            }
        }

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
