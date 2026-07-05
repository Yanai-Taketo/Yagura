namespace Yagura.Web.Components.Common;

/// <summary>
/// 時間軸チャート（<see cref="YaguraTimelineChart"/>）の 1 区切り分の受信量（M8-3）。
/// </summary>
/// <param name="Start">区切りの開始（UTC）。</param>
/// <param name="End">区切りの終了（UTC）。</param>
/// <param name="Count">この区切りの受信件数。</param>
public sealed record YaguraTimelineBucket(DateTimeOffset Start, DateTimeOffset End, int Count);

/// <summary>
/// 時間軸チャート上に重ねる受信断区間（architecture.md §4.4・ui.md §5.3。M8-3）。
/// </summary>
/// <param name="Start">区間の開始（UTC）。</param>
/// <param name="End">区間の終了（UTC）。</param>
/// <param name="Approximate">クラッシュ由来の近似断点か（ui.md §5.3——近似はその旨を印す）。</param>
public sealed record YaguraTimelineInterval(DateTimeOffset Start, DateTimeOffset End, bool Approximate);
