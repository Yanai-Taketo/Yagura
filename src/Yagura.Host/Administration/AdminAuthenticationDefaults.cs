namespace Yagura.Host.Administration;

/// <summary>
/// アプリ独自 ID/パスワード認証（ADR-0010 決定 3）のロックアウト仮値。
/// </summary>
/// <remarks>
/// ADR-0010 委任事項 4「ロックアウト閾値/期間は仮値で実装し実装 PR で確定する」を受けた仮値。
/// architecture.md/security.md の SEC-x・CF-x と同じ運用（<see cref="WizardSessionDefaults"/> と
/// 同じ位置づけ）——値そのものは確定待ちだが、仮値のまま実装しテストで固定する。確定時は
/// security.md の確定待ち一覧（SEC-12。本 PR で新設）へ反映する。
/// </remarks>
public static class AdminAuthenticationDefaults
{
    /// <summary>
    /// ロックアウトに至る連続失敗試行回数（仮値: 5 回）。
    /// </summary>
    public static readonly int LockoutThreshold = 5;

    /// <summary>
    /// ロックアウト期間（仮値: 15 分。ウィザードセッションの無操作タイムアウト
    /// <see cref="WizardSessionDefaults.InactivityTimeout"/> と同じ値を暫定的に採用——
    /// 運用感覚として妥当な桁の仮値であり、両者が独立に確定されるべき値であることに変わりはない）。
    /// </summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
}
