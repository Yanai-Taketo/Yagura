using System.Security.Claims;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Yagura.Abstractions.Administration;
using Yagura.TestSupport.Fakes;
using Yagura.Web.Administration;
using Yagura.Web.Administration.Screens;
using Yagura.Web.Circuits;
using Yagura.Web.Components.Common;
using Yagura.Web.ForwarderKit;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// フォワーダ MSI アップロード機能の opt-in 切替（<c>Admin:ForwarderKit:MsiUpload:Enabled</c>）の保存が、
/// 再起動待ちカードへ計上されることの検証（configuration.md §3）。
/// </summary>
/// <remarks>
/// opt-in の切替は再起動まで効かない（エンドポイントの登録が起動時に固定される）。再起動待ちカードの
/// データ源は再読み込みの実行時にしか更新されないため、「値が変わったら再読み込みを呼ぶ」ことを固定する。
/// 値が変わらなかった保存（no-op）では呼ばないことも併せて固定する。
/// </remarks>
public sealed class ForwarderKitScreenReloadTests : IAsyncLifetime
{
    private readonly BunitContext _ctx = new();
    private readonly ScriptedUploadAdminService _service = new();
    private readonly RecordingConfigurationReloadService _reload = new();

    public ForwarderKitScreenReloadTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();
        _ctx.Services.AddSingleton<INicCandidateSource>(new EmptyNicCandidateSource());
        _ctx.Services.AddSingleton<IForwarderMsiSource>(new FakeForwarderMsiSource(
            @"C:\ProgramData\Yagura\forwarder", ForwarderMsiLookup.NotFound()));

        // 機能は無効（既定）・認証方式は設定済みの構成。この構成で、実認証済みの管理セッションに
        // 有効化ボタンが出る。
        _ctx.Services.AddSingleton(new ForwarderMsiUploadRuntimeOptions(false));
        _ctx.Services.AddSingleton(new AdminAuthenticationRuntimeOptions(
            RequireAuthentication: false, WindowsAuthEnabled: true, AppAuthEnabled: false));
        _ctx.Services.AddSingleton<IForwarderMsiStore>(new UnusedForwarderMsiStore());
        _ctx.Services.AddSingleton<IForwarderMsiPlacementService>(new UnusedPlacementService());
        _ctx.Services.AddSingleton<IForwarderMsiUploadAdminService>(_service);
        _ctx.Services.AddSingleton(new YaguraCircuitContext());

        var authState = new YaguraCircuitAuthenticationStateProvider();
        authState.SetAuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AdminAuthenticationExtensions.AdminSessionClaimType, "1")],
            authenticationType: "test")));
        _ctx.Services.AddSingleton(authState);

        _ctx.Services.AddScoped<IYaguraNotifier, YaguraSnackbarNotifier>();
        _ctx.Services.AddSingleton<IConfigurationReloadService>(_reload);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor の一部サービスは IAsyncDisposable のみを実装するため、コンテナは非同期に破棄する。
    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    private static async Task ClickEnableAsync(IRenderedComponent<ForwarderKitScreen> cut)
    {
        var enable = cut.FindAll("button")
            .Single(button => button.TextContent.Contains(UiText.ForwarderMsiUploadEnableButton));
        await enable.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
    }

    [Fact]
    public async Task Enable_SettingChanged_RunsReloadSoThePendingRestartCardIsUpdated()
    {
        _service.NextChanged = true;
        var cut = _ctx.Render<ForwarderKitScreen>();

        await ClickEnableAsync(cut);

        Assert.Equal(1, _reload.ReloadCount);
    }

    [Fact]
    public async Task Enable_NoOpSave_DoesNotRunReload()
    {
        _service.NextChanged = false;
        var cut = _ctx.Render<ForwarderKitScreen>();

        await ClickEnableAsync(cut);

        Assert.Equal(0, _reload.ReloadCount);
    }

    // ------------------------------------------------------------------
    // フェイク
    // ------------------------------------------------------------------

    private sealed class ScriptedUploadAdminService : IForwarderMsiUploadAdminService
    {
        private static readonly ForwarderMsiUploadSettingStatus Status = new(
            Enabled: false,
            WindowsAuthEnabled: true,
            AppAuthEnabled: false,
            HasAppAccount: false,
            AppAccountUsername: null,
            AppAccountCreatedAtUtc: null,
            AppAccountUpdatedAtUtc: null);

        internal bool NextChanged { get; set; }

        public Task<ForwarderMsiUploadSettingStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Status);

        public Task<ForwarderMsiUploadSettingResult> ConfigureAsync(
            bool enabled,
            bool accountInventoryAcknowledged = false,
            string? operatorAddress = null,
            string? operatorScheme = null,
            string? operatorPrincipal = null,
            bool operatorIsUploadOperationAuthenticated = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ForwarderMsiUploadSettingResult(
                NextChanged,
                NextChanged ? ConfigurationApplyEffect.RestartRequired : ConfigurationApplyEffect.Immediate,
                Status with { Enabled = NextChanged ? enabled : Status.Enabled }));
    }

    private sealed class EmptyNicCandidateSource : INicCandidateSource
    {
        public IReadOnlyList<NicCandidate> GetCandidates() => [];
    }

    private sealed class UnusedForwarderMsiStore : IForwarderMsiStore
    {
        public string FolderPath => @"C:\ProgramData\Yagura\forwarder";

        public ForwarderMsiWriteAccess CheckWriteAccess() => new(false, "test");

        public int CleanupStagingFiles() => 0;

        public Task<ForwarderMsiStageResult> StageAsync(
            ForwarderMsiArchitecture architecture, Stream content, long? declaredLength, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ForwarderMsiCommitResult Commit(string stagingToken, bool versionMismatchAcknowledged, bool replaceAcknowledged) =>
            throw new NotSupportedException();

        public ForwarderMsiDiscardResult Discard(string stagingToken) => throw new NotSupportedException();

        public ForwarderMsiDeleteResult Delete(ForwarderMsiArchitecture architecture, string expectedSha256) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedPlacementService : IForwarderMsiPlacementService
    {
        public Task<ForwarderMsiCommitResult> CommitAsync(
            string stagingToken, bool versionMismatchAcknowledged, bool replaceAcknowledged,
            ForwarderMsiArchitecture architecture, ForwarderMsiOperatorContext operatorContext) =>
            throw new NotSupportedException();

        public Task<ForwarderMsiDiscardResult> DiscardAsync(
            string stagingToken, ForwarderMsiArchitecture architecture, ForwarderMsiOperatorContext operatorContext) =>
            throw new NotSupportedException();

        public Task<ForwarderMsiDeleteResult> DeleteAsync(
            ForwarderMsiArchitecture architecture, string expectedSha256, ForwarderMsiOperatorContext operatorContext) =>
            throw new NotSupportedException();
    }
}
