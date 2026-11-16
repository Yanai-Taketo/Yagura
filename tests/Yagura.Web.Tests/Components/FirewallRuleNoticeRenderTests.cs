using System.Globalization;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Yagura.Abstractions.Administration;
using Yagura.Web.Administration.Screens;
using Yagura.Web.Circuits;
using Yagura.Web.Components.Common;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// TLS 受信・管理リモート HTTPS の設定画面が、受信許可規則（Windows ファイアウォール）の案内を
/// 保存済みのポートで表示することの検証（configuration.md §4.3）。
/// </summary>
/// <remarks>
/// インストーラも Yagura も事後有効化の規則を作らないため、規則の要否を画面で知らせる。
/// 案内のポートが既定値のまま固定されていると、ポートを変えた環境で違う番号の規則を作らせてしまう
/// ——ここでは注記とコマンドの両方に保存済みのポートが入ること、保存で更新されることを固定する。
/// テストホストのプロセスは Yagura 本体ではないため、コマンドは <c>-Program</c> を省いた形になる。
/// </remarks>
public sealed class FirewallRuleNoticeRenderTests : IAsyncLifetime
{
    private readonly BunitContext _ctx = new();

    public FirewallRuleNoticeRenderTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();
        _ctx.Services.AddSingleton<ICertificateStoreReader>(new EmptyStoreReader());
        _ctx.Services.AddSingleton(new YaguraCircuitContext());
        _ctx.Services.AddSingleton(new YaguraCircuitAuthenticationStateProvider());
        _ctx.Services.AddScoped<IYaguraNotifier, YaguraSnackbarNotifier>();
        _ctx.Services.AddSingleton<IConfigurationReloadService>(new NoopReloadService());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor の一部サービスは IAsyncDisposable のみを実装するため非同期に破棄する。
    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    [Theory]
    [InlineData("7514", 7514)]
    [InlineData(null, 6514)]
    [InlineData("not-a-port", 6514)]
    public void IngestionTlsScreen_ShowsNoticeAndCommandForSavedPort(string? savedPort, int expectedPort)
    {
        _ctx.Services.AddSingleton<IIngestionTlsAdminService>(new FakeIngestionTlsAdminService(savedPort));

        var cut = _ctx.Render<IngestionTlsScreen>();

        Assert.Contains(Title(UiText.IngestionTlsFirewallNoticeTitleFormat, expectedPort), Text(cut), StringComparison.Ordinal);
        var command = CommandText(cut);
        Assert.Contains($"-DisplayName 'Yagura Syslog (TLS {expectedPort})'", command, StringComparison.Ordinal);
        Assert.Contains($"-Protocol TCP -LocalPort {expectedPort} `", command, StringComparison.Ordinal);
        AssertProgramOmitted(cut, command);
    }

