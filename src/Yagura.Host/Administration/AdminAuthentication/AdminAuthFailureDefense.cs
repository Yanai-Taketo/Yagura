using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;

namespace Yagura.Host.Administration.AdminAuthentication;

/// <summary>
/// アプリ独自 ID/パスワード認証の三層防御（ADR-0011 決定 2〜5.1）の状態保持・判定を担う
/// プロセス内シングルトン。
/// </summary>
/// <remarks>
/// <para>
/// <b>三層の役割分担（決定 2）</b>: ①<see cref="CheckIpRateLimit"/>（送信元 IP 単位・最も安価）
/// → ②<see cref="CheckGlobalBucket"/>（プロセス全体） → ③<see cref="GetBackoffDelay"/> +
/// <see cref="RecordFailure"/>（アカウント単位）の順に評価する。前段（①②）で拒否された試行は
/// パスワード検証まで到達せず、連続失敗回数 n も進めない——呼び出し元（<see cref="AppAdminAuthenticationService"/>）
/// が①②を先に判定し、通過した場合のみ③を呼ぶ契約とすることでこの因果を保つ。
/// </para>
/// <para>
/// <b>loopback の扱い（決定 4）</b>: ①②は loopback（呼び出し元が渡す <c>isLoopback</c>——判定点は
/// <see cref="Yagura.Web.Administration.AdminAuthenticationExtensions.IsLoopbackAdminConnection"/>
/// と同一）を明示的に除外する（カウントも 429 拒否もしない）。③バックオフは loopback/remote を
/// キーの一部として分離するのみで、loopback 自体を除外しない——loopback に作用する失敗試行対策は
/// バックオフのみ、という決定 4 の原則をそのまま体現する。
/// </para>
/// <para>
/// <b>原子性（委任事項 1）</b>: 送信元 IP レート制限・アカウント単位バックオフはいずれも CAS
/// ループ（<see cref="ConcurrentDictionary{TKey,TValue}.TryUpdate"/> による楽観的並行制御）で
/// 状態を更新し、分散送信元からの同時失敗による lost update を防ぐ（PR #217 の DB 側原子的 UPDATE
/// と同じ設計意図をインメモリで再現）。グローバルトークンバケットはプロセス全体で単一の状態を
/// 持つため、単純な <see langword="lock"/> で十分（競合区間はトークンの増減のみで短い）。
/// </para>
/// <para>
/// <b>非実在ユーザー名には個別状態を持たせない（決定 3）</b>: 本クラスの
/// <see cref="GetBackoffDelay"/>/<see cref="RecordFailure"/>/<see cref="RecordSuccess"/> は
/// <see cref="AppAdminAuthenticationService"/> が実在アカウントに対してのみ呼ぶ契約——非実在
/// ユーザー名は①②のみで絞られ、状態化されない（メモリ枯渇 DoS を構造的に回避する）。
/// </para>
/// <para>
/// <b>能動通知への昇格（決定 6）</b>: <see cref="GetBackoffEscalations"/>/
/// <see cref="GetIpRateLimitEscalations"/>/<see cref="GetGlobalBucketEscalation"/> は
/// <c>ActiveNotificationMonitor</c> の周期評価が参照するスナップショット取得点であり、状態を
/// 変更しない（読み取り専用）。
/// </para>
/// </remarks>
public sealed class AdminAuthFailureDefense
{
    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentDictionary<BackoffKey, BackoffState> _backoffStates = new();
    private readonly ConcurrentDictionary<string, IpWindowState> _ipWindows = new(StringComparer.Ordinal);

    private readonly object _bucketLock = new();
    private double _bucketTokens;
    private DateTimeOffset _bucketLastRefillAtUtc;
    private DateTimeOffset? _bucketDenyStreakStartAtUtc;
    private readonly List<string> _bucketRecentDeniedAddresses = [];

    private const int MaxTrackedRecentAddresses = 5;

    public AdminAuthFailureDefense(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _bucketTokens = AdminAuthenticationDefaults.GlobalBucketBurst;
        _bucketLastRefillAtUtc = _timeProvider.GetUtcNow();
    }

    // ==== ①IP レート制限（決定 2・4・5・5.1） ====

