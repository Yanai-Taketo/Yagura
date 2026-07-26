using Yagura.Web.Components.Common;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// 時刻表示の契約（ui.md §6）の整形検証（M8-2。Issue #69）: UTC 保存値を指定タイムゾーンで
/// ローカル化し、そのレコードの時刻時点のオフセットを明示する。
/// </summary>
/// <remarks>
/// タイムゾーンは <see cref="TimeZoneInfo.CreateCustomTimeZone(string, TimeSpan, string, string)"/>
/// で固定する（CI 実行環境の OS タイムゾーン・登録済みゾーン ID に依存しない）。
/// </remarks>
public sealed class YaguraTimeFormatterTests
{
    [Fact]
    public void FormatLocal_PositiveOffset_MatchesContractExample()
    {
        // ui.md §6 の表示例そのもの: 2026-07-04 21:05:11 (UTC+09:00)
        var timeZone = TimeZoneInfo.CreateCustomTimeZone("Test+09", TimeSpan.FromHours(9), "Test+09", "Test+09");
        var utc = new DateTimeOffset(2026, 7, 4, 12, 5, 11, TimeSpan.Zero);

        var formatted = YaguraTimeFormatter.FormatLocal(utc, timeZone);

        Assert.Equal("2026-07-04 21:05:11 (UTC+09:00)", formatted);
    }

    [Fact]
    public void FormatLocal_NegativeOffset_ShowsMinusSign()
    {
        var timeZone = TimeZoneInfo.CreateCustomTimeZone("Test-05", TimeSpan.FromHours(-5), "Test-05", "Test-05");
        var utc = new DateTimeOffset(2026, 1, 2, 3, 30, 0, TimeSpan.Zero);

        var formatted = YaguraTimeFormatter.FormatLocal(utc, timeZone);

        Assert.Equal("2026-01-01 22:30:00 (UTC-05:00)", formatted);
    }

    [Fact]
    public void FormatLocal_UtcZone_ShowsPlusZero()
    {
        var timeZone = TimeZoneInfo.CreateCustomTimeZone("Test+00", TimeSpan.Zero, "Test+00", "Test+00");
        var utc = new DateTimeOffset(2026, 7, 4, 12, 5, 11, TimeSpan.Zero);

        var formatted = YaguraTimeFormatter.FormatLocal(utc, timeZone);

        Assert.Equal("2026-07-04 12:05:11 (UTC+00:00)", formatted);
    }

    [Fact]
    public void FormatLocal_NonWholeHourOffset_ShowsMinutes()
    {
        // 30 分単位のオフセット（例: インド標準時相当 +05:30）でも一意に読めること。
        var timeZone = TimeZoneInfo.CreateCustomTimeZone("Test+0530", new TimeSpan(5, 30, 0), "Test+0530", "Test+0530");
        var utc = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

        var formatted = YaguraTimeFormatter.FormatLocal(utc, timeZone);

        Assert.Equal("2026-07-04 17:30:00 (UTC+05:30)", formatted);
    }

    [Fact]
    public void FormatLocal_NonUtcInput_IsNormalizedByInstant()
    {
        // 入力がオフセット付きでも「時点」として扱う（同一時点なら同一表示）。
        var timeZone = TimeZoneInfo.CreateCustomTimeZone("Test+09", TimeSpan.FromHours(9), "Test+09", "Test+09");
        var instantAsUtc = new DateTimeOffset(2026, 7, 4, 12, 5, 11, TimeSpan.Zero);
        var sameInstantWithOffset = new DateTimeOffset(2026, 7, 4, 7, 5, 11, TimeSpan.FromHours(-5));

        Assert.Equal(
            YaguraTimeFormatter.FormatLocal(instantAsUtc, timeZone),
            YaguraTimeFormatter.FormatLocal(sameInstantWithOffset, timeZone));
    }

    [Fact]
    public void FormatLocal_JstAccountInventoryTimestamp_ShowsLocalWallClock_NotUtc()
    {
        // Issue #462 の発端そのもの: アカウント点検の最終ログイン `2026-07-26T06:18:26Z` を
        // JST のサーバで表示すると **15:18:26** でなければならない。UTC 生表記（06:18）だと
        // 「心当たりのないアカウントか」を判断する材料が 9 時間ずれて読める。
        var jst = TimeZoneInfo.CreateCustomTimeZone("Test+09", TimeSpan.FromHours(9), "Test+09", "Test+09");
        var lastLogin = new DateTimeOffset(2026, 7, 26, 6, 18, 26, TimeSpan.Zero);

        var formatted = YaguraTimeFormatter.FormatLocal(lastLogin, jst);

        Assert.Equal("2026-07-26 15:18:26 (UTC+09:00)", formatted);
        Assert.DoesNotContain("06:18", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatLocalDateRange_ShowsBothDatesAndASingleOffset()
    {
        // 証明書の有効期間は「何日から何日まで」で読む値。日付だけを出しつつ、
        // タイムゾーンは範囲全体に 1 つだけ添える（両端に付けると冗長）。
        var jst = TimeZoneInfo.CreateCustomTimeZone("Test+09", TimeSpan.FromHours(9), "Test+09", "Test+09");
        var from = new DateTimeOffset(2026, 7, 4, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2031, 7, 3, 15, 0, 0, TimeSpan.Zero);

        var formatted = YaguraTimeFormatter.FormatLocalDateRange(from, to, jst);

        Assert.Equal("2026-07-04 〜 2031-07-04 (UTC+09:00)", formatted);
    }

    [Fact]
    public void FormatLocalDateRange_LocalisesTheDateItself_NotJustTheClock()
    {
        // 日付だけの表示でもタイムゾーンの明示が要る理由: 同じ瞬間でも日付が 1 日ずれる。
        // UTC で 2026-07-04 23:30 は JST では翌 05 日である。
        var jst = TimeZoneInfo.CreateCustomTimeZone("Test+09", TimeSpan.FromHours(9), "Test+09", "Test+09");
        var utcLateEvening = new DateTimeOffset(2026, 7, 4, 23, 30, 0, TimeSpan.Zero);

        var formatted = YaguraTimeFormatter.FormatLocalDateRange(utcLateEvening, utcLateEvening, jst);

        Assert.StartsWith("2026-07-05 〜 2026-07-05", formatted, StringComparison.Ordinal);
    }
}
