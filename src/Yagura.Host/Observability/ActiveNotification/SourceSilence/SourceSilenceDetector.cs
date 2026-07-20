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
/// <remarks>
/// 型のみ <c>public</c>（公開型 <see cref="ActiveNotificationMonitor"/> のコンストラクタ引数に
/// 現れるため）。構築と操作の口は <c>internal</c> のままとし、外部アセンブリからの利用面は開かない
/// ——<c>EmailNotificationDispatcher</c> と同じ扱い。
/// </remarks>
public sealed class SourceSilenceDetector
{
    private readonly SourceActivityTracker _tracker;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 全操作を直列化するロック（#351 第 5 段）。書き手は監視ループ
    /// （<see cref="Evaluate"/>・<see cref="RearmAfterReceptionRecovery"/>）だけでなく、
    /// 設定の即時反映（<see cref="ApplyWatchlist"/>——再読み込みスレッド・管理画面の保存）と
    /// UI の状態読み取り（<see cref="SnapshotEntryStatuses"/>——Blazor スレッド）が別スレッドから
    /// 呼ぶ。競合は稀（周期 1 分 + 設定変更時のみ）で保持時間はごく短いため、素直な lock で足りる。
    /// </summary>
    private readonly object _gate = new();

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
        lock (_gate)
        {
            var previousThresholds = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _watchlist)
            {
                previousThresholds[Key(entry.Address)] = entry.Threshold;
            }

            _watchlist = watchlist ?? [];

            var live = _watchlist.Select(entry => Key(entry.Address)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in _states.Keys.Where(key => !live.Contains(key)).ToList())
            {
                _states.Remove(stale);
            }

            // 閾値が変更されたエントリは途絶フラグを解除するのみ（決定 3。Issue #381 の欠陥 3）。
            // 解除を Evaluate の復帰分岐に委ねると、実際には 1 件も受信していないのに
            // 「受信が再開した」（1029）が記録される——復帰の証跡は実受信でのみ出す。
            foreach (var entry in _watchlist)
            {
                var key = Key(entry.Address);
                if (previousThresholds.TryGetValue(key, out var previous)
                    && previous != entry.Threshold
                    && _states.TryGetValue(key, out var state))
                {
                    state.IsSilent = false;
                    state.NotificationPending = false;
                }
            }
        }
    }

    /// <summary>
    /// 起動時の初期値（seed。決定 3。Issue #381）を反映する。
    /// <c>QuerySourceActivityAsync</c> の結果を受け取り、ウォッチリスト該当エントリの
    /// 暫定基準（起動時点）を DB の最終受信時刻へ置き換える。
    /// </summary>
    /// <remarks>
    /// <b>seed 時点で既に閾値超過のエントリは起動時刻仮基準のまま再アームする</b>（即発火しない
    /// ——サーバ自身が閾値超の期間停止していた場合〔週末メンテ等〕の健在装置の一斉偽陽性を防ぐ。
    /// 帰結: 既知の長期途絶エントリの再警告は起動から当該閾値経過後になる）。結果に現れない
    /// エントリ・照会失敗時（呼び出し側が本メソッドを呼ばない）も起動時刻仮基準のまま——
    /// いずれも決定 3 の規定どおり。
    /// </remarks>
    internal void SeedFromStore(IReadOnlyList<Yagura.Storage.SourceActivity> activities)
    {
        lock (_gate)
        {
            if (_watchlist.Count == 0 || activities.Count == 0)
            {
                return;
            }

            var lastSeenByAddress = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
            foreach (var activity in activities)
            {
                // 保存側のアドレス表記（IPv4-mapped IPv6 の可能性）を照合キーへ正規化する。
                if (IPAddress.TryParse(activity.SourceAddress, out var parsed))
                {
                    lastSeenByAddress[Key(parsed)] = activity.LastReceivedAt;
                }
            }

            var now = _timeProvider.GetUtcNow();
            foreach (var entry in _watchlist)
            {
                if (!lastSeenByAddress.TryGetValue(Key(entry.Address), out var lastSeenAt))
                {
                    continue;
                }

                if (now - lastSeenAt > entry.Threshold)
                {
                    // 既に閾値超過——起動時刻仮基準のまま（上記 remarks）。
                    continue;
                }

                _tracker.SeedProvisionalBaseline(entry.Address, lastSeenAt);
            }
        }
    }

    /// <summary>
    /// エントリ別の現在状態のスナップショット（UI-4 の登録済みマーク・途絶中強調と
    /// 設定画面の一覧表示の入力。ADR-0018 決定 4）。アドレスは正規化済み文字列
    /// （IPv4-mapped IPv6 は IPv4 表記）で返す——閲覧側の照合キーと揃える。
    /// </summary>
    internal IReadOnlyList<Yagura.Abstractions.Observability.YaguraSourceSilenceReading> SnapshotEntryStatuses()
    {
        lock (_gate)
        {
            return [.. _watchlist.Select(entry =>
                new Yagura.Abstractions.Observability.YaguraSourceSilenceReading(
                    Key(entry.Address),
                    entry.Label,
                    entry.Threshold,
                    IsSilent: _states.TryGetValue(Key(entry.Address), out var state) && state.IsSilent))];
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
        lock (_gate)
        {
            return EvaluateCore(receptionSuspended);
        }
    }

    private SourceSilenceEvaluation EvaluateCore(bool receptionSuspended)
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
    /// 受信経路の回復時に、<b>保留中に閾値超過となったエントリ</b>を回復時刻で再アームする
    /// （決定 3。委任 6。Issue #381 の欠陥 2）。再アームした件数を返す（ログ用）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 対象を限定する理由: 受信断の間はサーバ都合で装置の生死を区別できないため、その間に
    /// 閾値を跨いだエントリだけが「回復時刻からの再計測」を要する。全エントリを再アームすると、
    /// 数分の受信断が観測されるたびに、閾値未満で沈黙中の装置（例: 閾値 24h で 23h 沈黙）の
    /// 時計まで前進し、検知が閾値ぶん先送りされる——短い受信断が繰り返される環境では検知が
    /// 恒久先送りされ得る。
    /// </para>
    /// <para>
    /// 受信断より<b>前</b>から途絶中（警告済み）のエントリも触らない——その途絶は受信断とは
    /// 独立に始まっており、復帰の証跡（1029）は実受信でのみ出す。閾値未満のエントリは
    /// 追跡時計をそのまま保ち、本来の「最終受信 + 閾値」で発火する。
    /// </para>
    /// <para>
    /// 再アーム規則自体は起動時（<see cref="SourceActivityTracker.ApplyWatchlist"/> の新規
    /// エントリ・<see cref="SeedFromStore"/> の閾値超過エントリ）と同一——固定のグレース値を
    /// 置かず、各エントリの再検知は<b>当該エントリの閾値</b>で律速する。
    /// </para>
    /// </remarks>
    internal int RearmAfterReceptionRecovery()
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            var rearmedCount = 0;

            foreach (var entry in _watchlist)
            {
                var elapsed = _tracker.GetElapsedSinceLastActivity(entry.Address);
                if (elapsed is null || elapsed <= entry.Threshold)
                {
                    // 閾値未満——追跡時計を保ち、本来の時点で発火させる。
                    continue;
                }

                if (_states.TryGetValue(Key(entry.Address), out var state) && state.IsSilent)
                {
                    // 受信断より前から途絶中（警告済み）——再アームせず途絶状態を維持する。
                    continue;
                }

                _tracker.Seed(entry.Address, now);
                rearmedCount++;
            }

            return rearmedCount;
        }
    }

    private static string Key(IPAddress address) =>
        (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
}
