using System.Net;
using Microsoft.Extensions.Time.Testing;
using Yagura.Host.Configuration;
using Yagura.Host.Observability.ActiveNotification.SourceSilence;

namespace Yagura.Host.Tests.Observability.ActiveNotification.SourceSilence;

/// <summary>
/// <see cref="SourceActivityTracker"/>（ADR-0018 決定 3・委任 7）の単体テスト。
/// </summary>
/// <remarks>
/// 本クラスが固定する性質:
/// (1) <b>有界性</b>——未登録送信元の受信が追跡構造に一切影響しない（受け入れ基準）、
/// (2) <b>時刻が後退しない</b>——drain 合流点・seed が運ぶ過去の実績が、より新しい実受信を
///     引き戻さない（委任 7 の <c>max()</c> 更新）、
/// (3) <b>ウォッチリスト差し替えで既存エントリの追跡状態が保持される</b>（決定 6）。
/// </remarks>
public sealed class SourceActivityTrackerTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);

    private static SourceSilenceWatchEntry Entry(string address, int thresholdMinutes = 60) =>
        new(IPAddress.Parse(address), null, TimeSpan.FromMinutes(thresholdMinutes), false);

    private static (SourceActivityTracker Tracker, FakeTimeProvider Time) Create(params string[] addresses)
    {
        var time = new FakeTimeProvider(Origin);
        var tracker = new SourceActivityTracker(time);
        tracker.ApplyWatchlist([.. addresses.Select(a => Entry(a))]);
        return (tracker, time);
    }

    // ------------------------------------------------------------------
    // (1) 有界性
    // ------------------------------------------------------------------

    [Fact]
    public void RecordActivity_UnwatchedSource_DoesNotAffectTracking()
    {
        // 送信元アドレスを変えながら送るだけでメモリを食い潰せる作りにしない。
        var (tracker, _) = Create("192.0.2.10");

        for (var i = 0; i < 5000; i++)
        {
            tracker.RecordActivity(IPAddress.Parse($"10.{i / 65536 % 256}.{i / 256 % 256}.{i % 256}"));
        }

        Assert.Equal(1, tracker.TrackedCount);
        Assert.Null(tracker.GetElapsedSinceLastActivity(IPAddress.Parse("10.0.0.1")));
    }

    [Fact]
    public void ApplyWatchlist_Null_DisablesTrackingEntirely()
    {
        var (tracker, _) = Create("192.0.2.10");

        tracker.ApplyWatchlist(null);

        Assert.Equal(0, tracker.TrackedCount);
        Assert.Null(tracker.GetElapsedSinceLastActivity(IPAddress.Parse("192.0.2.10")));
    }

    // ------------------------------------------------------------------
    // 基本の追跡（単調クロック基準）
    // ------------------------------------------------------------------

    [Fact]
    public void RecordActivity_ResetsElapsedTime()
    {
        var (tracker, time) = Create("192.0.2.10");
        var address = IPAddress.Parse("192.0.2.10");

        time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(TimeSpan.FromMinutes(30), tracker.GetElapsedSinceLastActivity(address));

        tracker.RecordActivity(address);
        Assert.Equal(TimeSpan.Zero, tracker.GetElapsedSinceLastActivity(address));

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.FromMinutes(5), tracker.GetElapsedSinceLastActivity(address));
    }

    [Fact]
    public void NewEntry_StartsFromRegistrationTime()
    {
        // 決定 3: 一度も受信のない送信元は「登録時点」を仮の最終受信時刻とし、閾値経過で発火する。
        // これは仕様——「先回りで登録したが機器側の設定が済んでいない」を検出する機会になる。
        var (tracker, time) = Create("192.0.2.10");

        time.Advance(TimeSpan.FromMinutes(90));

        Assert.Equal(TimeSpan.FromMinutes(90), tracker.GetElapsedSinceLastActivity(IPAddress.Parse("192.0.2.10")));
    }

    [Fact]
    public void RecordActivity_NormalizesIPv4MappedIPv6()
    {
        var (tracker, time) = Create("192.0.2.10");
        time.Advance(TimeSpan.FromMinutes(10));

        // IPv4-mapped IPv6 で届いても、同じエントリとして数える（2 キーに割れない）。
        tracker.RecordActivity(IPAddress.Parse("::ffff:192.0.2.10"));

        Assert.Equal(TimeSpan.Zero, tracker.GetElapsedSinceLastActivity(IPAddress.Parse("192.0.2.10")));
    }

    // ------------------------------------------------------------------
    // (2) 時刻が後退しない（委任 7 の max() 更新）
    // ------------------------------------------------------------------

    [Fact]
    public void RecordHistoricalActivity_DoesNotPullBackANewerRealReceipt()
    {
        // drain 合流点が運ぶのは「過去の受信実績」。単純代入だと、今受信したばかりの装置が
        // 過去へ引き戻され、途絶に見える（lost update）。
        var (tracker, time) = Create("192.0.2.10");
        var address = IPAddress.Parse("192.0.2.10");

        time.Advance(TimeSpan.FromMinutes(60));
        tracker.RecordActivity(address); // 今まさに受信

        // 30 分前のスプール滞留レコードが遅れて合流する。
        tracker.RecordHistoricalActivity(address, time.GetUtcNow() - TimeSpan.FromMinutes(30));

        Assert.Equal(TimeSpan.Zero, tracker.GetElapsedSinceLastActivity(address));
    }

    [Fact]
    public void RecordHistoricalActivity_AdvancesWhenItIsNewerThanTheCurrentValue()
    {
        // 逆向き: 深いスプール滞留 + 再起動で「直前まで送っていた装置」が途絶に見える偽陽性を
        // 塞ぐのが drain 更新の目的なので、より新しければ前進しなければならない。
        var (tracker, time) = Create("192.0.2.10");
        var address = IPAddress.Parse("192.0.2.10");

        time.Advance(TimeSpan.FromMinutes(120));
        tracker.RecordHistoricalActivity(address, time.GetUtcNow() - TimeSpan.FromMinutes(10));

        Assert.Equal(TimeSpan.FromMinutes(10), tracker.GetElapsedSinceLastActivity(address));
    }

    [Fact]
    public void RecordHistoricalActivity_FutureWallClock_IsClampedToNow()
    {
        // 時計のずれた送信元・クロックスキューで最終受信時刻が未来に置かれると、
        // 永久に途絶しないエントリが生まれる。負の経過は 0 に clamp する（決定 3）。
        var (tracker, time) = Create("192.0.2.10");
        var address = IPAddress.Parse("192.0.2.10");

        time.Advance(TimeSpan.FromMinutes(60));
        tracker.RecordHistoricalActivity(address, time.GetUtcNow() + TimeSpan.FromDays(365));

        Assert.Equal(TimeSpan.Zero, tracker.GetElapsedSinceLastActivity(address));

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.FromMinutes(5), tracker.GetElapsedSinceLastActivity(address));
    }

    [Fact]
    public void Seed_AnchorsWallClockOntoTheMonotonicTimeline()
    {
        var (tracker, time) = Create("192.0.2.10");
        var address = IPAddress.Parse("192.0.2.10");

        time.Advance(TimeSpan.FromHours(10));
        tracker.Seed(address, time.GetUtcNow() - TimeSpan.FromHours(3));

        Assert.Equal(TimeSpan.FromHours(3), tracker.GetElapsedSinceLastActivity(address));
    }

    [Fact]
    public void Seed_UnwatchedSource_IsIgnored()
    {
        var (tracker, _) = Create("192.0.2.10");

        tracker.Seed(IPAddress.Parse("192.0.2.99"), Origin);

        Assert.Equal(1, tracker.TrackedCount);
    }

    // ------------------------------------------------------------------
    // (3) ウォッチリスト差し替え（決定 6）
    // ------------------------------------------------------------------

    [Fact]
    public void ApplyWatchlist_KeepsStateOfSurvivingEntries_AndDropsRemovedOnes()
    {
        // 保持しないと、設定を触るたびに全エントリが「登録時点基準」へ戻り、長い閾値の
        // エントリが実質永久に発火しなくなる。
        var (tracker, time) = Create("192.0.2.10", "192.0.2.11");
        var kept = IPAddress.Parse("192.0.2.10");

        time.Advance(TimeSpan.FromMinutes(45));
        tracker.RecordActivity(kept);
        time.Advance(TimeSpan.FromMinutes(15));

        // 192.0.2.11 を外し、192.0.2.12 を追加する。
        tracker.ApplyWatchlist([Entry("192.0.2.10"), Entry("192.0.2.12")]);

        // 生き残ったエントリの追跡状態は保たれる。
        Assert.Equal(TimeSpan.FromMinutes(15), tracker.GetElapsedSinceLastActivity(kept));
        // 削除されたエントリは破棄される。
        Assert.Null(tracker.GetElapsedSinceLastActivity(IPAddress.Parse("192.0.2.11")));
        // 追加されたエントリは登録時点基準で始まる。
        Assert.Equal(TimeSpan.Zero, tracker.GetElapsedSinceLastActivity(IPAddress.Parse("192.0.2.12")));
    }

    // ------------------------------------------------------------------
    // 並行更新（委任 7 の CAS ループ）
    // ------------------------------------------------------------------

    [Fact]
    public void ConcurrentWriters_NeverMoveTheTimestampBackwards()
    {
        // 実クロックで 2 系統の書き手を競合させ、最終値が「最も新しい実受信」以上であることを
        // 確認する。単純代入だと過去の実績で上書きされ得る。
        var tracker = new SourceActivityTracker();
        var address = IPAddress.Parse("192.0.2.10");
        tracker.ApplyWatchlist([Entry("192.0.2.10")]);

        var historical = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);

        Parallel.For(0, 2000, i =>
        {
            if (i % 2 == 0)
            {
                tracker.RecordActivity(address);
            }
            else
            {
                tracker.RecordHistoricalActivity(address, historical);
            }
        });

        var elapsed = tracker.GetElapsedSinceLastActivity(address);

        Assert.NotNull(elapsed);
        // 1 時間前の実績で引き戻されていないこと（十分に緩い上界で判定する）。
        Assert.True(elapsed < TimeSpan.FromMinutes(1), $"時刻が後退している可能性: elapsed={elapsed}");
    }
}
