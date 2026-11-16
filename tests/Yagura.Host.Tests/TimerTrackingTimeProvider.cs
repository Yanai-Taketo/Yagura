using Microsoft.Extensions.Time.Testing;

namespace Yagura.Host.Tests;

/// <summary>
/// 作成されたタイマーを期限ごとに観測できる <see cref="FakeTimeProvider"/>。
/// </summary>
/// <remarks>
/// 用途は 2 つ: ①非同期の処理が「待機に入った」ことを固定 sleep なしで確定する（その待機の
/// 期限で作られたタイマーの有無で判定する）。②待ち合わせが決着したあとに、負けた側のタイマーが
/// 破棄されずに残っていないことを確かめる。
/// </remarks>
internal sealed class TimerTrackingTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
{
    private readonly object _lock = new();
    private readonly List<TrackedTimer> _timers = [];

    /// <summary>これまでに作成されたタイマーの総数（破棄済みを含む）。</summary>
    public int CreatedTimerCount
    {
        get
        {
            lock (_lock)
            {
                return _timers.Count;
            }
        }
    }

    /// <summary>期限 <paramref name="dueTime"/> で作成され、まだ破棄されていないタイマーの数。</summary>
    public int CountLiveTimers(TimeSpan dueTime)
    {
        lock (_lock)
        {
            return _timers.Count(t => !t.IsDisposed && t.InitialDueTime == dueTime);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new TrackedTimer(base.CreateTimer(callback, state, dueTime, period), dueTime);
        lock (_lock)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    private sealed class TrackedTimer(ITimer inner, TimeSpan initialDueTime) : ITimer
    {
        private int _disposed;

        public TimeSpan InitialDueTime => initialDueTime;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            inner.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            return inner.DisposeAsync();
        }
    }
}
