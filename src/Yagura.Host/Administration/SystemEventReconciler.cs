using Yagura.Storage;

namespace Yagura.Host.Administration;

/// <summary>
/// 旧ファイルのシステムイベント（受信できなかった時間帯の記録・保存期間削除の記録等。database.md §2.3）が
/// 移行先にあるかの照合。移行（§6.2）の複写と、処分（§6.3）の完了判定が同じ規則を使う。
/// </summary>
/// <remarks>
/// 照合の鍵は種別・区間（UTC の tick 精度）・近似の印。Id は provider が採番し直し、付帯情報は provider 間で
/// 空文字と NULL の扱いが揺れ得るため鍵に含めない。同じ鍵のイベントは件数で数える（多重集合の差）。
/// </remarks>
public static class SystemEventReconciler
{
    /// <summary>移行先の照合に使う読み出しの上限（件数がこれに達したら取りこぼし得るものとして扱う）。</summary>
    internal const int TargetQueryLimit = 1_000_000;

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromMinutes(5);

    /// <summary><paramref name="source"/> のうち、移行先に無いイベントを返す。</summary>
    public static async Task<IReadOnlyList<SystemEvent>> FindMissingInTargetAsync(
        IReadOnlyList<SystemEvent> source,
        ILogStore target,
        CancellationToken cancellationToken)
    {
        if (source.Count == 0)
        {
            return [];
        }

        var from = source.Min(e => e.StartAt);
        var to = source.Max(e => e.EndAt);
        var targetEvents = await target.QuerySystemEventsAsync(
            from, to, TargetQueryLimit, QueryTimeout, kind: null, cancellationToken).ConfigureAwait(false);

        var available = new Dictionary<EventKey, int>();
        foreach (var targetEvent in targetEvents)
        {
            var key = EventKey.Of(targetEvent);
            available[key] = available.GetValueOrDefault(key) + 1;
        }

        var missing = new List<SystemEvent>();
        foreach (var sourceEvent in source)
        {
            var key = EventKey.Of(sourceEvent);
            if (available.TryGetValue(key, out var count) && count > 0)
            {
                available[key] = count - 1;
            }
            else
            {
                missing.Add(sourceEvent);
            }
        }

        return missing;
    }

    private readonly record struct EventKey(string Kind, long StartTicks, long EndTicks, bool Approximate)
    {
        public static EventKey Of(SystemEvent systemEvent) => new(
            systemEvent.Kind,
            systemEvent.StartAt.UtcTicks,
            systemEvent.EndAt.UtcTicks,
            systemEvent.Approximate);
    }
}
