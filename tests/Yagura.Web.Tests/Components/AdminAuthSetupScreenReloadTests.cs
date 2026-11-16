using Bunit;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Yagura.Abstractions.Administration;
using Yagura.Web.Administration.Screens;
using Yagura.Web.Circuits;
using Yagura.Web.Components.Common;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// 管理 UI 認証設定画面の保存が、再起動待ちカードへ計上されることの検証（configuration.md §3）。
/// </summary>
/// <remarks>
/// 再起動待ちカードのデータ源は再読み込みの実行時にしか更新されない。認証方式の変更は再起動まで
/// 効かないため、「保存で変更キーが出たら再読み込みを呼ぶ」ことが計上の経路そのものであり、ここで
/// 固定する。パスワードだけの変更（変更キーなし）では呼ばないことも併せて固定する。
/// </remarks>
public sealed class AdminAuthSetupScreenReloadTests : IAsyncLifetime
{
    private readonly BunitContext _ctx = new();
    private readonly ScriptedAuthAdminService _service = new();
    private readonly RecordingConfigurationReloadService _reload = new();

    public AdminAuthSetupScreenReloadTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();
        _ctx.Services.AddSingleton<IAdminAuthenticationAdminService>(_service);
        _ctx.Services.AddSingleton(new YaguraCircuitContext());
        _ctx.Services.AddSingleton(new YaguraCircuitAuthenticationStateProvider());
        _ctx.Services.AddScoped<IYaguraNotifier, YaguraSnackbarNotifier>();
        _ctx.Services.AddSingleton<IAntiforgery>(new UnusedAntiforgery());
        _ctx.Services.AddSingleton<IConfigurationReloadService>(_reload);
        _ctx.AddBunitPersistentComponentState();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor の一部サービスは IAsyncDisposable のみを実装するため、コンテナは非同期に破棄する。
    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    private static async Task ClickApplyAsync(IRenderedComponent<AdminAuthSetupScreen> cut)
    {
        var apply = cut.FindAll("button").Single(button => button.TextContent.Contains("適用する"));
        await apply.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
    }

    [Fact]
    public async Task Apply_AuthenticationMethodChanged_RunsReloadSoThePendingRestartCardIsUpdated()
    {
        _service.NextChangedKeys = ["Admin:Authentication:Windows:Enabled"];
        var cut = _ctx.Render<AdminAuthSetupScreen>();

        await ClickApplyAsync(cut);

        Assert.Equal(1, _reload.ReloadCount);
    }

    [Fact]
    public async Task Apply_NoAuthenticationMethodChanged_DoesNotRunReload()
    {
        _service.NextChangedKeys = [];
        var cut = _ctx.Render<AdminAuthSetupScreen>();

        await ClickApplyAsync(cut);

        Assert.Equal(0, _reload.ReloadCount);
    }

    // ------------------------------------------------------------------
    // フェイク
    // ------------------------------------------------------------------

    private sealed class ScriptedAuthAdminService : IAdminAuthenticationAdminService
    {
        private static readonly AdminAuthenticationStatus Status = new(
            WindowsAuthEnabled: false,
            KerberosOnly: false,
            AppAuthEnabled: false,
            RequireForLoopback: false,
            HasAppAccount: false,
            AppAccountUsername: null);

        internal IReadOnlyList<string> NextChangedKeys { get; set; } = [];

        public Task<AdminAuthenticationStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Status);

        public Task<AdminAuthenticationConfigureResult> ConfigureAsync(
            bool windowsAuthEnabled,
            bool kerberosOnly,
            bool appAuthEnabled,
            bool requireForLoopback,
            string? newAppUsername,
            string? newAppPassword,
            string? operatorAddress = null,
            string? operatorScheme = null,
            string? operatorPrincipal = null,
            bool operatorIsUploadOperationAuthenticated = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AdminAuthenticationConfigureResult(NextChangedKeys, Status));
    }

    /// <summary>
    /// 緊急全失効フォームのトークン発行にしか使われない（HttpContext のない対話的描画では呼ばれない）。
    /// </summary>
    private sealed class UnusedAntiforgery : IAntiforgery
    {
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => throw new NotSupportedException();

        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => throw new NotSupportedException();

        public Task<bool> IsRequestValidAsync(HttpContext httpContext) => throw new NotSupportedException();

        public Task ValidateRequestAsync(HttpContext httpContext) => throw new NotSupportedException();

        public void SetCookieTokenAndHeader(HttpContext httpContext) => throw new NotSupportedException();
    }
}
