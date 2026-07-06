using Microsoft.Extensions.DependencyInjection;
using Yagura.Abstractions.Administration;
using Yagura.Web.Administration.Screens;
using Yagura.Web.Circuits;
using Yagura.Web.Components.Common;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// 本番昇格ウィザード画面の表示確認（database.md §6.1・ui.md §4。PR #102 の UX 完成——
/// 接続の項目入力・認証トグル・マスク付きパスワード・退避先入力）。
/// </summary>
/// <remarks>
/// <see cref="CommonComponentRenderHarness"/>（初期描画のみ）でスナップショットの状態ごとの
/// 画面構造・文言を検証する。対話後の状態遷移（検証結果・修復 SQL の表示）はサービス側の
/// 単体テスト（<c>Yagura.Host.Tests</c>）が結果の内容を、本テストが初期状態からの復元を担う。
/// </remarks>
public sealed class PromotionWizardScreenRenderTests
{
    private const string ServiceAccount = @"NT SERVICE\Yagura";

    [Fact]
    public async Task DefaultFormMode_WindowsAuth_ShowsFormFieldsAndConnectingAccount()
    {
        var html = await RenderAsync(Snapshot());

        // 項目入力（既定）: サーバ名・データベース名・認証方式・証明書の信頼。
        Assert.Contains(UiText.PromotionServerNameLabel, html);
        Assert.Contains(UiText.PromotionDatabaseNameLabel, html);
        Assert.Contains(UiText.PromotionAuthModeLabel, html);
        Assert.Contains(UiText.PromotionTrustServerCertificateLabel, html);
        Assert.Contains(UiText.PromotionTrustServerCertificateHelp, html);

        // Windows 統合認証（既定）: 接続に使うアカウントを明示し（database.md §6.1）、
        // パスワード欄は出さない。
        Assert.Contains(ServiceAccount, html);
        Assert.DoesNotContain("type=\"password\"", html);

        // 入力方式の切り替え導線（直接入力は上級者向け）。
        Assert.Contains(UiText.PromotionInputModeForm, html);
        Assert.Contains(UiText.PromotionInputModeRaw, html);
    }

    [Fact]
    public async Task FormMode_SqlAuth_ShowsMaskedPasswordField()
    {
        var html = await RenderAsync(Snapshot() with
        {
            Form = new PromotionConnectionForm(
                "SV01", "Yagura", PromotionAuthenticationMode.SqlServer, "sa", TrustServerCertificate: false),
        });

        Assert.Contains(UiText.PromotionUserNameLabel, html);
        Assert.Contains(UiText.PromotionPasswordLabel, html);
        // パスワードはマスク入力（受け入れ条件——平文で画面に残らない）+ DPAPI の取り扱い注記。
        Assert.Contains("type=\"password\"", html);
        Assert.Contains(UiText.PromotionPasswordHelp, html);
    }

    [Fact]
    public async Task RawMode_ShowsRawFieldWithPasswordKeyNotice_AndMaskedPasswordField()
    {
        var html = await RenderAsync(Snapshot() with
        {
            InputMode = PromotionConnectionInputMode.Raw,
            RawConnectionString = "Server=db;User Id=sa",
        });

        Assert.Contains(UiText.PromotionConnectionStringLabel, html);
        Assert.Contains(UiText.PromotionRawConnectionStringHelp, html);
        Assert.Contains("type=\"password\"", html);
    }

    [Fact]
    public async Task CredentialReentryRequired_ShowsReentryNotice()
    {
        var html = await RenderAsync(Snapshot() with { CredentialReentryRequired = true });

        Assert.Contains(UiText.PromotionCredentialReentryRequired, html);
    }

    [Fact]
    public async Task EvacuationChosen_ShowsDirectoryInputAndCurrentChoice()
    {
        var html = await RenderAsync(Snapshot() with
        {
            Disposal = OldDatabaseDisposal.Evacuate,
            EvacuationDirectory = @"D:\Backup\Yagura",
        });

        Assert.Contains(UiText.PromotionEvacuationDirectoryLabel, html);
        Assert.Contains(UiText.PromotionConfirmEvacuation, html);
        Assert.Contains(@"D:\Backup\Yagura", html);
    }

    [Fact]
    public async Task Executed_ShowsAppliedMessageOnly()
    {
        var html = await RenderAsync(Snapshot() with { Executed = true });

        Assert.Contains(UiText.WizardApplied, html);
        Assert.DoesNotContain(UiText.PromotionServerNameLabel, html);
    }

    private static PromotionWizardSnapshot Snapshot() => new(
        InputMode: PromotionConnectionInputMode.Form,
        Form: null,
        RawConnectionString: null,
        ServiceAccountName: ServiceAccount,
        HasPassword: false,
        ConnectionValidated: false,
        Disposal: null,
        EvacuationDirectory: null,
        ExecuteIdempotencyToken: null,
        Executed: false,
        CredentialReentryRequired: false);

    private static Task<string> RenderAsync(PromotionWizardSnapshot snapshot) =>
        CommonComponentRenderHarness.RenderAsync<PromotionWizardScreen>(
            configureServices: services =>
            {
                services.AddSingleton<IPromotionWizardService>(new FakePromotionWizardService(snapshot));
                services.AddSingleton(new YaguraCircuitContext());
            });

    /// <summary>初期描画（GetSnapshotAsync のみ）に応答する表示確認用のフェイク。</summary>
    private sealed class FakePromotionWizardService(PromotionWizardSnapshot snapshot) : IPromotionWizardService
    {
        public Task<PromotionWizardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);

        public Task<PromotionWizardSnapshot> SetConnectionFormAsync(
            PromotionConnectionForm form,
            string? password = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);

        public Task<PromotionWizardSnapshot> SetRawConnectionStringAsync(
            string connectionString,
            string? password = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);

        public Task<PromotionValidationResult> ValidateConnectionAsync(
            string? operatorAddress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new PromotionValidationResult(true, "ok"));

        public Task<PromotionWizardSnapshot> ChooseOldDatabaseDisposalAsync(
            OldDatabaseDisposal disposal,
            string? evacuationDirectory = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);

        public Task<PromotionApplyResult> ExecuteAsync(
            string idempotencyToken,
            string? operatorAddress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new PromotionApplyResult(
                WizardApplyOutcome.Applied, ConfigurationApplyEffect.RestartRequired, "ok"));
    }
}
