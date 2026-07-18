using Yagura.Abstractions.Auditing;

namespace Yagura.Host.Observability.Auditing;

/// <summary>
/// 拒否試行の集約記録（SEC-4。security.md §4.4。Issue #268）の仮値。
/// </summary>
/// <remarks>
/// <b>本クラスの閾値・窓・静穏はすべて仮値である</b>（security.md §7 SEC-4）。確定は実トラフィックで
/// 「単発・新種の事象が希釈されないこと」を確認して行う。確定するまで設定キーは設けない
/// （<see cref="CircuitGovernanceDefaults"/> と同じ方針——値が固まる前にキーを公開すると
/// additive-only 規約の互換負債を先に負う）。
/// </remarks>
internal static class AuditAggregationDefaults
{
    /// <summary>
    /// 集約へ切り替える閾値（同一送信元・同一種別の拒否がこの回数に達したら集約モードへ。仮値 10）。
    /// この回数までは個別記録する（単発・少数の事象を希釈しない——§4.4 の判定基準）。
    /// </summary>
    public const int AggregationThreshold = 10;

    /// <summary>
    /// 集約判定の窓（この時間内に閾値回数に達したら集約へ。仮値 1 分）。窓を跨いで間隔が空けば
    /// カウントは新しい窓としてやり直す（散発的な失敗を集約しない）。
    /// </summary>
    public static readonly TimeSpan AggregationWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 静穏窓（集約中のキーがこの時間、新たな事象を受けなければ集約サマリを確定記録し、個別記録へ
    /// 復帰する。仮値 5 分。§4.4「静穏窓の経過後は個別記録へ復帰する」）。
    /// </summary>
    public static readonly TimeSpan QuietWindow = TimeSpan.FromMinutes(5);

    /// <summary>静穏経過の周期スキャン間隔（実装都合の値。静穏サマリ出力の遅延はこの間隔に依存する）。</summary>
    public static readonly TimeSpan FlushScanInterval = TimeSpan.FromMinutes(1);

    /// <summary>集約サマリに列挙する利用者名の上限（Detail の肥大化防止。超過は「+N」と注記）。</summary>
    public const int MaxDistinctUsernamesInSummary = 20;

    /// <summary>
    /// 送信元単位で集約する拒否事象の種別（§4.4）。<b>グローバルトークンバケット涸渇（3007
    /// AdminAuthRateLimited）は含めない</b>——プロセス全体の事象であり「送信元ごと」の集約単位に
    /// 載せると意味が壊れる（§4.4 の申し送り。3007 は IP レート制限とグローバルバケットの両層を
    /// 同一 Kind で運ぶため、送信元集約からは一律に除外し個別記録のまま残す）。3005
    /// （AdminAccountLockedOut）は ADR-0011 で発火しなくなった凍結 ID のため対象に含めない。
    /// </summary>
    public static readonly IReadOnlySet<AuditEventKind> AggregatedKinds = new HashSet<AuditEventKind>
    {
        AuditEventKind.CircuitOriginRejected,               // 3002
        AuditEventKind.WindowsAuthenticationHandshakeFailed, // 3003
        AuditEventKind.AppAuthenticationLoginFailed,         // 3004
        AuditEventKind.AdminAuthBackoffCapReached,           // 3006
        AuditEventKind.AdminAuthorizationDenied,             // 3008
        AuditEventKind.ViewerAuthorizationDenied,            // 3009
    };
}