    /// <summary>
    /// 送信元 IP 単位のレート制限を判定する（決定 2 評価順序 ①）。loopback は対象外（決定 4）。
    /// 待たせず即座に許可/拒否を返す（決定 5.1）。
    /// </summary>
    public AdminAuthGateDecision CheckIpRateLimit(IPAddress? remoteAddress, bool isLoopback)
    {
        if (isLoopback)
        {
            return AdminAuthGateDecision.Allow();
        }

        if (remoteAddress is null)
        {
            // 送信元 IP を取得できない接続は本層でキーにできない——fail-closed ではなく
            // 素通りさせる（アカウント単位バックオフ・グローバルバケットが引き続き効く）。
            return AdminAuthGateDecision.Allow();
        }

        var key = remoteAddress.ToString();
        var window = AdminAuthenticationDefaults.IpRateLimitWindow;
        var maxAttempts = AdminAuthenticationDefaults.IpRateLimitMaxAttempts;

        while (true)
        {
            var now = _timeProvider.GetUtcNow();

            if (!_ipWindows.TryGetValue(key, out var current))
            {
                var fresh = new IpWindowState(1, now, null);
                if (_ipWindows.TryAdd(key, fresh))
                {
                    return AdminAuthGateDecision.Allow();
                }

                continue;
            }

            if (now - current.WindowStartAtUtc >= window)
            {
                // 昇格判定用のストリーク（決定 6）は、窓の境界をまたぐたびに単純クリアしない——
                // 前の窓が上限に達していた（飽和していた）場合は継続的な圧力とみなしストリークを
                // 引き継ぐ。上限に達していなかった窓（実需要が閾値未満だった = 真の鎮静化）を
                // 挟んだ場合のみストリークを解除する。これを行わないと、トークンバケット/固定窓
                // 方式が本質的に持つ「境界で必ず 1 件は通す」性質により、持続的な攻撃下でも
                // 昇格が絶対に成立しなくなる。
                var carriedDenyStreakStart = current.Count >= maxAttempts
                    ? current.DenyStreakStartAtUtc ?? current.WindowStartAtUtc
                    : (DateTimeOffset?)null;
                var reset = new IpWindowState(1, now, carriedDenyStreakStart);
                if (_ipWindows.TryUpdate(key, reset, current))
                {
                    return AdminAuthGateDecision.Allow();
                }

                continue;
            }

            if (current.Count < maxAttempts)
            {
                var incremented = current with { Count = current.Count + 1 };
                if (_ipWindows.TryUpdate(key, incremented, current))
                {
                    return AdminAuthGateDecision.Allow();
                }

                continue;
            }

            var denyStreakStart = current.DenyStreakStartAtUtc ?? now;
            if (current.DenyStreakStartAtUtc is null)
            {
                var marked = current with { DenyStreakStartAtUtc = denyStreakStart };
                // 失敗しても致命的ではない（次周期で再度セットされる）ため CAS の結果を無視する。
                _ipWindows.TryUpdate(key, marked, current);
            }

            var retryAfter = MinTimeSpan(window - (now - current.WindowStartAtUtc), AdminAuthenticationDefaults.RateLimitRetryAfterCap);
            return AdminAuthGateDecision.Deny(retryAfter);
        }
    }

    /// <summary>
    /// 現在追跡中の IP レート制限エントリ数（診断・テスト用。Issue #233）。<see cref="_ipWindows"/>
    /// は非 loopback の送信元 IP のみをキーにする（loopback は <see cref="CheckIpRateLimit"/> が
    /// 早期リターンし、本辞書に一切触れない）。
    /// </summary>
    public int IpRateLimitTrackedAddressCount => _ipWindows.Count;