    [Fact]
    public async Task IngestionTlsScreen_AfterSavingNewPort_CommandUsesSavedPort()
    {
        var service = new FakeIngestionTlsAdminService("6514");
        _ctx.Services.AddSingleton<IIngestionTlsAdminService>(service);
        var cut = _ctx.Render<IngestionTlsScreen>();

        service.NextSavedPort = "7514";
        await ClickApplyAsync(cut);

        Assert.Contains(Title(UiText.IngestionTlsFirewallNoticeTitleFormat, 7514), Text(cut), StringComparison.Ordinal);
        Assert.Contains("-LocalPort 7514 `", CommandText(cut), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("9516", 9516)]
    [InlineData(null, 8516)]
    public void AdminRemoteAccessScreen_ShowsNoticeAndCommandForSavedPort(string? savedPort, int expectedPort)
    {
        _ctx.Services.AddSingleton<IAdminRemoteAccessAdminService>(new FakeAdminRemoteAccessAdminService(savedPort));

        var cut = _ctx.Render<AdminRemoteAccessScreen>();

        Assert.Contains(Title(UiText.AdminRemoteAccessFirewallNoticeTitleFormat, expectedPort), Text(cut), StringComparison.Ordinal);
        var command = CommandText(cut);
        Assert.Contains($"-DisplayName 'Yagura Admin Remote (HTTPS {expectedPort})'", command, StringComparison.Ordinal);
        Assert.Contains($"-Protocol TCP -LocalPort {expectedPort} `", command, StringComparison.Ordinal);
        AssertProgramOmitted(cut, command);
    }

    [Fact]
    public void BothScreens_PointToOperationsGuideSection()
    {
        _ctx.Services.AddSingleton<IIngestionTlsAdminService>(new FakeIngestionTlsAdminService(null));
        _ctx.Services.AddSingleton<IAdminRemoteAccessAdminService>(new FakeAdminRemoteAccessAdminService(null));

        Assert.Contains(UiText.FirewallRuleFollowUp, Text(_ctx.Render<IngestionTlsScreen>()), StringComparison.Ordinal);
        Assert.Contains(UiText.FirewallRuleFollowUp, Text(_ctx.Render<AdminRemoteAccessScreen>()), StringComparison.Ordinal);
    }

    // マークアップは HTML エンコード済み（単一引用符等が実体参照になる）ため、文言は表示テキストで照合する。
    private static string Text<T>(IRenderedComponent<T> cut)
        where T : Microsoft.AspNetCore.Components.IComponent =>
        string.Concat(cut.Nodes.Select(node => node.TextContent));

    private static string Title(string format, int port) =>
        string.Format(CultureInfo.InvariantCulture, format, port);

    private static string CommandText<T>(IRenderedComponent<T> cut)
        where T : Microsoft.AspNetCore.Components.IComponent =>
        cut.FindAll("pre.yagura-copy-block-value")
            .Single(pre => pre.TextContent.StartsWith("New-NetFirewallRule", StringComparison.Ordinal))
            .TextContent;

    private static void AssertProgramOmitted<T>(IRenderedComponent<T> cut, string command)
        where T : Microsoft.AspNetCore.Components.IComponent
    {
        Assert.DoesNotContain("-Program", command, StringComparison.Ordinal);
        Assert.Contains(UiText.FirewallRuleProgramUnresolved, Text(cut), StringComparison.Ordinal);
    }

    private static async Task ClickApplyAsync<T>(IRenderedComponent<T> cut)
        where T : Microsoft.AspNetCore.Components.IComponent
    {
        var apply = cut.FindAll("button").Single(button => button.TextContent.Contains("適用する"));
        await apply.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
    }

    // ------------------------------------------------------------------
    // フェイク
    // ------------------------------------------------------------------

    private sealed class FakeIngestionTlsAdminService(string? savedPort) : IIngestionTlsAdminService
    {
        internal string? NextSavedPort { get; set; }

        public Task<IngestionTlsStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new IngestionTlsStatus(Enabled: true, CertificateThumbprint: null, Port: savedPort));

        public Task<IngestionTlsConfigureResult> ConfigureAsync(
            bool enabled, string? certificateThumbprint, string? port,
            string? operatorAddress = null, string? operatorScheme = null, string? operatorPrincipal = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new IngestionTlsConfigureResult(
                ["Ingestion:Tls:Port"],
                ConfigurationApplyEffect.RestartRequired,
                ExpiredWarning: false,
                new IngestionTlsStatus(enabled, certificateThumbprint, NextSavedPort)));
    }

    private sealed class FakeAdminRemoteAccessAdminService(string? savedPort) : IAdminRemoteAccessAdminService
    {
        public Task<AdminRemoteAccessStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AdminRemoteAccessStatus(
                RemoteBindingEnabled: true, HttpsEnabled: true, CertificateThumbprint: null,
                HttpsPort: savedPort, WindowsAuthEnabled: true, AppAuthEnabled: false));

        public Task<AdminRemoteAccessConfigureResult> ConfigureAsync(
            bool remoteBindingEnabled, bool httpsEnabled, string? certificateThumbprint, string? httpsPort,
            string? operatorAddress = null, string? operatorScheme = null, string? operatorPrincipal = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class EmptyStoreReader : ICertificateStoreReader
    {
        public IReadOnlyList<CertificateCandidate> ListServerAuthCertificates() => [];
    }

    private sealed class NoopReloadService : IConfigurationReloadService
    {
        public IReadOnlyList<PendingRestartKey> GetPendingRestartKeys() => [];

        public Task<ConfigurationReloadResult> ReloadAsync(
            string? operatorAddress,
            string? authenticationScheme,
            string? authenticatedPrincipal,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConfigurationReloadResult(
                Rejected: false,
                RejectionReason: null,
                ChangedKeys: [],
                AppliedKeys: [],
                PendingRestartKeys: [],
                WarningMessages: [],
                UnknownKeys: [],
                TypeCoercionNotes: []));
    }
}
