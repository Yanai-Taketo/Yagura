using Yagura.Web.Administration;
using Yagura.Web.Components.Common;
using Yagura.Web.Tests.Components;

namespace Yagura.Web.Tests.Administration;

/// <summary>
/// <see cref="ConfigurationSaveReload"/> の単体テスト。保存後の再読み込みが拒否されたときに、
/// 再起動待ちへ計上できなかったことを利用者へ知らせることを固定する。
/// </summary>
public sealed class ConfigurationSaveReloadTests
{
    private readonly RecordingNotifier _notifier = new();
    private readonly RecordingConfigurationReloadService _reload = new();

    [Fact]
    public async Task ReloadAfterSave_Succeeded_RunsReloadOnceWithoutWarning()
    {
        _reload.NextResult = RecordingConfigurationReloadService.Succeeded(["Storage:Provider"]);

        var result = await ConfigurationSaveReload.ReloadAfterSaveAsync(_reload, _notifier, "127.0.0.1", null, null);

        Assert.Equal(1, _reload.ReloadCount);
        Assert.False(result.Rejected);
        Assert.Empty(_notifier.Warnings);
    }

    [Fact]
    public async Task ReloadAfterSave_Rejected_WarnsThatTheSaveSucceededButWasNotCounted()
    {
        _reload.NextResult = RecordingConfigurationReloadService.RejectedWith("受信ポートが不正です");

        await ConfigurationSaveReload.ReloadAfterSaveAsync(_reload, _notifier, "127.0.0.1", null, null);

        var warning = Assert.Single(_notifier.Warnings);
        Assert.Equal(string.Format(UiText.AdminSaveReloadRejectedFormat, "受信ポートが不正です"), warning);
    }

    private sealed class RecordingNotifier : IYaguraNotifier
    {
        internal List<string> Warnings { get; } = [];

        public void NotifyOk(string message)
        {
        }

        public void NotifyInfo(string message)
        {
        }

        public void NotifyWarning(string message) => Warnings.Add(message);

        public void NotifyError(string message)
        {
        }
    }
}
