using System.Linq;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Yagura.Web.Components.Common;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// 通知（トースト）規約の検証（ui.md §3.1。M8-2。Issue #69）:
/// 一時通知は自動で消えるが、state-error の通知は手動で閉じるまで残る（見逃し防止）。
/// </summary>
public sealed class YaguraSnackbarNotifierTests
{
    [Fact]
    public void NotifyError_RequiresManualClose()
    {
        var snackbar = new RecordingSnackbar();
        var notifier = new YaguraSnackbarNotifier(snackbar);

        notifier.NotifyError("保存に失敗しました");

        var call = Assert.Single(snackbar.Calls);
        Assert.Equal(Severity.Error, call.Severity);
        Assert.Equal("保存に失敗しました", call.Message);

        // state-error は手動で閉じるまで残る（ui.md §3.1）: RequireInteraction + 閉じる手段
        var options = new SnackbarOptions(Severity.Error, new SnackbarConfiguration());
        Assert.NotNull(call.Configure);
        call.Configure!(options);
        Assert.True(options.RequireInteraction, "state-error の通知が自動で消える設定になっている（ui.md §3.1 違反）。");
        Assert.True(options.ShowCloseIcon, "state-error の通知に手動で閉じる手段がない（ui.md §3.1 違反）。");
    }

    [Theory]
    [InlineData(Severity.Success)]
    [InlineData(Severity.Info)]
    [InlineData(Severity.Warning)]
    public void TransientNotifications_DoNotRequireInteraction(Severity severity)
    {
        var snackbar = new RecordingSnackbar();
        var notifier = new YaguraSnackbarNotifier(snackbar);

        switch (severity)
        {
            case Severity.Success:
                notifier.NotifyOk("保存しました");
                break;
            case Severity.Info:
                notifier.NotifyInfo("案内です");
                break;
            default:
                notifier.NotifyWarning("注意してください");
                break;
        }

        var call = Assert.Single(snackbar.Calls);
        Assert.Equal(severity, call.Severity);

        // 一時通知は自動で消える（RequireInteraction を true にしない——null は
        // SnackbarConfiguration の既定値（自動クローズ）を使う意味）
        var options = new SnackbarOptions(severity, new SnackbarConfiguration());
        call.Configure?.Invoke(options);
        Assert.NotEqual(true, options.RequireInteraction);
    }

    /// <summary>Add 呼び出しを記録するだけの ISnackbar 実装（描画は対象外）。</summary>
    private sealed class RecordingSnackbar : ISnackbar
    {
        public List<(string Message, Severity Severity, Action<SnackbarOptions>? Configure)> Calls { get; } = [];

        public SnackbarConfiguration Configuration { get; } = new();

        public IEnumerable<Snackbar> ShownSnackbars => Enumerable.Empty<Snackbar>();

        public event Action? OnSnackbarsUpdated
        {
            add { }
            remove { }
        }

        public Snackbar? Add(string message, Severity severity = Severity.Normal, Action<SnackbarOptions>? configure = null, string? key = null)
        {
            Calls.Add((message, severity, configure));
            return null;
        }

        public Snackbar? Add(RenderFragment message, Severity severity = Severity.Normal, Action<SnackbarOptions>? configure = null, string? key = null)
        {
            Calls.Add(("(RenderFragment)", severity, configure));
            return null;
        }

        public Snackbar? Add(MarkupString message, Severity severity = Severity.Normal, Action<SnackbarOptions>? configure = null, string? key = null)
        {
            Calls.Add((message.Value, severity, configure));
            return null;
        }

        public Snackbar? Add<TComponent>(Dictionary<string, object>? componentParameters = null, Severity severity = Severity.Normal, Action<SnackbarOptions>? configure = null, string? key = null)
            where TComponent : IComponent
        {
            Calls.Add((typeof(TComponent).Name, severity, configure));
            return null;
        }

        public void Clear()
        {
        }

        public void Remove(Snackbar snackbar)
        {
        }

        public void RemoveByKey(string key)
        {
        }

        public void Dispose()
        {
        }
    }
}
