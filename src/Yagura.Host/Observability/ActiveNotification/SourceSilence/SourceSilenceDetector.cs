using System.Net;
using Yagura.Host.Configuration;

namespace Yagura.Host.Observability.ActiveNotification.SourceSilence;

/// <summary>1 エントリの途絶／復帰の判定結果。</summary>
/// <param name="Address">送信元アドレス。</param>
/// <param name="Label">表示名（未設定なら <see langword="null"/>）。</param>
/// <param name="Threshold">適用された実効閾値。</param>
/// <param name="Elapsed">最終受信からの経過時間。</param>
internal sealed record SourceSilenceEvent(
    IPAddress Address,
    string? Label,
    TimeSpan Threshold,
    TimeSpan Elapsed);

/// <summary>1 回の周期評価の結果。</summary>
/// <param name="Silences">
/// 個別に警告すべき途絶（<see cref="IsBurst"/> が <see langword="true"/> のときは集約警告の
/// 明細として使う——個別警告は出さない）。
/// </param>
/// <param name="IsBurst">一斉集約の閾値に達したか（決定 3）。</param>
/// <param name="Recoveries">復帰の情報記録を出すべきエントリ。</param>
internal sealed record SourceSilenceEvaluation(
    IReadOnlyList<SourceSilenceEvent> Silences,
    bool IsBurst,
    IReadOnlyList<SourceSilenceEvent> Recoveries)
{
    internal static readonly SourceSilenceEvaluation Empty = new([], false, []);

    internal bool HasAnything => Silences.Count > 0 || Recoveries.Count > 0;
}

/// <summary>
/// 途絶の判定・状態管理（ADR-0018 決定 3）。
/// </summary>
/// <remarks>
/// <para>
/// <b>新しい常駐機構は作らない</b>（ADR-0018 の検討した選択肢）——本クラスは状態を持つだけの
/// 純粋な判定器で、駆動は <see cref="ActiveNotificationMonitor"/> の既存の周期評価（1 分間隔）が行う。
/// </para>
/// <para>
/// <b>発火を律速する仕組みは 2 段</b>（決定 3）:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>状態遷移</b>——途絶中フラグが立っている間は再発火しない（1009/1010 のラッチ設計と同じ向き）。
/// </description></item>
/// <item><description>
/// <b>エントリ別の抑制窓</b>（仮値 15 分）——短い個別閾値 + 不安定な装置のフラッピングで
/// 発火が反復する事態を防ぐ。<b>粒度がエントリ別である</b>のが既存のトリガ別抑制窓との違いで、
/// 装置 A の発火が装置 B の初報を飲まない。
/// </description></item>
/// </list>
/// <para>
/// <b>抑制窓に飲まれた途絶は恒久に失わない</b>（決定 3）: 窓内に抑制された途絶が窓明け時点でも
/// 継続していれば遅延発火する。「途絶が継続しているのに警告が一度も出ていない」状態を作らない。
/// </para>
/// </remarks>
internal sealed class SourceSilenceDetector
{
    private readonly SourceActivityTracker _tracker;
    private readonly TimeProvider _timeProvider;

    /// <summary>エントリ別の状態（キーは正規化済みアドレス文字列）。</summary>
    private readonly Dictionary<string, EntryState> _states = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<SourceSilenceWatchEntry> _watchlist = [];

    internal SourceSilenceDetector(SourceActivityTracker tracker, TimeProvider? timeProvider = null)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private sealed class EntryState
    {
        /// <summary>途絶中か（状態遷移で 1 回だけ発火させるためのラッチ）。</summary>
        internal bool IsSilent;

        /// <summary>最後に警告を出した時刻（エントリ別の抑制窓の起点）。</summary>
        internal DateTimeOffset? LastNotifiedAt;

        /// <summary>
        /// 抑制窓に飲まれた発火が保留されているか。窓明け時点でも途絶が継続していれば
        /// 遅延発火する（決定 3——恒久に失わない）。
        /// </summary>
        internal bool NotificationPending;
    }

    /// <summary>
    /// ウォッチリストを差し替える（設定の即時反映。決定 6）。
    /// </summary>
    /// <remarks>
    /// 追跡状態（最終受信時刻）は <see cref="SourceActivityTracker.ApplyWatchlist"/> が、
    /// 判定状態（途絶中フラグ・抑制窓）は本メソッドが引き継ぐ。削除されたエントリの状態は破棄する。
    /// </remarks>
    internal void ApplyWatchlist(IReadOnlyList<SourceSilenceWatchEntry>? watchlist)
    {
        _watchlist = watchlist ?? [];

        var live = _watchlist.Select(entry => Key(entry.Address)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _states.Keys.Where(key => !live.Contains(key)).ToList())
        {
            _states.Remove(stale);
        }
    }

