using System.Collections.Concurrent;
using System.Net;

namespace Yagura.Ingestion.FlowControl;

/// <summary>
/// 送信元単位の token bucket による流量制御（architecture.md §3.3。ADR-0002 決定 2 の
/// 「送信元単位の流量制御（既定有効）」の実装。Issue #260）。
/// </summary>
/// <remarks>
/// <para>
/// <b>方式</b>: 送信元 IP アドレスごとに token bucket を 1 つ持つ。バケットは
/// <see cref="MessagesPerSecond"/> の速度でトークンが補充され（上限 <see cref="BurstSize"/>）、
/// データグラム 1 件の受け入れがトークン 1 個を消費する。トークンが尽きた送信元のデータグラムは
/// 破棄され、破棄は呼び出し元（各リスナ）が必ず計上する（「発火は必ず計測される」§3.3。
/// <c>IngestionMetrics.RecordFlowControlDropped</c>）。判定単位は件数（バイト数ではない）——
/// 破棄カウンタの単位 <c>{datagram}</c> と揃え、運用者が「何件落ちたか」を直接読めることを優先する。
/// </para>
/// <para>
/// <b>時刻の扱い</b>: 補充は遅延計算（lazy refill）——判定のたびに前回補充からの経過時間分を
/// まとめて加算する。時刻源は <see cref="TimeProvider.GetTimestamp"/>（単調クロック）であり、
/// システム時刻の巻き戻し・進みの影響を受けない。テストは <c>FakeTimeProvider</c> を注入する。
/// </para>
/// <para>
/// <b>状態空間の有界化</b>: バケット辞書は送信元アドレスをキーに増えるため、（特に UDP では
/// 送信元アドレスが偽装可能であることから）無制限に成長し得る。これを 2 段で抑える:
/// ①周期スイープ（<see cref="_sweepInterval"/> ごと）で「満杯まで回復したバケット」を削除する
/// ——満杯のバケットは削除しても次の到着時に満杯で再生成されるため、削除は判定結果を変えない。
/// ②追跡数の上限（<see cref="_maxTrackedSources"/>）到達時は、新規送信元を<b>追跡なしで通す</b>
/// （fail-open）。上限到達は偽装アドレスの大量流入など異常時に限られ、そこで新規送信元を
/// 一律破棄する側（fail-closed）に倒すと、偽装による飽和が正規の新規送信元の受信断へ転化する
/// ——「受信を止めない」（ADR-0002）を優先し、追跡済み送信元の制限は維持したまま通す。
/// 信頼ネットワーク前提（ADR-0004）では上限到達自体が例外的である。
/// </para>
/// <para>
/// <b>並行性</b>: 判定は複数スレッド（UDP 受信ループ・TCP/TLS の接続処理）から呼ばれる。
/// バケットの取得は <see cref="ConcurrentDictionary{TKey,TValue}"/>、補充・消費はバケット単位の
/// lock で直列化する（競合は同一送信元の判定同士に限られ、送信元が異なれば競合しない）。
/// スイープと判定の競合で、削除直前のバケットへの消費が失われる可能性があるが、削除対象は
/// 「満杯（= 最低でも補充 1 周期ぶん無通信）」のバケットに限るため、過剰許可は高々バースト
/// 1 回ぶんで、継続的な制限逃れにはならない。
/// </para>
/// <para>
/// <b>既定値は仮値</b>: 既定閾値は「通常運用で発火しない安全側」に実測で定める（M-4）。
/// 現在の仮値は CI 回帰ベンチの単一送信元の実測スループット
/// （SustainedZeroDrop ≒ 5,000 件/秒。<c>tools/Yagura.Bench/baselines/ci-baseline.json</c>）の
/// 2 倍を持続速度とし、バーストはその 2 秒ぶんとした。確定は M-4 の実測で行う。
/// </para>
/// </remarks>
public sealed class TokenBucketIngressGate : IIngressGate, IFlowControlRejectionReader
{
    /// <summary>持続速度（件/秒）の既定値（M-4 実測確定待ちの仮値。remarks 参照）。</summary>
    public const int DefaultMessagesPerSecond = 10_000;

