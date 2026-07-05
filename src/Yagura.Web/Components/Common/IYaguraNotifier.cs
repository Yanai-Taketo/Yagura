namespace Yagura.Web.Components.Common;

/// <summary>
/// 通知（トースト）の共通経路（ui.md §3.1 通知規約の実装形。M8-2）。
/// </summary>
/// <remarks>
/// <para>
/// 規約（ui.md §3.1）: 一時通知は自動で消えるが、<b>state-error の通知は手動で閉じるまで残る</b>
/// （見逃し防止）。「閉じる」はその閲覧者の画面からのみ消えるセッションローカル操作であり、
/// サーバ状態を変更しない（閲覧リスナの「書き込みエンドポイントを持たない」不変条件——ui.md §4
/// ——を通知の実装レベルでも守る）。
/// </para>
/// <para>
/// 通知履歴（状態画面から辿れる——ui.md §3.1）はサーバ側の観測性（architecture.md §4）の管轄で、
/// 状態画面（M8-3）が引き受ける。本インターフェースは画面内トーストの表示のみを担う。
/// </para>
/// <para>
/// ページは MudBlazor の ISnackbar を直接使わず本経路を使う（低レベル API をページに
/// 散乱させない——ui.md §1）。
/// </para>
/// </remarks>
public interface IYaguraNotifier
{
    /// <summary>正常・成功（state-ok）の一時通知。自動で消える。</summary>
    void NotifyOk(string message);

    /// <summary>情報（state-info）の一時通知。自動で消える。</summary>
    void NotifyInfo(string message);

    /// <summary>警告（state-warning）の一時通知。自動で消える。</summary>
    void NotifyWarning(string message);

    /// <summary>異常・失敗（state-error）の通知。<b>手動で閉じるまで残る</b>（ui.md §3.1）。</summary>
    void NotifyError(string message);
}
