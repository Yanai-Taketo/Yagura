namespace Yagura.Storage;

/// <summary>
/// ReceivedAt の 1 時間ごとの件数集計（database.md §1.2「契約拡張の予約」の集計。§6.3 の
/// 移行の完了判定——時間帯ごとの照合——用）。<see cref="IBulkLogReader"/> と同じく
/// <see cref="ILogStore"/> 本体には載せない optional 契約であり、呼び出し経路は管理操作に限る。
/// provider が対応するかは <c>store is IHourlyLogCountReader</c> で判定する。
/// </summary>
/// <remarks>
/// 時間帯の境界は UTC の正時（分以下を切り捨てた時刻）。件数 0 の時間帯は返さない。
/// </remarks>
public interface IHourlyLogCountReader
{
    /// <summary>
    /// <paramref name="fromInclusive"/> 以上 <paramref name="toExclusive"/> 未満の ReceivedAt を持つ
    /// レコードを、UTC の正時ごとに数える。<see langword="null"/> はその側の境界なし。
    /// </summary>
    Task<IReadOnlyList<HourlyLogCount>> CountByHourAsync(
        DateTimeOffset? fromInclusive,
        DateTimeOffset? toExclusive,
        CancellationToken cancellationToken = default);
}

/// <summary>1 時間ぶんの件数。</summary>
/// <param name="HourStartUtc">時間帯の開始（UTC の正時）。</param>
/// <param name="Count">その時間帯の件数。</param>
public sealed record HourlyLogCount(DateTimeOffset HourStartUtc, long Count);
