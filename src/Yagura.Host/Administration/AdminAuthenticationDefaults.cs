namespace Yagura.Host.Administration;

/// <summary>
/// アプリ独自 ID/パスワード認証（ADR-0011 決定 2〜7）の三層防御・パスワード強度の仮値一覧。
/// </summary>
/// <remarks>
/// ADR-0011 決定 10（SEC-12 の統合）の仮値表をそのまま実装する。値そのものは確定待ちだが
/// （SEC-12。<c>security.md</c> §7）、仮値のまま実装しテストで固定する（architecture.md/security.md の
/// SEC-x・CF-x と同じ運用）。ADR-0010 決定 3 が採用していたハードロックアウトの仮値
/// （<c>LockoutThreshold</c>/<c>LockoutDuration</c>）は本 ADR の supersession により置き換えられた。
/// </remarks>
public static class AdminAuthenticationDefaults
{
    /// <summary>
    /// バックオフ猶予閾値 k（仮値: 3 回。k 回目までは遅延なし。ADR-0011 決定 10）。
    /// </summary>
    public static readonly int BackoffThreshold = 3;

    /// <summary>
    /// バックオフ基数 base（仮値: 1 秒。<c>delay = min(base * 2^(n-k), cap)</c>。ADR-0011 決定 10）。
    /// </summary>
    public static readonly TimeSpan BackoffBase = TimeSpan.FromSeconds(1);

    /// <summary>
    /// バックオフ上限 cap（仮値: 30 秒。ADR-0011 決定 3・10）。cap に達しても拒否はしない——
    /// 正しいパスワードであれば cap の待ち時間の後に必ずログインできる。
    /// </summary>
    public static readonly TimeSpan BackoffCap = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 連続失敗回数 n のアイドル減衰窓（仮値: 30 分。無失敗が継続したら n を 0 にリセットする。
    /// ADR-0011 決定 3・10）。
    /// </summary>
    public static readonly TimeSpan BackoffIdleDecay = TimeSpan.FromMinutes(30);

    /// <summary>
    /// IP レート制限の窓（仮値: 60 秒。ADR-0011 決定 4・5・10）。loopback（<c>127.0.0.1</c>・
    /// <c>::1</c>）は明示除外——判定点は
    /// <see cref="Yagura.Web.Administration.AdminAuthenticationExtensions.IsLoopbackAdminConnection"/>
    /// と同一。
    /// </summary>
    public static readonly TimeSpan IpRateLimitWindow = TimeSpan.FromSeconds(60);

    /// <summary>IP レート制限の窓内上限試行回数（仮値: 10 回。ADR-0011 決定 10）。</summary>
    public static readonly int IpRateLimitMaxAttempts = 10;

    /// <summary>
    /// グローバルトークンバケットの定常補充速度（仮値: 1 トークン/秒。ADR-0011 決定 10）。
    /// </summary>
    public static readonly double GlobalBucketRefillPerSecond = 1.0;

    /// <summary>
    /// グローバルトークンバケットのバースト容量（仮値: 20。ADR-0011 決定 5.1・10。#227 稿の
    /// バースト 5 から Phase 2 の複数オペレータ同時対応を踏まえ引き上げ）。loopback は明示除外。
    /// </summary>
    public static readonly int GlobalBucketBurst = 20;

    /// <summary>
    /// IP レート制限・グローバルトークンバケット層の <c>Retry-After</c> 上限（仮値: 30 秒。
    /// バックオフ cap と揃える。ADR-0011 決定 5.1・10）。
    /// </summary>
    public static readonly TimeSpan RateLimitRetryAfterCap = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 能動通知への昇格閾値（仮値: 15 分。同一アカウントで cap 到達状態が継続、またはレート制限
    /// 拒否が同水準で継続する場合に <c>ActiveNotificationMonitor</c> 経由で昇格する。
    /// ADR-0011 決定 6・10）。
    /// </summary>
    public static readonly TimeSpan EscalationThreshold = TimeSpan.FromMinutes(15);

    /// <summary>パスワード最小長（確定値: 12 文字以上。ADR-0011 決定 7・10）。</summary>
    public static readonly int MinimumPasswordLength = 12;
}