    /// <summary>バーストサイズ（バケット容量。件）の既定値（M-4 実測確定待ちの仮値）。</summary>
    public const int DefaultBurstSize = 20_000;

    /// <summary>持続速度の下限（0 以下は「全破棄」となり受信の存在意義を失うため許容しない）。</summary>
    public const int MinMessagesPerSecond = 1;

    /// <summary>持続速度の上限（設定不正値の安全弁。int 演算の余裕を残す）。</summary>
    public const int MaxMessagesPerSecond = 100_000_000;

    /// <summary>バーストサイズの下限。</summary>
    public const int MinBurstSize = 1;

    /// <summary>バーストサイズの上限（設定不正値の安全弁）。</summary>
    public const int MaxBurstSize = 100_000_000;

    /// <summary>追跡する送信元数の上限の既定値（remarks「状態空間の有界化」参照）。</summary>
    internal const int DefaultMaxTrackedSources = 100_000;

    /// <summary>スイープ周期の既定値。</summary>
    internal static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<IPAddress, SourceBucket> _buckets = new();
    private readonly double _messagesPerSecond;
    private readonly double _burstSize;
    private readonly int _maxTrackedSources;
    private readonly TimeSpan _sweepInterval;
    private readonly TimeProvider _timeProvider;

    private long _lastSweepTimestamp;
    private int _sweepInProgress;

    /// <param name="messagesPerSecond">送信元 1 つあたりの持続速度（件/秒）。</param>
    /// <param name="burstSize">送信元 1 つあたりのバーストサイズ（バケット容量。件）。</param>
    /// <param name="timeProvider">時刻源（<c>null</c> は <see cref="TimeProvider.System"/>）。</param>
    public TokenBucketIngressGate(int messagesPerSecond, int burstSize, TimeProvider? timeProvider = null)
        : this(messagesPerSecond, burstSize, DefaultMaxTrackedSources, DefaultSweepInterval, timeProvider)
    {
    }

