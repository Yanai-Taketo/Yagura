using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Yagura.Abstractions.Administration;
using Yagura.Web.Administration.Screens;
using Yagura.Web.Circuits;
using Yagura.Web.Components.Common;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// TLS 受信設定画面の保存が再起動待ちカードへ計上されること（ADR-0019 委任 2。Issue #388）の検証。
/// </summary>
/// <remarks>
/// 再起動待ちカード（#286）のデータ源は <c>ConfigurationReloadService</c> 内部の累積辞書で、
/// 再読み込みの実行時にしか更新されない。したがって「保存成功（変更あり）後に自動で
/// 再読み込みが実行される」ことが計上の経路そのものであり、ここで固定する。
/// no-op 保存（変更ゼロ）では再読み込みを呼ばないことも併せて固定する。
/// </remarks>
public sealed class IngestionTlsScreenReloadTests : IAsyncLifetime
{
    private readonly BunitContext _ctx = new();
    private readonly ScriptedTlsAdminService _service = new();
    private readonly CountingReloadService _reload = new();

    public IngestionTlsScreenReloadTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();
        _ctx.Services.AddSingleton<IIngestionTlsAdminService>(_service);
        _ctx.Services.AddSingleton<ICertificateStoreReader>(new EmptyStoreReader());
        _ctx.Services.AddSingleton(new YaguraCircuitContext());
        _ctx.Services.AddSingleton(new YaguraCircuitAuthenticationStateProvider());
        _ctx.Services.AddScoped<IYaguraNotifier, YaguraSnackbarNotifier>();
        _ctx.Services.AddSingleton<IConfigurationReloadService>(_reload);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor の一部サービス（KeyInterceptorService）は IAsyncDisposable のみを実装するため、
    // コンテナは非同期に破棄する（同期 Dispose は InvalidOperationException になる——実挙動で確認）。
    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    private async Task ClickApplyAsync(IRenderedComponent<IngestionTlsScreen> cut)
    {
        var apply = cut.FindAll("button").Single(button => button.TextContent.Contains("適用する"));
        await apply.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
    }

    [Fact]
    public async Task Apply_SaveWithChanges_RunsReloadSoThePendingRestartCardIsUpdated()
    {
        _service.NextChangedKeys = ["Ingestion:Tls:Enabled"];
        var cut = _ctx.Render<IngestionTlsScreen>();

        await ClickApplyAsync(cut);

        Assert.Equal(1, _reload.ReloadCount);
    }

    [Fact]
    public async Task Apply_NoOpSave_DoesNotRunReload()
    {
        _service.NextChangedKeys = [];
        var cut = _ctx.Render<IngestionTlsScreen>();

        await ClickApplyAsync(cut);

        Assert.Equal(0, _reload.ReloadCount);
    }

    // ------------------------------------------------------------------
    // フェイク
    // ------------------------------------------------------------------

    private sealed class ScriptedTlsAdminService : IIngestionTlsAdminService
    {
        internal IReadOnlyList<string> NextChangedKeys { get; set; } = [];

        public Task<IngestionTlsStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new IngestionTlsStatus(Enabled: false, CertificateThumbprint: null, Port: null));

        public Task<IngestionTlsConfigureResult> ConfigureAsync(
            bool enabled,
            string? certificateThumbprint,
            string? port,
            string? operatorAddress = null,
            string? operatorScheme = null,
            string? operatorPrincipal = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new IngestionTlsConfigureResult(
                NextChangedKeys,
                NextChangedKeys.Count > 0
                    ? ConfigurationApplyEffect.RestartRequired
                    : ConfigurationApplyEffect.Immediate,
                ExpiredWarning: false,
                new IngestionTlsStatus(enabled, certificateThumbprint, port)));
    }

    private sealed class EmptyStoreReader : ICertificateStoreReader
    {
        public IReadOnlyList<CertificateCandidate> ListServerAuthCertificates() => [];
    }

    private sealed class CountingReloadService : IConfigurationReloadService
    {
        internal int ReloadCount { get; private set; }

        public IReadOnlyList<PendingRestartKey> GetPendingRestartKeys() => [];

        public Task<ConfigurationReloadResult> ReloadAsync(
            string? operatorAddress,
            string? authenticationScheme,
            string? authenticatedPrincipal,
            CancellationToken cancellationToken = default)
        {
            ReloadCount++;
            return Task.FromResult(new ConfigurationReloadResult(
                Rejected: false,
                RejectionReason: null,
                ChangedKeys: ["Ingestion:Tls:Enabled"],
                AppliedKeys: [],
                PendingRestartKeys: ["Ingestion:Tls:Enabled"],
                WarningMessages: [],
                UnknownKeys: [],
                TypeCoercionNotes: []));
        }
    }
}
