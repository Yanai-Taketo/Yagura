using System.Net;
using System.Net.Sockets;
using Yagura.Storage.Observability;
using Yagura.Host.Configuration;

namespace Yagura.Host.Observability.ActiveNotification.SourceSilence;

/// <summary>
/// ウォッチリスト登録済み送信元の最終受信時刻を追跡する（ADR-0018 決定 3・委任 7）。
/// </summary>
/// <remarks>
/// <para>
/// <b>単調クロック基準</b>（<see cref="TimeProvider.GetTimestamp"/>）。壁時計は表示・記録にのみ
/// 使う——NTP のステップで一斉誤発火するのを防ぐ（<c>TokenBucketIngressGate</c> の前例を踏襲）。
/// </para>
/// <para>
/// <b>書き手は 2 系統</b>（決定 3）: 受信段（<c>ParsingStage</c> の消費ループ）と、スプール
/// drain の合流点。後者があるのは、深いスプール滞留 + 再起動の組で「直前まで送っていた装置」が
/// 途絶に見える偽陽性を塞ぐため（§3.2.2 は滞留を正常状態と明記しており「短い窓」ではない）。
/// </para>
/// <para>
/// <b>更新は <see cref="Interlocked.CompareExchange(ref long, long, long)"/> の CAS ループによる
/// <c>max()</c></b>（委任 7）。単純な代入だと、2 系統の書き手が競合したときに<b>古い時刻が
/// 新しい時刻を上書きし得る</b>（lost update）——drain 合流点が運ぶのは過去の受信実績なので、
/// これが起きると「今受信したばかりの装置」が過去へ引き戻される。<c>long</c> の単一フィールドに
/// 保持するため読み取り側の torn read も起きない（32bit 環境でも <c>Interlocked</c> 経由の
/// 読み書きは分割されない）。
/// </para>
/// <para>
/// <b>有界性</b>: 追跡辞書はウォッチリスト適用時に<b>凍結して作り切る</b>（登録済みアドレスの
/// エントリだけを持つ）。受信のたびに辞書へ要素を足すことはしない——送信元アドレスを変えながら
/// 送るだけでメモリを食い潰せる経路を作らないため（受け入れ基準の有界性テスト）。
/// </para>
/// </remarks>
internal sealed class SourceActivityTracker : ISourceActivityTracker
{
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 凍結済みルックアップ（アドレス文字列 → 最終受信時刻のスロット）。
    /// 参照交換のみで差し替える（<c>UpdateDefaultRfc3164TimeZone</c> と同じパターン）。
    /// </summary>
    private volatile IReadOnlyDictionary<string, ActivitySlot> _slots =
        new Dictionary<string, ActivitySlot>(StringComparer.OrdinalIgnoreCase);

