using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Yagura.Abstractions.Administration;
using Yagura.Web.Administration.Screens;
using Yagura.Web.Circuits;
using Yagura.Web.Components.Common;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// 本番昇格ウィザードの切替実行が、成立したときだけ再起動待ちカードへ計上されることの検証
/// （configuration.md §3・database.md §6.1）。
/// </summary>
/// <remarks>
/// 保存先の切替は再起動まで効かず、再起動待ちカードのデータ源は再読み込みの実行時にしか更新されない。
/// 「切替が成立したら再読み込みを呼ぶ」ことが計上の経路そのものであり、ここで固定する。成立しなかった
/// 実行（競合・トークン不一致）では設定ファイルが変わっていないため呼ばない。確認ダイアログを経る
/// 実行経路を通すため、<c>MudDialogProvider</c> と同居させて描画する。
/// </remarks>
public sealed class PromotionWizardScreenExecuteTests : IAsyncLifetime
{
    private readonly BunitContext _ctx = new();
    private readonly ScriptedPromotionWizardService _service = new();
    private readonly RecordingConfigurationReloadService _reload = new();
    private readonly RecordingNotifier _notifier = new();

    public PromotionWizardScreenExecuteTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();
        _ctx.Services.AddSingleton<IPromotionWizardService>(_service);
        _ctx.Services.AddSingleton<ILogMigrationService>(new NotPromotedLogMigrationService());
        _ctx.Services.AddSingleton(new YaguraCircuitContext());
        _ctx.Services.AddSingleton(new YaguraCircuitAuthenticationStateProvider());
        _ctx.Services.AddSingleton<IYaguraNotifier>(_notifier);
        _ctx.Services.AddSingleton<IConfigurationReloadService>(_reload);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor の一部サービスは IAsyncDisposable のみを実装するため、コンテナは非同期に破棄する。
    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    [Fact]
    public async Task Execute_Applied_RunsReloadSoThePendingRestartCardIsUpdated()
    {
        _service.NextOutcome = WizardApplyOutcome.Applied;
        var cut = _ctx.Render<ProviderHost>();

        await ExecuteAndConfirmAsync(cut);

        Assert.Equal(1, _service.ExecuteCount);
        Assert.Equal(1, _reload.ReloadCount);
        Assert.Empty(_notifier.Warnings);
        Assert.Empty(_notifier.Errors);
    }

    [Fact]
    public async Task Execute_AppliedButReloadRejected_WarnsThatTheSwitchWasSavedButNotCounted()
    {
        _service.NextOutcome = WizardApplyOutcome.Applied;
        _reload.NextResult = RecordingConfigurationReloadService.RejectedWith("受信ポートが不正です");
        var cut = _ctx.Render<ProviderHost>();

        await ExecuteAndConfirmAsync(cut);

        var warning = Assert.Single(_notifier.Warnings);
        Assert.Equal(string.Format(UiText.AdminSaveReloadRejectedFormat, "受信ポートが不正です"), warning);
        Assert.Empty(_notifier.Errors);
    }

    [Theory]
    [InlineData(WizardApplyOutcome.Conflict)]
    [InlineData(WizardApplyOutcome.InvalidToken)]
    public async Task Execute_NotApplied_DoesNotRunReload(WizardApplyOutcome outcome)
    {
        _service.NextOutcome = outcome;
        var cut = _ctx.Render<ProviderHost>();

        await ExecuteAndConfirmAsync(cut);

        Assert.Equal(1, _service.ExecuteCount);
        Assert.Equal(0, _reload.ReloadCount);
        Assert.Empty(_notifier.Errors);
    }

    /// <summary>切替実行ボタンを押し、破壊的操作の確認ダイアログで確定して、実行の完了まで待つ。</summary>
    private static async Task ExecuteAndConfirmAsync(IRenderedComponent<ProviderHost> cut)
    {
        var execute = cut.FindAll("button")
            .Single(button => button.TextContent.Trim() == UiText.PromotionExecute);

        // 実行ボタンのハンドラは確認ダイアログの結果を待つため、確定するまで完了しない。
        var executing = execute.ClickAsync(new MouseEventArgs());

        // 既定 1 秒の待機は遅い CI ランナーで不足し得るため延長する。
        cut.WaitForAssertion(
            () => Assert.Contains(
                cut.FindAll("button"),
                button => button.TextContent.Trim() == UiText.PromotionExecuteConfirmAction),
            TimeSpan.FromSeconds(30));
        var confirm = cut.FindAll("button")
            .Single(button => button.TextContent.Trim() == UiText.PromotionExecuteConfirmAction);
        await confirm.ClickAsync(new MouseEventArgs());

        await executing;
    }

    /// <summary>確認ダイアログを描画できるよう、実アプリの MainLayout と同じくプロバイダを同居させる。</summary>
    private sealed class ProviderHost : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudBlazor.MudDialogProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<PromotionWizardScreen>(2);
            builder.CloseComponent();
        }
    }

    // ------------------------------------------------------------------
    // フェイク
    // ------------------------------------------------------------------

    /// <summary>接続検証済みで、切替を実行できる状態を返すフェイク。</summary>
    private sealed class ScriptedPromotionWizardService : IPromotionWizardService
    {
        private static readonly PromotionWizardSnapshot ReadyToExecute = new(
            InputMode: PromotionConnectionInputMode.Form,
            Form: new PromotionConnectionForm(
                "SV01", "Yagura", PromotionAuthenticationMode.WindowsIntegrated, null, TrustServerCertificate: false),
            RawConnectionString: null,
            ServiceAccountName: @"NT SERVICE\Yagura",
            HasPassword: false,
            ConnectionValidated: true,
            ExecuteIdempotencyToken: "execute-token",
            Executed: false,
            CredentialReentryRequired: false);

        internal WizardApplyOutcome NextOutcome { get; set; } = WizardApplyOutcome.Applied;

        internal int ExecuteCount { get; private set; }

        public Task<PromotionWizardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ReadyToExecute);

        public Task<PromotionWizardSnapshot> SetConnectionFormAsync(
            PromotionConnectionForm form,
            string? password = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PromotionWizardSnapshot> SetRawConnectionStringAsync(
            string connectionString,
            string? password = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PromotionValidationResult> ValidateConnectionAsync(
            string? operatorAddress = null,
            string? operatorScheme = null,
            string? operatorPrincipal = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PromotionApplyResult> ExecuteAsync(
            string idempotencyToken,
            string? operatorAddress = null,
            string? operatorScheme = null,
            string? operatorPrincipal = null,
            CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            return Task.FromResult(new PromotionApplyResult(
                NextOutcome, ConfigurationApplyEffect.RestartRequired, "保存先の切り替えを設定に保存しました。"));
        }
    }

    private sealed class NotPromotedLogMigrationService : ILogMigrationService
    {
        public Task<LogMigrationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new LogMigrationStatus(LogMigrationAvailability.NotPromoted));

        public Task<LogMigrationResult> RunAsync(
            string? operatorAddress,
            string? authenticationScheme,
            string? authenticatedPrincipal,
            IProgress<LogMigrationProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingNotifier : IYaguraNotifier
    {
        internal List<string> Warnings { get; } = [];

        internal List<string> Errors { get; } = [];

        public void NotifyOk(string message)
        {
        }

        public void NotifyInfo(string message)
        {
        }

        public void NotifyWarning(string message) => Warnings.Add(message);

        public void NotifyError(string message) => Errors.Add(message);
    }
}
