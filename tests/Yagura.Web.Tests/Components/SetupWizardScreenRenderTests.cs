using Microsoft.Extensions.DependencyInjection;
using Yagura.Abstractions.Administration;
using Yagura.Web.Administration.Screens;
using Yagura.Web.Circuits;
using Yagura.Web.Components.Common;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// 初期セットアップウィザード画面の表示確認。保存後の自動反映チェックボックス
/// （Issue #287。2026-07-18 オーナー裁定——チェックボックス式・既定オン）の確認ステップでの
/// 描画を固定する。適用後の自動反映の実行と結果（監査 2001 + 2016 の 2 件）はサービス側の
/// 合成テスト（<c>ConfigurationReloadServiceTests</c>）が担う。
/// </summary>
public sealed class SetupWizardScreenRenderTests
{
    [Fact]
    public async Task ReviewStep_ShowsAutoApplyCheckbox_DefaultOn()
    {
        var html = await RenderAsync(ReviewSnapshot());

        Assert.Contains(UiText.WizardAutoApplyLabel, html);
        Assert.Contains(UiText.WizardAutoApplyHelp, html);
        // 既定オン（裁定）——ネイティブ checkbox の checked 属性で確認する
        //（確認ステップのチェックボックスはこの 1 つだけ）。
        Assert.Contains("type=\"checkbox\"", html);
        Assert.Contains("checked", html);
    }

    [Fact]
    public async Task ReceptionStep_DoesNotShowAutoApplyCheckbox()
    {
        var html = await RenderAsync(InitialSnapshot());

        // 自動反映の選択は確認（保存）ステップの関心事——入力ステップには出さない。
        Assert.DoesNotContain(UiText.WizardAutoApplyLabel, html);
    }

    private static SetupWizardSnapshot InitialSnapshot() => new(
        ConfirmedSteps: [],
        NextStep: SetupWizardStep.Reception,
        ConfirmedValues: new Dictionary<string, string>(),
        ApplyIdempotencyToken: null,
        Applied: false);

    private static SetupWizardSnapshot ReviewSnapshot() => new(
        ConfirmedSteps: [SetupWizardStep.Reception, SetupWizardStep.ViewerAccess, SetupWizardStep.Retention],
        NextStep: SetupWizardStep.Review,
        ConfirmedValues: new Dictionary<string, string>
        {
            [SetupWizardValueKeys.UdpPort] = "514",
            [SetupWizardValueKeys.TcpPort] = "514",
            [SetupWizardValueKeys.ViewerHttpPort] = "8514",
            [SetupWizardValueKeys.ViewerPublicAccess] = "Lan",
            [SetupWizardValueKeys.AdminHttpPort] = "8515",
            [SetupWizardValueKeys.RetentionDays] = "30",
        },
        ApplyIdempotencyToken: "token",
        Applied: false);

    private static Task<string> RenderAsync(SetupWizardSnapshot snapshot) =>
        CommonComponentRenderHarness.RenderAsync<SetupWizardScreen>(
            configureServices: services =>
            {
                services.AddSingleton<ISetupWizardService>(new FakeSetupWizardService(snapshot));
                services.AddSingleton<IConfigurationReloadService>(new FakeReloadService());
                services.AddSingleton(new YaguraCircuitContext());
            });

    /// <summary>初期描画（GetSnapshotAsync のみ）に応答する表示確認用のフェイク。</summary>
    private sealed class FakeSetupWizardService(SetupWizardSnapshot snapshot) : ISetupWizardService
    {
        public Task<SetupWizardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);

        public Task<SetupWizardSnapshot> ConfirmStepAsync(
            SetupWizardStep step,
            IReadOnlyDictionary<string, string> values,
            CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);

        public Task<SetupWizardSnapshot> GoBackAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);

        public Task<SetupWizardSnapshot> BeginReconfigurationAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);

        public Task<SetupWizardApplyResult> ApplyAsync(
            string idempotencyToken,
            string? operatorAddress = null,
            string? operatorScheme = null,
            string? operatorPrincipal = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>初期描画では呼ばれないフェイク（注入解決のためだけに登録する）。</summary>
    private sealed class FakeReloadService : IConfigurationReloadService
    {
        public IReadOnlyList<PendingRestartKey> GetPendingRestartKeys() => [];

        public Task<ConfigurationReloadResult> ReloadAsync(
            string? operatorAddress,
            string? authenticationScheme,
            string? authenticatedPrincipal,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
