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
/// **解析失敗時のフィールド保持**: ParseFailed になった原因より手前で既に
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
public static partial class SyslogParser
{
    /// <summary>
    /// 受信済みの生データグラムを解析し、<see cref="LogRecord"/>（挿入前・Id 未採番）を返す。
    /// </summary>
    /// <param name="datagram">受信済みの生データグラム。</param>
    /// <param name="defaultRfc3164TimeZone">
    /// RFC 3164 TIMESTAMP（年・タイムゾーンを持たない）の解釈に使う既定タイムゾーン
    /// （configuration.md の <c>Ingestion:Rfc3164:DefaultTimeZone</c>）。
    /// <see langword="null"/> は UTC（<see cref="TimeZoneInfo.Utc"/>）——本引数を省略した既存の
    /// 呼び出し元との後方互換を保つ既定値。<b>優先順位</b>: TIMESTAMP に送信元付記の
    /// タイムゾーン（Cisco の <c>show-timezone</c> 等）が取れた場合はそちらを優先し、
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
}