    /// <summary>
    /// 1 周期ぶんの評価を行う。<see cref="ActiveNotificationMonitor"/> から呼ぶ。
    /// </summary>
    /// <param name="receptionSuspended">
    /// 全受信リスナが受信不能な間は判定を保留する（決定 3。実装粒度は委任 6——第 4 段）。
    /// <see langword="true"/> の間は途絶へ遷移させない。
    /// </param>
    internal SourceSilenceEvaluation Evaluate(bool receptionSuspended = false)
    {
        if (_watchlist.Count == 0)
        {
            return SourceSilenceEvaluation.Empty;
        }

        var now = _timeProvider.GetUtcNow();
        var silences = new List<SourceSilenceEvent>();
        var recoveries = new List<SourceSilenceEvent>();

        foreach (var entry in _watchlist)
        {
            var key = Key(entry.Address);
            var elapsed = _tracker.GetElapsedSinceLastActivity(entry.Address);

            if (elapsed is null)
            {
                // 追跡側にスロットが無い（差し替えの過渡状態）。次の周期で整合する。
                continue;
            }

            if (!_states.TryGetValue(key, out var state))
            {
                state = new EntryState();
                _states[key] = state;
            }

            var isSilentNow = elapsed > entry.Threshold;

            // --- 復帰（決定 3。能動通知はしないが情報記録は残す） ---
            if (state.IsSilent && !isSilentNow)
            {
                state.IsSilent = false;
                state.NotificationPending = false;
                recoveries.Add(new SourceSilenceEvent(entry.Address, entry.Label, entry.Threshold, elapsed.Value));
                continue;
            }

            if (!isSilentNow)
            {
                continue;
            }

            // --- 受信断保留（決定 3。委任 6） ---
            // サーバ側が受信できていない間は、装置側の途絶と区別できない。真因がサーバ側なのに
            // 運用者を装置側の調査へ誘導しないため、遷移させずに次の周期へ持ち越す。
            if (receptionSuspended)
            {
                continue;
            }

            // --- 途絶（状態遷移で 1 回。抑制窓でさらに律速） ---
            var isNewTransition = !state.IsSilent;
            state.IsSilent = true;

            if (!isNewTransition && !state.NotificationPending)
            {
                // 既に途絶中で、保留もない——再発火しない。
                continue;
            }

            var withinSuppressionWindow =
                state.LastNotifiedAt is { } lastAt
                && now - lastAt < SourceSilenceConstants.EntrySuppressionWindow;

            if (withinSuppressionWindow)
            {
                // 窓明け時点でも継続していれば遅延発火する（恒久に失わない）。
                state.NotificationPending = true;
                continue;
            }

            state.LastNotifiedAt = now;
            state.NotificationPending = false;
            silences.Add(new SourceSilenceEvent(entry.Address, entry.Label, entry.Threshold, elapsed.Value));
        }

        // --- 一斉集約（決定 3） ---
        // 集約警告時も各エントリの途絶フラグ・抑制窓は個別警告時と同様に更新済みである
        // （上のループで更新している）——集約は「出し方」だけを変える。
        var isBurst = silences.Count >= SourceSilenceConstants.BurstAggregationThreshold;

        return new SourceSilenceEvaluation(silences, isBurst, recoveries);
    }

    /// <summary>
    /// 受信経路の回復時に、保留中のエントリを回復時刻で再アームする（決定 3。委任 6）。
    /// </summary>
    /// <remarks>
    /// 起動時の再アーム（<see cref="SourceActivityTracker.ApplyWatchlist"/> の新規エントリ）と
    /// 同一規則——固定のグレース値を置かず、各エントリの再検知は<b>当該エントリの閾値</b>で
    /// 律速する。短閾値エントリの検知を一律に止めず、長閾値エントリに短時間の受信を強要しない。
    /// </remarks>
    internal void RearmAfterReceptionRecovery()
    {
        foreach (var entry in _watchlist)
        {
            _tracker.Seed(entry.Address, _timeProvider.GetUtcNow());

            if (_states.TryGetValue(Key(entry.Address), out var state))
            {
                state.IsSilent = false;
                state.NotificationPending = false;
            }
        }
    }

    private static string Key(IPAddress address) =>
        (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
}