    /// <summary>
    /// アイドル化した IP レート制限エントリ（Issue #233）を辞書から掃き出す。「アイドル」の定義は
    /// 現在の窓が既に失効している（直近 <see cref="AdminAuthenticationDefaults.IpRateLimitWindow"/>
    /// の間、当該送信元 IP からの試行が一件もない）ことのみ——<see cref="CheckIpRateLimit"/> は
    /// 窓が失効した状態で新規試行を受けると常に <c>Count=1</c> の新しい窓として扱う（飽和していた
    /// 前窓のストリークのみを引き継ぐ）ため、同じ条件で先回りして除去しても次回アクセス時の挙動と
    /// 等価——アクティブに攻撃中（直近の窓内で上限に達し続けている）送信元は窓が失効しないため
    /// 対象にならない（決定 2・4 の評価順序・loopback 無条件復旧経路のいずれにも影響しない。呼び出し元
    /// remarks 参照）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>原子性</b>: 列挙で得た <see cref="KeyValuePair{TKey,TValue}"/> のスナップショットを
    /// <see cref="ICollection{T}.Remove(T)"/>（キー一致 <b>かつ</b> 値一致でのみ削除する比較 &amp; 削除。
    /// <see cref="IpWindowState"/> はレコード構造体のため値等価性で比較される）で除去する——スイープ中に
    /// <see cref="CheckIpRateLimit"/> が同じキーを更新（新しい窓へロールオーバー等）していれば値が
    /// 一致せず削除は不成立になるため、更新後の状態を誤って消す lost update は起きない（委任事項 1
    /// と同じ設計意図）。
    /// </para>
    /// <para>
    /// <b>呼び出し元</b>: <see cref="Yagura.Host.Observability.ActiveNotification.ActiveNotificationMonitor"/>
    /// の周期評価（仮値 1 分ごと）が毎周期呼ぶ——IP レート制限の窓（仮値 60 秒）と同オーダーの
    /// 頻度で、送信元が攻撃者制御であるがゆえに無制限に増加し得る辞書（Issue #233 の問題提起）を
    /// 定期的に縮退させる。辞書サイズの上限機構（LRU 等）は採用しない——アクティブな送信元を
    /// 上限超過で強制退避すると、退避された攻撃者がレート制限を回避できてしまい「エビクションが
    /// アクティブな攻撃者の状態を消して制限を回避させない」という要件に反するため、除去対象は
    /// アイドル判定に一致するエントリのみに限定する。
    /// </para>
    /// </remarks>
    /// <returns>実際に除去したエントリ数。</returns>
    public int SweepIdleIpRateLimitEntries()
    {
        var now = _timeProvider.GetUtcNow();
        var window = AdminAuthenticationDefaults.IpRateLimitWindow;
        var removed = 0;

        foreach (var entry in _ipWindows)
        {
            if (now - entry.Value.WindowStartAtUtc < window)
            {
                continue;
            }

            if (((ICollection<KeyValuePair<string, IpWindowState>>)_ipWindows).Remove(entry))
            {
                removed++;
            }
        }

        return removed;
    }

    // ==== ②グローバルトークンバケット（決定 2・4・5.1） ====

    /// <summary>
    /// プロセス全体のグローバルトークンバケットを判定する（決定 2 評価順序 ②）。loopback は
    /// 対象外（決定 4）。待たせず即座に許可/拒否を返す（決定 5.1）。
    /// </summary>
    public AdminAuthGateDecision CheckGlobalBucket(bool isLoopback, IPAddress? remoteAddress)
    {
        if (isLoopback)
        {
            return AdminAuthGateDecision.Allow();
        }

        lock (_bucketLock)
        {
            var now = _timeProvider.GetUtcNow();
            RefillBucketLocked(now);

            // 昇格判定用のストリーク（決定 6）は「バーストまで完全に再充填された」= 需要が
            // 途絶えた真の鎮静化の場合にのみ解除する。単純に「許可された 1 件が来たら解除」だと、
            // 補充速度分のトリクル許可を必ず伴うトークンバケットの性質上、持続的な攻撃下でも
            // 昇格が絶対に成立しなくなる（IP レート制限層と同じ理由。上記コメント参照）。
            if (_bucketTokens >= AdminAuthenticationDefaults.GlobalBucketBurst - 0.0001)
            {
                _bucketDenyStreakStartAtUtc = null;
                _bucketRecentDeniedAddresses.Clear();
            }

            if (_bucketTokens >= 1.0)
            {
                _bucketTokens -= 1.0;
                return AdminAuthGateDecision.Allow();
            }

            _bucketDenyStreakStartAtUtc ??= now;
            TrackRecentAddressLocked(remoteAddress);

            var secondsToNextToken = (1.0 - _bucketTokens) / AdminAuthenticationDefaults.GlobalBucketRefillPerSecond;
            var retryAfter = MinTimeSpan(
                TimeSpan.FromSeconds(secondsToNextToken),
                AdminAuthenticationDefaults.RateLimitRetryAfterCap);
            return AdminAuthGateDecision.Deny(retryAfter);
        }
    }

    private void RefillBucketLocked(DateTimeOffset now)
    {
        var elapsedSeconds = (now - _bucketLastRefillAtUtc).TotalSeconds;
        if (elapsedSeconds > 0)
        {
            _bucketTokens = Math.Min(
                AdminAuthenticationDefaults.GlobalBucketBurst,
                _bucketTokens + (elapsedSeconds * AdminAuthenticationDefaults.GlobalBucketRefillPerSecond));
            _bucketLastRefillAtUtc = now;
        }
    }

