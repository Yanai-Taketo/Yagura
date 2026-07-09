using Yagura.Web.Components.Common;
using Yagura.Web.Components.Pages;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// 装置時計ずれの明示（ui.md §6・Issue #158）の注記組み立てロジックの検証。
/// </summary>
/// <remarks>
/// 詳細オーバーレイ全体の描画確認は <see cref="ViewerPageRenderTests"/> が担うが、そちらの
/// <see cref="CommonComponentRenderHarness"/> は初期描画（prerender 相当）のみを行うため、
/// 行クリックで開く詳細ダイアログの中身（本注記を含む）までは検証できない
/// （CommonComponentRenderHarness のコメント参照）。そのため注記の組み立てを
/// <see cref="LogSearch.BuildDeviceTimestampDriftNote"/>（internal——
/// <c>InternalsVisibleTo</c> 経由でテストから直接参照）として切り出し、
/// <see cref="YaguraTimeFormatterTests"/> と同じ形式（純粋関数の直接呼び出し）で検証する。
/// </remarks>
public sealed class LogSearchDeviceTimestampDriftTests
{
    private static readonly DateTimeOffset ReceivedAt = new(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BuildNote_DeviceTimestampMissing_ReturnsNull()
    {
        var note = LogSearch.BuildDeviceTimestampDriftNote(ReceivedAt, deviceTimestamp: null);

        Assert.Null(note);
    }

    [Fact]
    public void BuildNote_DriftBelowThreshold_ReturnsNull()
    {
        // 閾値（仮値 5 分。UI-2）未満は注記しない。
        var deviceTimestamp = ReceivedAt + TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59);

        var note = LogSearch.BuildDeviceTimestampDriftNote(ReceivedAt, deviceTimestamp);

        Assert.Null(note);
    }

    [Fact]
    public void BuildNote_DriftExactlyAtThreshold_ReturnsNote()
    {
        var deviceTimestamp = ReceivedAt + LogSearch.DeviceTimestampDriftThreshold;

        var note = LogSearch.BuildDeviceTimestampDriftNote(ReceivedAt, deviceTimestamp);

        Assert.Equal(
            string.Format(UiText.DetailDeviceTimestampDriftAheadFormat, "5 分"),
            note);
    }

    [Fact]
    public void BuildNote_DeviceAheadByHoursAndMinutes_ReturnsAheadFormatWithHoursAndMinutes()
    {
        // 装置時刻がサーバ受信より進んでいる（例: Issue #158 の「約 5 時間進んでいます」相当の形）。
        var deviceTimestamp = ReceivedAt + TimeSpan.FromHours(2) + TimeSpan.FromMinutes(30);

        var note = LogSearch.BuildDeviceTimestampDriftNote(ReceivedAt, deviceTimestamp);

        Assert.Equal(
            string.Format(UiText.DetailDeviceTimestampDriftAheadFormat, "2 時間 30 分"),
            note);
    }

    [Fact]
    public void BuildNote_DeviceBehindByDaysAndHours_ReturnsBehindFormatWithDaysAndHours()
    {
        // 装置時刻がサーバ受信より遅れている（RTC 切れ等で過去の時刻を刻んだ想定）。
        var deviceTimestamp = ReceivedAt - TimeSpan.FromDays(1) - TimeSpan.FromHours(3);

        var note = LogSearch.BuildDeviceTimestampDriftNote(ReceivedAt, deviceTimestamp);

        Assert.Equal(
            string.Format(UiText.DetailDeviceTimestampDriftBehindFormat, "1 日 3 時間"),
            note);
    }

    [Fact]
    public void BuildNote_DeviceTimestampSameInstantDifferentOffset_ReturnsNull()
    {
        // DeviceTimestamp は絶対時点として比較する——表現上のオフセットが違うだけで実際には
        // 同じ瞬間を指す場合は乖離ゼロ（誤警告にならない。ui.md §6「タイムゾーンの扱いに注意」）。
        var sameInstantDifferentOffset = ReceivedAt.ToOffset(TimeSpan.FromHours(9));

        var note = LogSearch.BuildDeviceTimestampDriftNote(ReceivedAt, sameInstantDifferentOffset);

        Assert.Null(note);
    }

    [Fact]
    public void BuildNote_DeviceTimestampOffsetRepresentation_DoesNotAffectDriftMagnitude()
    {
        // 実際に 9 時間 30 分先行している装置時刻を、別オフセットの表現で渡しても
        // （DateTimeOffset の差分演算は時点基準のため）同じ乖離量になる。
        var trueInstant = ReceivedAt + TimeSpan.FromHours(9) + TimeSpan.FromMinutes(30);
        var deviceTimestamp = trueInstant.ToOffset(TimeSpan.FromHours(9));

        var note = LogSearch.BuildDeviceTimestampDriftNote(ReceivedAt, deviceTimestamp);

        Assert.Equal(
            string.Format(UiText.DetailDeviceTimestampDriftAheadFormat, "9 時間 30 分"),
            note);
    }
}
