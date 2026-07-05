using MudBlazor;

namespace Yagura.Web.Components.Common;

/// <summary>
/// <see cref="IYaguraNotifier"/> の MudBlazor Snackbar 実装（ui.md §3.1 通知規約。M8-2）。
/// </summary>
/// <remarks>
/// state-error の通知は <see cref="SnackbarOptions.RequireInteraction"/> により自動で消えず、
/// 閉じるボタンで手動クローズするまで残る（見逃し防止。ui.md §3.1）。クローズは MudBlazor の
/// クライアント側状態の変更のみで、サーバへの書き込みは発生しない。
/// </remarks>
public sealed class YaguraSnackbarNotifier : IYaguraNotifier
{
    private readonly ISnackbar _snackbar;

    public YaguraSnackbarNotifier(ISnackbar snackbar)
    {
        ArgumentNullException.ThrowIfNull(snackbar);
        _snackbar = snackbar;
    }

    /// <inheritdoc />
    public void NotifyOk(string message) => _snackbar.Add(message, Severity.Success);

    /// <inheritdoc />
    public void NotifyInfo(string message) => _snackbar.Add(message, Severity.Info);

    /// <inheritdoc />
    public void NotifyWarning(string message) => _snackbar.Add(message, Severity.Warning);

    /// <inheritdoc />
    public void NotifyError(string message) => _snackbar.Add(message, Severity.Error, ConfigureError);

    /// <summary>
    /// state-error 通知のオプション（テストから直接検証できるよう分離。ui.md §3.1 の
    /// 「state-error の通知は手動で閉じるまで残る」の実装点）。
    /// </summary>
    internal static void ConfigureError(SnackbarOptions options)
    {
        options.RequireInteraction = true;
        options.ShowCloseIcon = true;
    }
}