    private void TrackRecentAddressLocked(IPAddress? remoteAddress)
    {
        if (remoteAddress is null)
        {
            return;
        }

        var text = remoteAddress.ToString();
        _bucketRecentDeniedAddresses.Remove(text);
        _bucketRecentDeniedAddresses.Insert(0, text);
        if (_bucketRecentDeniedAddresses.Count > MaxTrackedRecentAddresses)
        {
            _bucketRecentDeniedAddresses.RemoveRange(MaxTrackedRecentAddresses, _bucketRecentDeniedAddresses.Count - MaxTrackedRecentAddresses);
        }
    }

    // ==== ③アカウント単位バックオフ（決定 2・3・4） ====

    /// <summary>
    /// 現在の状態に基づき、これから行う試行に適用すべき遅延を返す（決定 3。パスワード検証の
    /// 前に呼ぶこと）。アイドル減衰（決定 3）を読み取り時に適用する——実際の状態のリセットは
    /// 次回の <see cref="RecordFailure"/>/<see cref="RecordSuccess"/> で行う。
    /// </summary>
    public TimeSpan GetBackoffDelay(string username, bool isLoopback)
    {
        var key = new BackoffKey(NormalizeUsername(username), isLoopback);
        if (!_backoffStates.TryGetValue(key, out var state))
        {
            return TimeSpan.Zero;
        }

        var now = _timeProvider.GetUtcNow();
        return ComputeDelay(EffectiveN(state, now));
    }

    /// <summary>
    /// ログイン成功時に n をリセットする（決定 3）。
    /// </summary>
    public void RecordSuccess(string username, bool isLoopback)
    {
        var key = new BackoffKey(NormalizeUsername(username), isLoopback);
        _backoffStates.TryRemove(key, out _);
    }

    /// <summary>
    /// ログイン失敗を記録し、連続失敗回数 n を原子的に増分する（委任事項 1）。戻り値は次回試行に
    /// 適用される遅延と、今回の試行が cap（上限遅延）下で行われたか（監査 ID 3006 の発火判定に使う。
    /// 決定 9）。
    /// </summary>
    public BackoffFailureOutcome RecordFailure(string username, bool isLoopback, IPAddress? remoteAddress)
    {
        var key = new BackoffKey(NormalizeUsername(username), isLoopback);
        var addressText = remoteAddress?.ToString();

        while (true)
        {
            var now = _timeProvider.GetUtcNow();

            if (!_backoffStates.TryGetValue(key, out var current))
            {
                // 初回失敗: baseN = 0 のため cap に達することはない。
                var fresh = new BackoffState(1, now, null, TrackAddress(ImmutableList<string>.Empty, addressText));
                if (_backoffStates.TryAdd(key, fresh))
                {
                    return new BackoffFailureOutcome(1, CapReachedThisAttempt: false, ComputeDelay(1));
                }

                continue;
            }

            var baseN = EffectiveN(current, now);
            var delayThisAttempt = ComputeDelay(baseN);
            var capReachedThisAttempt = delayThisAttempt >= AdminAuthenticationDefaults.BackoffCap;
            var newN = baseN + 1;
            var capReachedSince = capReachedThisAttempt ? (current.CapReachedSinceUtc ?? now) : (DateTimeOffset?)null;

            var updated = new BackoffState(newN, now, capReachedSince, TrackAddress(current.RecentSourceAddresses, addressText));
            if (_backoffStates.TryUpdate(key, updated, current))
            {
                return new BackoffFailureOutcome(newN, capReachedThisAttempt, ComputeDelay(newN));
            }
        }
    }

    private static int EffectiveN(BackoffState state, DateTimeOffset now) =>
        now - state.LastFailureAtUtc >= AdminAuthenticationDefaults.BackoffIdleDecay ? 0 : state.N;

