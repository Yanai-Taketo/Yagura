namespace Yagura.Host.Administration;

/// <summary>
/// ウィザードセッション統治の既定値（configuration.md §5・§7。M8-4。Issue #71）。
/// </summary>
public static class WizardSessionDefaults
{
    /// <summary>
    /// 無操作タイムアウト（<b>CF-3 仮値: 15 分</b>。configuration.md §5・§9）。
    /// 入力途中で放置されたウィザードセッションは、この時間の無操作で資格情報を破棄し、
    /// 再開時に再入力を求める。<b>確定済みステップ（進行状態）は破棄しない</b>——
    /// 「資格情報の非永続」と「進行状態の永続化」の分離（§5）。
    /// 仮値のまま実装し、実利用（DBA 依頼の往復等）を踏まえ CF-3 で確定する。
    /// </summary>
    public static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(15);
}