    /// <summary>
    /// テスト用に追跡上限・スイープ周期も指定できる内部コンストラクタ
    /// （本番結線は公開コンストラクタの既定値のみを使う）。
    /// </summary>
    internal TokenBucketIngressGate(
        int messagesPerSecond,
        int burstSize,
        int maxTrackedSources,
        TimeSpan sweepInterval,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(messagesPerSecond, MinMessagesPerSecond);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(messagesPerSecond, MaxMessagesPerSecond);
        ArgumentOutOfRangeException.ThrowIfLessThan(burstSize, MinBurstSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(burstSize, MaxBurstSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTrackedSources, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sweepInterval, TimeSpan.Zero);

        _messagesPerSecond = messagesPerSecond;
        _burstSize = burstSize;
        _maxTrackedSources = maxTrackedSources;
        _sweepInterval = sweepInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastSweepTimestamp = _timeProvider.GetTimestamp();
    }

    /// <summary>現在追跡中の送信元数（テスト・診断用の観測点）。</summary>
    internal int TrackedSourceCount => _buckets.Count;

    /// <inheritdoc />
    public bool ShouldAdmit(IPAddress sourceAddress, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(sourceAddress);

        var now = _timeProvider.GetTimestamp();
        SweepIfDue(now);

        if (!_buckets.TryGetValue(sourceAddress, out var bucket))
        {
            if (_buckets.Count >= _maxTrackedSources)
            {
                // 追跡上限到達——新規送信元は追跡なしで通す（fail-open。remarks 参照）。
                return true;
            }

            bucket = _buckets.GetOrAdd(sourceAddress, _ => new SourceBucket(_burstSize, now));
        }

        lock (bucket.SyncRoot)
        {
            Refill(bucket, now);

            if (bucket.Tokens >= 1d)
            {
                bucket.Tokens -= 1d;
                return true;
            }

            // 送信元別の拒否カウント（Issue #288——「どの送信元が制限に達しているか」の可視化）。
            // 総数の計上（yagura.ingestion.flow_control.dropped）は従来どおり呼び出し元の責務。
            bucket.RejectedCount++;
            return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<FlowControlRejectedSource> SnapshotRejectedSources(int maxCount)
    {
        if (maxCount < 1)
        {
            return [];
        }

        var rejected = new List<FlowControlRejectedSource>();
        foreach (var (address, bucket) in _buckets)
        {
            long count;
            lock (bucket.SyncRoot)
            {
                count = bucket.RejectedCount;
            }

            if (count > 0)
            {
                rejected.Add(new FlowControlRejectedSource(address, count));
            }
        }

        // 同数時の順序を決定的にする（テスト・表示の安定のためのアドレス文字列順）。
        return rejected
            .OrderByDescending(source => source.RejectedCount)
            .ThenBy(source => source.SourceAddress.ToString(), StringComparer.Ordinal)
            .Take(maxCount)
            .ToArray();
    }

    /// <summary>
    /// 前回補充からの経過時間ぶんのトークンを加算する（呼び出し側が <c>bucket.SyncRoot</c> を
    /// lock していること）。拒否時も補充時刻は進む——トークンは端数（1 未満）のまま蓄積されるため、
    /// 補充量が失われることはない。
    /// </summary>
    private void Refill(SourceBucket bucket, long now)
    {
        var elapsed = _timeProvider.GetElapsedTime(bucket.LastRefillTimestamp, now);
        if (elapsed > TimeSpan.Zero)
        {
            bucket.Tokens = Math.Min(_burstSize, bucket.Tokens + (elapsed.TotalSeconds * _messagesPerSecond));
        }

        bucket.LastRefillTimestamp = now;
    }

    /// <summary>
    /// スイープ周期が経過していれば、満杯まで回復したバケットを削除する（remarks「状態空間の
    /// 有界化」参照）。同時に走るのは 1 スレッドのみ（他スレッドはスキップして判定を続行する
    /// ——スイープは判定のホットパスを止めない）。
    /// </summary>
    private void SweepIfDue(long now)
    {
        if (_timeProvider.GetElapsedTime(Volatile.Read(ref _lastSweepTimestamp), now) < _sweepInterval)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _sweepInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Volatile.Write(ref _lastSweepTimestamp, now);

            foreach (var (address, bucket) in _buckets)
            {
                bool isFull;
                lock (bucket.SyncRoot)
                {
                    Refill(bucket, now);
                    isFull = bucket.Tokens >= _burstSize;
                }

                if (isFull)
                {
                    // 満杯のバケットは削除しても次の到着時に満杯で再生成されるため、
                    // 削除が判定結果を変えることはない（remarks「並行性」の過剰許可の議論参照）。
                    // 拒否カウント（Issue #288）もバケットごと消える——これは意図した設計
                    // （可視化のために有界化を崩さない。IFlowControlRejectionReader remarks）。
                    // 「制限なく受信できる状態がしばらく続いた送信元は一覧から消える」として
                    // 表示側の説明文にも明示する。
                    _buckets.TryRemove(address, out _);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _sweepInProgress, 0);
        }
    }

    /// <summary>送信元 1 つぶんの token bucket 状態。</summary>
    private sealed class SourceBucket(double initialTokens, long createdTimestamp)
    {
        /// <summary>本バケットの補充・消費を直列化する lock オブジェクト。</summary>
        public object SyncRoot { get; } = new();

        /// <summary>現在のトークン数（端数を保持するため double）。</summary>
        public double Tokens { get; set; } = initialTokens;

        /// <summary>前回補充の時刻（<see cref="TimeProvider.GetTimestamp"/> の値）。</summary>
        public long LastRefillTimestamp { get; set; } = createdTimestamp;

        /// <summary>
        /// 本バケット生成からの拒否（破棄）件数（Issue #288。読み書きとも <see cref="SyncRoot"/> の
        /// lock 内で行う）。バケットの生存期間と同じ寿命——スイープでバケットごと消える
        /// （<see cref="IFlowControlRejectionReader"/> remarks 参照）。
        /// </summary>
        public long RejectedCount { get; set; }
    }
}
