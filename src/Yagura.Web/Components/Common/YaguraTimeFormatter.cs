using System.Globalization;

namespace Yagura.Web.Components.Common;

/// <summary>
/// 時刻表示の契約（ui.md §6）の実装位置（M8-2）: 保存は UTC（database.md §2.2）、
/// 表示はすべてサーバの OS タイムゾーンでローカル化し、タイムゾーンを画面に明示する。
/// </summary>
/// <remarks>
/// <para>
/// 表示例: <c>2026-07-04 21:05:11 (UTC+09:00)</c>。オフセット表記は<b>そのレコードの
/// 時刻時点のオフセット</b>とする（夏時間のあるタイムゾーンでも一意に読める。ui.md §6）。
/// </para>
/// <para>
/// <b>閲覧者ごとのタイムゾーン変換はしない</b>（ui.md §6 の決定——閲覧リスナは認証なしで
/// 閲覧者の概念がなく、表示の一意性が実装も検証も単純にする）。画面の時刻表示は本クラス
/// （または <see cref="YaguraTimestamp"/> コンポーネント）経由に統一し、各画面が独自の
/// 書式・変換を持たない。
/// </para>
/// </remarks>
public static class YaguraTimeFormatter
{
    /// <summary>
    /// UTC 時刻をサーバの OS タイムゾーンでローカル化し、オフセットを明示して整形する。
    /// </summary>
    /// <param name="instant">整形対象の時刻（UTC 保存値。オフセット付きでも UTC 換算で扱う）。</param>
    public static string FormatLocal(DateTimeOffset instant) => FormatLocal(instant, TimeZoneInfo.Local);

    /// <summary>
    /// UTC 時刻を指定タイムゾーンでローカル化し、オフセットを明示して整形する
    /// （タイムゾーンを引数に取るのはテスト可能性のため。本番経路はサーバ OS タイムゾーン）。
    /// </summary>
    public static string FormatLocal(DateTimeOffset instant, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        var local = TimeZoneInfo.ConvertTime(instant, timeZone);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{local:yyyy-MM-dd HH:mm:ss} ({FormatOffset(local.Offset)})");
    }

    /// <summary>
    /// 証明書の有効期間のような<b>日付だけを見せたい範囲</b>を整形する
    /// （例: <c>2026-07-04 〜 2027-07-04 (UTC+09:00)</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>なぜ専用のメソッドを置くのか</b>（Issue #462）: 有効期間は「何日から何日まで」で読む値であり、
    /// 秒まで出すと桁が増えて比較しづらい。しかし画面ごとに <c>ToString("yyyy-MM-dd")</c> を書くと
    /// ui.md §6 が禁じる「画面が独自の書式・変換を持つ」状態に戻る——**日付だけの表示も契約の側に
    /// 引き取る**ことで、各画面から時刻整形を消し切る。
    /// </para>
    /// <para>
    /// <b>オフセットは範囲全体に 1 つだけ添える</b>: 日付のみの表示でもタイムゾーンの明示は要る
    /// （同じ瞬間でもタイムゾーンによって日付が 1 日ずれるため）。ただし両端に付けると冗長なので、
    /// 範囲の末尾に 1 つ置く。両端でオフセットが異なる場合（夏時間を跨ぐ期間）は<b>終端側</b>を採る
    /// ——有効期限の判断は終端を見る操作であり、そちらの現地時刻に合わせるのが読み手の意図に近い。
    /// </para>
    /// </remarks>
    public static string FormatLocalDateRange(DateTimeOffset from, DateTimeOffset to) =>
        FormatLocalDateRange(from, to, TimeZoneInfo.Local);

    /// <inheritdoc cref="FormatLocalDateRange(DateTimeOffset, DateTimeOffset)"/>
    public static string FormatLocalDateRange(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        var localFrom = TimeZoneInfo.ConvertTime(from, timeZone);
        var localTo = TimeZoneInfo.ConvertTime(to, timeZone);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{localFrom:yyyy-MM-dd} 〜 {localTo:yyyy-MM-dd} ({FormatOffset(localTo.Offset)})");
    }

    /// <summary>
    /// オフセットの表示形式（例: <c>UTC+09:00</c> / <c>UTC-05:00</c> / <c>UTC+00:00</c>）。
    /// </summary>
    public static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var magnitude = offset < TimeSpan.Zero ? -offset : offset;
        return string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{magnitude:hh\\:mm}");
    }
}