    private static TimeSpan ComputeDelay(int n)
    {
        if (n < AdminAuthenticationDefaults.BackoffThreshold)
        {
            return TimeSpan.Zero;
        }

        var exponent = n - AdminAuthenticationDefaults.BackoffThreshold;
        var rawSeconds = AdminAuthenticationDefaults.BackoffBase.TotalSeconds * Math.Pow(2, exponent);
        var seconds = Math.Min(rawSeconds, AdminAuthenticationDefaults.BackoffCap.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private static ImmutableList<string> TrackAddress(ImmutableList<string> existing, string? addressText)
    {
        if (addressText is null)
        {
            return existing;
        }

        var withoutDuplicate = existing.Remove(addressText);
        var prefixed = withoutDuplicate.Insert(0, addressText);
        return prefixed.Count > MaxTrackedRecentAddresses
            ? prefixed.RemoveRange(MaxTrackedRecentAddresses, prefixed.Count - MaxTrackedRecentAddresses)
            : prefixed;
    }

    // ==== 能動通知への昇格スナップショット（決定 6。読み取り専用） ====

    /// <summary>cap 到達状態が <see cref="AdminAuthenticationDefaults.EscalationThreshold"/> 以上継続しているバックオフキー。</summary>
    public IReadOnlyList<AdminAuthBackoffEscalation> GetBackoffEscalations()
    {
        var now = _timeProvider.GetUtcNow();
        var results = new List<AdminAuthBackoffEscalation>();

        foreach (var (key, state) in _backoffStates)
        {
            if (state.CapReachedSinceUtc is { } since && now - since >= AdminAuthenticationDefaults.EscalationThreshold)
            {
                results.Add(new AdminAuthBackoffEscalation(key.UsernameNormalized, key.IsLoopback, state.N, since, state.RecentSourceAddresses));
            }
        }

        return results;
    }

    /// <summary>拒否状態が <see cref="AdminAuthenticationDefaults.EscalationThreshold"/> 以上継続している送信元 IP（IP レート制限層）。</summary>
    public IReadOnlyList<AdminAuthRateLimitEscalation> GetIpRateLimitEscalations()
    {
        var now = _timeProvider.GetUtcNow();
        var results = new List<AdminAuthRateLimitEscalation>();

        foreach (var (address, state) in _ipWindows)
        {
            if (state.DenyStreakStartAtUtc is { } since && now - since >= AdminAuthenticationDefaults.EscalationThreshold)
            {
                results.Add(new AdminAuthRateLimitEscalation(address, since));
            }
        }

        return results;
    }

    /// <summary>グローバルトークンバケットの涸渇状態が <see cref="AdminAuthenticationDefaults.EscalationThreshold"/> 以上継続しているか。</summary>
    public AdminAuthGlobalBucketEscalation? GetGlobalBucketEscalation()
    {
        lock (_bucketLock)
        {
            var now = _timeProvider.GetUtcNow();
            if (_bucketDenyStreakStartAtUtc is { } since && now - since >= AdminAuthenticationDefaults.EscalationThreshold)
            {
                return new AdminAuthGlobalBucketEscalation(since, [.. _bucketRecentDeniedAddresses]);
            }

            return null;
        }
    }

    private static TimeSpan MinTimeSpan(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static string NormalizeUsername(string username) => username.Trim().ToLowerInvariant();

    private readonly record struct BackoffKey(string UsernameNormalized, bool IsLoopback);

    private sealed record BackoffState(
        int N,
        DateTimeOffset LastFailureAtUtc,
        DateTimeOffset? CapReachedSinceUtc,
        ImmutableList<string> RecentSourceAddresses);

    private readonly record struct IpWindowState(int Count, DateTimeOffset WindowStartAtUtc, DateTimeOffset? DenyStreakStartAtUtc);
}

/// <summary>①②層の判定結果（決定 5.1: 待たせず即座に許可/拒否・拒否時は有限 <c>Retry-After</c>）。</summary>
public readonly record struct AdminAuthGateDecision(bool Allowed, TimeSpan RetryAfter)
{
    public static AdminAuthGateDecision Allow() => new(true, TimeSpan.Zero);

    public static AdminAuthGateDecision Deny(TimeSpan retryAfter) => new(false, retryAfter);
}

/// <summary><see cref="AdminAuthFailureDefense.RecordFailure"/> の結果。</summary>
/// <param name="N">増分後の連続失敗回数。</param>
/// <param name="CapReachedThisAttempt">今回の試行が cap（上限遅延）下で行われたか（監査 3006 の発火判定）。</param>
/// <param name="NextDelay">次回試行に適用される遅延。</param>
public sealed record BackoffFailureOutcome(int N, bool CapReachedThisAttempt, TimeSpan NextDelay);

/// <summary>能動通知（決定 6）向けのバックオフ cap 到達継続スナップショット。</summary>
public sealed record AdminAuthBackoffEscalation(
    string UsernameNormalized,
    bool IsLoopback,
    int FailedAttemptCount,
    DateTimeOffset CapReachedSinceUtc,
    IReadOnlyList<string> RecentSourceAddresses);

/// <summary>能動通知（決定 6）向けの IP レート制限拒否継続スナップショット。</summary>
public sealed record AdminAuthRateLimitEscalation(string RemoteAddress, DateTimeOffset DenyStreakStartAtUtc);

/// <summary>能動通知（決定 6）向けのグローバルトークンバケット涸渇継続スナップショット。</summary>
public sealed record AdminAuthGlobalBucketEscalation(DateTimeOffset DenyStreakStartAtUtc, IReadOnlyList<string> RecentSourceAddresses);