    internal SourceActivityTracker(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>1 エントリの可変スロット。</summary>
    private sealed class ActivitySlot
    {
        /// <summary>最終受信時刻（単調タイムスタンプ。CAS 対象）。</summary>
        internal long LastActivityTimestamp;

        /// <summary>
        /// 実受信（受信段・drain 合流点）を一度でも観測したか（0/1。Issue #381）。
        /// 0 の間は <see cref="LastActivityTimestamp"/> が暫定基準（登録時点・再アーム時点）で
        /// あることを表し、起動時 seed（<see cref="SeedProvisionalBaseline"/>）だけが過去へ
        /// 置き換えられる。実受信の書き手は本フラグを先に立ててから時刻を更新する
        /// （seed 側はフラグを先に読む——この順序で「実績の引き戻し」を競合下でも防ぐ）。
        /// </summary>
        internal int HasObservedActivity;

        /// <summary>
        /// 現在の基準時刻が再アーム（登録時点・起動時刻・受信断回復）由来か（0/1。Issue #382）。
        /// 1 の間、基準時刻は「実際に受信した時刻」ではない——1027 の最終受信時刻表示と
        /// 1028 の「再アーム起点の一斉発火かの別」の判定材料（表示・記録専用。判定は従来どおり
        /// 単調 tick の経過のみを見る）。実受信（<see cref="HasObservedActivity"/> と同じ書き手）と
        /// DB 実績の起動時 seed で 0 になり、受信断回復の再アーム（<see cref="Seed"/>）で 1 に戻る。
        /// </summary>
        internal int BaselineIsRearmed = 1;
    }

    /// <inheritdoc />
    public void RecordActivity(string sourceAddress)
    {
        // 受信ホットパス。**解析もアロケーションもしない**——辞書引き 1 回で戻る。
        // IPv4-mapped IPv6 の別表記は ApplyWatchlist が両方のキーを登録済みなので、
        // ここで正規化する必要がない（正規化の費用をリスト適用時へ寄せてある）。
        if (sourceAddress is null || !_slots.TryGetValue(sourceAddress, out var slot))
        {
            return;
        }

        Volatile.Write(ref slot.HasObservedActivity, 1);
        Volatile.Write(ref slot.BaselineIsRearmed, 0);
        UpdateToMax(slot, _timeProvider.GetTimestamp());
    }

    /// <summary>
    /// <see cref="IPAddress"/> 版（テスト・診断用。ホットパスからは使わない）。
    /// </summary>
    internal void RecordActivity(IPAddress address)
    {
        if (address is not null)
        {
            RecordActivity(NormalizeAddress(address));
        }
    }

    /// <summary>
    /// スプール drain の合流点から、<b>過去の受信実績</b>を遅延反映する（決定 3）。
    /// </summary>
    /// <param name="address">送信元アドレス。</param>
    /// <param name="observedAt">
    /// レコードが実際に受信された壁時計時刻。<b>単調タイムラインへ換算してから</b>反映する。
    /// </param>
    public void RecordHistoricalActivity(string sourceAddress, DateTimeOffset observedAt)
    {
        // RecordActivity と同じく、ここでも解析しない——drain は 1 セグメントぶんの
        // レコードをまとめて流すため、件数ぶんの解析が積み上がる。
        if (sourceAddress is null || !_slots.TryGetValue(sourceAddress, out var slot))
        {
            return;
        }

        Volatile.Write(ref slot.HasObservedActivity, 1);
        Volatile.Write(ref slot.BaselineIsRearmed, 0);
        UpdateToMax(slot, ToMonotonicTimestamp(observedAt));
    }

    /// <summary>
    /// <see cref="IPAddress"/> 版（テスト・診断用）。
    /// </summary>
    internal void RecordHistoricalActivity(IPAddress address, DateTimeOffset observedAt)
    {
        if (address is not null)
        {
            RecordHistoricalActivity(NormalizeAddress(address), observedAt);
        }
    }

    /// <summary>
    /// ウォッチリストを差し替える（設定の即時反映。決定 6）。
    /// </summary>
    /// <remarks>
    /// <b>既存エントリの追跡状態は保持し、削除されたエントリの状態は破棄する</b>（決定 6）。
    /// 保持しないと、設定を触るたびに全エントリが「登録時点基準」へ戻り、長い閾値のエントリが
    /// 実質永久に発火しなくなる。
    /// </remarks>
    /// <param name="watchlist">新しいウォッチリスト（<see langword="null"/> は機能無効）。</param>
    internal void ApplyWatchlist(IReadOnlyList<SourceSilenceWatchEntry>? watchlist)
    {
        var previous = _slots;
        var next = new Dictionary<string, ActivitySlot>(StringComparer.OrdinalIgnoreCase);

        if (watchlist is not null)
        {
            var now = _timeProvider.GetTimestamp();

            foreach (var entry in watchlist)
            {
                var key = NormalizeAddress(entry.Address);

                // 既存エントリはスロットごと引き継ぐ（追跡状態の保持）。
                // 新規エントリは「登録時点」を仮の最終受信時刻とする——これは仕様である
                // （決定 3）: 「先回りで登録したが機器側の設定が済んでいない／経路が開通して
                // いない」を検出する数少ない機会を提供する。
                var slot = previous.TryGetValue(key, out var existing)
                    ? existing
                    : new ActivitySlot { LastActivityTimestamp = now };

                next[key] = slot;

                // 同一スロットを IPv4-mapped IPv6 の別表記でも引けるようにしておく
                // （受信ホットパスで解析させないための前計算。両キーは同じ ActivitySlot を
                // 指すため、どちらの表記で届いても 1 つの最終受信時刻が更新される）。
                if (entry.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    next[$"::ffff:{key}"] = slot;
                }
            }
        }

        _slots = next;
    }

    /// <summary>
    /// 基準時刻を前方へ進める（再アーム。決定 3——受信断回復時）。<c>max()</c> 更新のため
    /// 既に新しい実受信があるスロットは後退させない。
    /// </summary>
    internal void Seed(IPAddress address, DateTimeOffset lastSeenAt)
    {
        var key = NormalizeAddress(address);
        if (_slots.TryGetValue(key, out var slot))
        {
            Volatile.Write(ref slot.BaselineIsRearmed, 1);
            UpdateToMax(slot, ToMonotonicTimestamp(lastSeenAt));
        }
    }

    /// <summary>
    /// 起動時 seed（決定 3。Issue #381）: DB 照会の最終受信時刻で、暫定基準（登録時点）を
    /// <b>過去へ</b>置き換える。実受信（受信段・drain 合流点）を一度でも観測したスロットには
    /// 適用しない——過去の DB 値が今の実績を引き戻さない。
    /// </summary>
    /// <remarks>
    /// <see cref="Seed"/>（max 更新 = 前方専用）とは向きが逆のため別メソッドにする。max 更新の
    /// ままでは、<see cref="ApplyWatchlist"/> が置く登録時点基準より古い DB 値が常に no-op となり、
    /// 「閾値 24h の装置がサーバ再起動の 23h 前に死んだ場合に最終受信 +24h で発火する」という
    /// 決定 3 の設計が成立しない（Issue #381 の欠陥 1）。
    /// </remarks>
    internal void SeedProvisionalBaseline(IPAddress address, DateTimeOffset lastSeenAt)
    {
        var key = NormalizeAddress(address);
        if (!_slots.TryGetValue(key, out var slot))
        {
            return;
        }

        var target = ToMonotonicTimestamp(lastSeenAt);
        while (true)
        {
            if (Volatile.Read(ref slot.HasObservedActivity) != 0)
            {
                // 実受信が先に届いた——DB 値より新しい実績を正とする（引き戻さない）。
                return;
            }

            var current = Interlocked.Read(ref slot.LastActivityTimestamp);
            if (Interlocked.CompareExchange(ref slot.LastActivityTimestamp, target, current) == current)
            {
                // DB の実績で置き換えた——基準は「実際に受信した時刻」になった（Issue #382）。
                Volatile.Write(ref slot.BaselineIsRearmed, 0);
                return;
            }

            // CAS 失敗 = 実受信の書き手と競合した可能性。フラグを読み直して再判定する
            // （実受信側は「フラグを立ててから時刻を更新する」順序のため、ここで必ず観測できる）。
        }
    }

    /// <summary>
    /// 当該アドレスの最終受信からの経過時間を返す。ウォッチリスト外なら <see langword="null"/>。
    /// </summary>
    internal TimeSpan? GetElapsedSinceLastActivity(IPAddress address) =>
        GetActivityReading(address)?.Elapsed;

    /// <summary>
    /// 経過時間 + 基準の由来（再アーム起点か）を返す（Issue #382——1027 の最終受信時刻表示・
    /// 1028 の「再アーム起点の一斉発火かの別」の入力）。ウォッチリスト外なら <see langword="null"/>。
    /// </summary>
    internal (TimeSpan Elapsed, bool BaselineIsRearmed)? GetActivityReading(IPAddress address)
    {
        var key = NormalizeAddress(address);
        if (!_slots.TryGetValue(key, out var slot))
        {
            return null;
        }

        var last = Interlocked.Read(ref slot.LastActivityTimestamp);
        return (
            _timeProvider.GetElapsedTime(last, _timeProvider.GetTimestamp()),
            Volatile.Read(ref slot.BaselineIsRearmed) != 0);
    }

    /// <summary>
    /// 追跡中の<b>エントリ数</b>（有界性のテスト・診断用）。
    /// 辞書は IPv4 エントリを 2 キーで引けるようにしているため、キー数ではなくスロットの
    /// 実体数を数える。
    /// </summary>
    internal int TrackedCount => _slots.Values.Distinct().Count();

    /// <summary>
    /// <c>max()</c> 更新の CAS ループ（委任 7）。既存値以下なら何もしない。
    /// </summary>
    private static void UpdateToMax(ActivitySlot slot, long candidate)
    {
        while (true)
        {
            var current = Interlocked.Read(ref slot.LastActivityTimestamp);
            if (candidate <= current)
            {
                // 過去の実績（drain 合流点・seed）が、より新しい実受信を引き戻さない。
                return;
            }

            if (Interlocked.CompareExchange(ref slot.LastActivityTimestamp, candidate, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 壁時計の時刻を単調タイムラインへアンカーする（決定 3 の換算規則）。
    /// </summary>
    /// <remarks>
    /// 「現在壁時計との経過差を、現在の単調時刻から差し引く」。<b>負の経過（未来の壁時計値）は
    /// 0 に clamp する</b>——時計のずれた送信元の主張やクロックスキューで、最終受信時刻が
    /// 未来に置かれて永久に途絶しないエントリが生まれるのを防ぐ。換算時点の NTP ずれによる
    /// 誤差は一回限りで、以後の実受信が上書きするため許容する（決定 3）。
    /// </remarks>
    private long ToMonotonicTimestamp(DateTimeOffset wallClock)
    {
        var elapsed = _timeProvider.GetUtcNow() - wallClock;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var ticksPerSecond = _timeProvider.TimestampFrequency;
        var offset = (long)(elapsed.TotalSeconds * ticksPerSecond);

        return _timeProvider.GetTimestamp() - offset;
    }

    /// <summary>
    /// IPv4-mapped IPv6 を IPv4 へ畳む（流量制御・Top talkers と同じ既存規約）。
    /// 同一装置が 2 つのキーに割れ、片方だけが更新されて他方が途絶に見える事故を防ぐ。
    /// </summary>
    private static string NormalizeAddress(IPAddress address) =>
        (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
}
