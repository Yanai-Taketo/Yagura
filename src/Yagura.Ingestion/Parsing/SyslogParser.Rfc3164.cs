using System.Text;
using Yagura.Ingestion.Udp;
using Yagura.Storage;

namespace Yagura.Ingestion.Parsing;

public static partial class SyslogParser
{
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
            // CONTENT より手前で既に確定した値は破棄しない（database.md §2.1
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
    /// 2 文字）を寛容リーダとして解析する。年・タイムゾーンを持たないため、年は
    /// 4 桁年変種（後述）が無い限り <paramref name="referenceTime"/>（= ReceivedAt。基準時刻は
    /// 1 回だけ読み取り、以後使い回す——年またぎ判定の内部で複数回現在時刻を読むと境界付近で
    /// 矛盾した年を選びうるため）から補完する（年またぎは <see cref="ResolveYear"/> 参照）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>受理する逸脱（Cisco IOS 等のベンダ拡張）</b>: いずれも「厳密な
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
    /// <b>タイムゾーンの優先順位</b>: ①TIMESTAMP に送信元付記の
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
                // ①送信元付記の TZ が最優先。
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
    /// （Cisco <c>service sequence-numbers</c>）。パターンに一致しない場合は
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
    /// 4 桁の数字を年として読み取る（4 桁年変種）。数字 4 個の連続が見えなければ
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
    /// <c>hh:mm:ss</c> + 任意の小数秒（<c>.</c> + 1 桁以上の数字。msec 拡張）を読み取る。
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
    /// （Cisco <c>show-timezone</c>）。数値オフセット（<c>+HH:MM</c>/<c>+HHMM</c>/
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
    /// 曖昧でない TZ 略号のみを既知集合とする。<b>意図的に含めない</b>: 米国の
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
            ["JST"] = TimeSpan.FromHours(9), // 具体例（Cisco show-timezone）。DST 無し。
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
}
