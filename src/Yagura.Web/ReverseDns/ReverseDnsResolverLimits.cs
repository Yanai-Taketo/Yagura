namespace Yagura.Web.ReverseDns;

/// <summary>
/// 逆引き解決の資源保護の数値群（ADR-0007 決定 3。**すべて UI-10 の仮値**——実利用で確定する。
/// ui.md §12 UI-10 に本仮値を記録済み。値の確定時は設定キー化を判断する——additive-only 規約により
/// 確定前の設定キーは設けない、の SEC-1/SEC-8 と同じ扱い）。
/// </summary>
/// <param name="PositiveTtl">解決成功（名前あり）の保持時間。</param>
/// <param name="NegativeTtl">未登録・失敗（名前なし）の保持時間（PTR 未登録は数秒級の解決失敗を
/// 伴い得るため、必ずキャッシュして反復を防ぐ——ADR-0007 検証記録の前提条件）。</param>
/// <param name="MaxCacheEntries">キャッシュ件数の上限。超過時は新規解決を行わず IP のみ表示
/// （見送りは <c>yagura.web.reverse_dns.skipped</c> に計上）。</param>
/// <param name="MaxConcurrentLookups">同時解決数の上限。到達時の後続要求は**延期**する
/// （キューに積まない——次の表示更新の <c>TryGetDisplayName</c> で自然に再試行される。
/// キャッシュ上限の「見送り」と異なり恒常状態ではないため計上しない——実装 PR 決定・UI-10 記録）。</param>
/// <param name="LookupTimeout">解決待ちの打ち切り時間（「待つのをやめる」制御——OS の解決は
/// 中断できず完走する。ADR-0007 検証記録。完走した成功結果は事後にキャッシュへ反映する）。</param>
/// <param name="NotifyBatchInterval">表示反映の束ね間隔（解決完了 1 件ごとに再描画通知を
/// 発火させない——ADR-0007 決定 2 の反映粒度）。</param>
public sealed record ReverseDnsResolverLimits(
    TimeSpan PositiveTtl,
    TimeSpan NegativeTtl,
    int MaxCacheEntries,
    int MaxConcurrentLookups,
    TimeSpan LookupTimeout,
    TimeSpan NotifyBatchInterval)
{
    /// <summary>本番の既定値（UI-10 の仮値）。</summary>
    public static ReverseDnsResolverLimits Default { get; } = new(
        PositiveTtl: TimeSpan.FromMinutes(30),
        NegativeTtl: TimeSpan.FromMinutes(5),
        MaxCacheEntries: 10_000,
        MaxConcurrentLookups: 4,
        LookupTimeout: TimeSpan.FromSeconds(10),
        NotifyBatchInterval: TimeSpan.FromMilliseconds(500));
}
