using Yagura.Web.Administration;
using Yagura.Web.Components.Common;
using Yagura.Web.Tests.Components;

namespace Yagura.Web.Tests.Administration;

/// <summary>
/// <see cref="ConfigurationSaveReload"/> の単体テスト。保存は成立済みという前提のもとで、
/// 再起動待ちへ計上できなかったこと・保存したキー以外が即時反映されたことを利用者へ知らせることを固定する。
/// </summary>
public sealed class ConfigurationSaveReloadTests
{
    private static readonly string[] SavedKeys = ["Storage:Provider", "Storage:SqlServer:ConnectionString"];

    private readonly RecordingNotifier _notifier = new();
    private readonly RecordingConfigurationReloadService _reload = new();

    [Fact]
    public async Task ReloadAfterSave_Succeeded_RunsReloadOnceWithoutWarning()
    {
        _reload.NextResult = RecordingConfigurationReloadService.Succeeded(SavedKeys);

        var result = await ConfigurationSaveReload.ReloadAfterSaveAsync(
            _reload, _notifier, SavedKeys, "127.0.0.1", null, null);

        Assert.Equal(1, _reload.ReloadCount);
        Assert.NotNull(result);
        Assert.False(result.Rejected);
        Assert.Empty(_notifier.Warnings);
    }

    [Fact]
    public async Task ReloadAfterSave_Rejected_WarnsThatTheSaveSucceededButWasNotCounted()
    {
        _reload.NextResult = RecordingConfigurationReloadService.RejectedWith("受信ポートが不正です");

        await ConfigurationSaveReload.ReloadAfterSaveAsync(_reload, _notifier, SavedKeys, "127.0.0.1", null, null);

        var warning = Assert.Single(_notifier.Warnings);
        Assert.Equal(string.Format(UiText.AdminSaveReloadRejectedFormat, "受信ポートが不正です"), warning);
    }

    [Fact]
    public async Task ReloadAfterSave_OtherPendingChangeApplied_ListsOnlyTheKeysThisScreenDidNotSave()
    {
        // 初期セットアップウィザードで「保存のみ」にした受信ポートの変更が、この保存に続く再読み込みで
        // 即時反映された状況。保存したキーは列挙しない。
        _reload.NextResult = RecordingConfigurationReloadService.Succeeded(
            appliedKeys: ["Ingestion:Udp:Port", "Storage:Provider", "Retention:Days"],
            pendingRestartKeys: ["Storage:SqlServer:ConnectionString"]);

        await ConfigurationSaveReload.ReloadAfterSaveAsync(_reload, _notifier, SavedKeys, "127.0.0.1", null, null);

        var warning = Assert.Single(_notifier.Warnings);
        Assert.Equal(
            string.Format(UiText.AdminSaveReloadAppliedOtherChangesFormat, "Ingestion:Udp:Port、Retention:Days"),
            warning);
    }

    [Fact]
    public async Task ReloadAfterSave_ReloadThrows_WarnsThatTheSaveSucceededInsteadOfPropagating()
    {
        _reload.NextException = new IOException("yagura.json は別のプロセスが使用中です");

        var result = await ConfigurationSaveReload.ReloadAfterSaveAsync(
            _reload, _notifier, SavedKeys, "127.0.0.1", null, null);

        Assert.Null(result);
        var warning = Assert.Single(_notifier.Warnings);
        Assert.Equal(
            string.Format(UiText.AdminSaveReloadFailedFormat, "yagura.json は別のプロセスが使用中です"),
            warning);
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
