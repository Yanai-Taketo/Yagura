using Microsoft.Extensions.DependencyInjection;
using Yagura.Abstractions.Administration;
using Yagura.Web.Administration.Screens;
using Yagura.Web.Components.Common;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// 設定トップ（/admin）の表示確認。再起動待ちキーの常設カード（認証済み管理面のみ・検出時刻併記・再起動で
/// 自然消滅）と、以前の保存先のファイルの後始末が残っている常設カード（database.md §6.3 の状態表）の
/// 出し分けを固定する。
/// </summary>
public sealed class AdminHomeRenderTests
{
    [Fact]
    public async Task NoPendingRestartKeys_DoesNotShowCard()
    {
        var html = await RenderAsync([]);

        // 入口のナビゲーションは常時表示。
        Assert.Contains(UiText.AdminSetupWizardTitle, html);

        // 再起動待ちなし = カード自体を出さない（常設カードは再起動で自然に消える）。
        Assert.DoesNotContain(UiText.AdminPendingRestartCardTitle, html);
    }

    [Fact]
    public async Task PendingRestartKeys_ShowsCardWithKeysAndDetectionTime()
    {
        var detectedAt = DateTimeOffset.Parse("2026-07-18T01:23:45Z");
        var html = await RenderAsync(
        [
            new PendingRestartKey("Viewer:HttpPort", detectedAt),
            new PendingRestartKey("Storage:SqliteFileName", detectedAt),
        ]);

        Assert.Contains(UiText.AdminPendingRestartCardTitle, html);
        Assert.Contains(UiText.AdminPendingRestartCardDescription, html);
        Assert.Contains("Viewer:HttpPort", html);
        Assert.Contains("Storage:SqliteFileName", html);

        // 検出時刻の併記（YaguraTimestamp——機械可読の UTC ISO 8601 を datetime 属性に出す）。
        Assert.Contains(UiText.AdminPendingRestartDetectedAtLabel, html);
        Assert.Contains("2026-07-18T01:23:45", html);
    }

    [Fact]
    public void RestartNotes_ReferToTheCardByItsDisplayedTitle()
    {
        // 他画面の案内文がカードを名指しするときは、管理ホームに実際に出る見出しで呼ぶ
        // （違う名前で案内すると、利用者が入口で該当カードを見つけられない）。
        Assert.Contains($"「{UiText.AdminPendingRestartCardTitle}」", UiText.ViewerHttpsRestartNote);
    }

    [Fact]
    public async Task StorageAvailable_DoesNotShowTheDegradedBanner()
    {
        var html = await RenderAsync([]);

        Assert.DoesNotContain(UiText.AdminHomeStorageUnavailableTitle, html);
    }

    [Fact]
    public async Task StorageUnavailable_ShowsTheDegradedBannerOnTheEntryPage()
    {
        // ADR-0023 決定 1 の「可視化された縮退」: 起動を失敗させずに縮退させる以上、縮退している
        // ことが必ず見えるのが成立条件。Windows 認証・認証無効の構成では管理面に入れてしまうため、
        // 入れた側にも同じ事実を見せる（メール通知未設定の環境では、管理画面を開いた瞬間に
        // 分かることが現実的な発見経路——ペルソナレビュー 佐藤の指摘）。
        var degraded = new Yagura.Web.Administration.StorageAvailabilityState();
        degraded.MarkUnavailable("SqlException");

        var html = await RenderAsync([], storageAvailability: degraded);

        Assert.Contains(UiText.AdminHomeStorageUnavailableTitle, html);
        Assert.Contains(UiText.AdminHomeStorageUnavailableDescription, html);

        // 失敗の詳細（例外型名）は画面に出さない——出所はイベントログへ寄せる（ADR-0023 決定 1）。
        Assert.DoesNotContain("SqlException", html);
    }

    // ------------------------------------------------------------------
    // 以前の保存先（組み込みデータベース）のファイルの後始末が残っている常設カード（database.md §6.3 状態表）
    // ------------------------------------------------------------------

    [Fact]
    public async Task OldDatabaseDisposal_NotApplicable_DoesNotShowTheCard()
    {
        var html = await RenderAsync([], disposalStatus: FakeOldDatabaseDisposalService.NotApplicable());

        Assert.DoesNotContain(UiText.AdminOldDatabaseDisposalCardTitle, html);
    }

    [Fact]
    public async Task OldDatabaseDisposal_CompletedInThisProcess_DoesNotShowTheCard()
    {
        // 完了は「対象外」に含まれる（処分の対象がいずれも存在しない）——カードは自然に消える。
        var completed = new OldDatabaseDisposalResult(OldDatabaseDisposalOutcome.Completed, "以前の保存先のファイルを削除しました。");
        var html = await RenderAsync([], disposalStatus: FakeOldDatabaseDisposalService.NotApplicable(completed));

        Assert.DoesNotContain(UiText.AdminOldDatabaseDisposalCardTitle, html);
    }

    [Fact]
    public async Task OldDatabaseDisposal_Pending_ShowsTheCard_LeadingWithIngestionContinuing_AndLinksToThePromotionPage()
    {
        var html = await RenderAsync([], disposalStatus: FakeOldDatabaseDisposalService.Pending());

        Assert.Contains(UiText.AdminOldDatabaseDisposalCardTitle, html);
        Assert.Contains(UiText.AdminOldDatabaseDisposalCardDescription, html);
        Assert.StartsWith("受信と保存は正常に続いています。", UiText.AdminOldDatabaseDisposalCardDescription);
        Assert.Contains(UiText.AdminOldDatabaseDisposalCardLink, html);
        Assert.Contains("href=\"/admin/promotion\"", html);
        Assert.Contains(string.Format(UiText.AdminOldDatabaseDisposalCardFilesFormat, 2, "3 MB"), html);

        // カードは旧ファイルを開かずに判定するため、中身を「切り替え前のログ」と決めつけない。
        Assert.DoesNotContain("切り替え前", html);
    }

    [Fact]
    public async Task OldDatabaseDisposal_MigrationRunning_ShowsTheCardSayingSo()
    {
        var html = await RenderAsync([], disposalStatus: FakeOldDatabaseDisposalService.MigrationRunning());

        Assert.Contains(UiText.AdminOldDatabaseDisposalCardTitle, html);
        Assert.Contains(UiText.OldDatabaseDisposalMigrationRunning, html);
    }

    [Theory]
    [InlineData(OldDatabaseDisposalStage.Exporting)]
    [InlineData(OldDatabaseDisposalStage.Deleting)]
    public async Task OldDatabaseDisposal_Running_ShowsTheCardSayingSo(OldDatabaseDisposalStage stage)
    {
        var html = await RenderAsync([], disposalStatus: FakeOldDatabaseDisposalService.Running(stage));

        Assert.Contains(UiText.AdminOldDatabaseDisposalCardTitle, html);
        Assert.Contains(UiText.AdminOldDatabaseDisposalCardRunning, html);
    }

    [Fact]
    public async Task OldDatabaseDisposal_LastAttemptFailed_ShowsTheCardWithTheLastResultAndReason()
    {
        var failed = new OldDatabaseDisposalResult(
            OldDatabaseDisposalOutcome.DeletePartiallyFailed,
            "元のファイルの一部を削除できませんでした（yagura.db）。",
            OldDatabaseDisposalReason.DeleteFailed);
        var html = await RenderAsync([], disposalStatus: FakeOldDatabaseDisposalService.LastAttemptFailed(failed));

        Assert.Contains(UiText.AdminOldDatabaseDisposalCardTitle, html);
        Assert.Contains(string.Format(UiText.AdminOldDatabaseDisposalCardLastFailedFormat, failed.Message), html);
    }

    [Fact]
    public async Task OldDatabaseDisposal_RecordedOldFileUnderAnotherName_ShowsTheCardWithThePathAndManualGuidance()
    {
        // 切替の後に設定の名前を変えた場合: 処分の対象は無いが未完了として扱う（1041 と同じ判定）。
        const string misplaced = @"C:\ProgramData\Yagura\old-name.db";
        var status = FakeOldDatabaseDisposalService.NotApplicable() with { MisplacedRecordedOldDatabasePath = misplaced };
        var html = await RenderAsync([], disposalStatus: status);

        Assert.Contains(UiText.AdminOldDatabaseDisposalCardTitle, html);
        Assert.Contains(string.Format(UiText.OldDatabaseDisposalMisplacedFormat, misplaced), html);
    }

    [Fact]
    public void SwitchWarning_RefersToTheDisposalCardByItsDisplayedTitle()
    {
        // 切替前の予告が「管理画面に『…』と表示されます」と名指しする名前は、管理ホームに実際に出る見出しと同じにする。
        Assert.Contains($"『{UiText.AdminOldDatabaseDisposalCardTitle}』", UiText.PromotionSwitchWarning);
    }

    private static Task<string> RenderAsync(
        IReadOnlyList<PendingRestartKey> pendingKeys,
        EmailNotificationStatus? emailStatus = null,
        Yagura.Web.Administration.StorageAvailabilityState? storageAvailability = null,
        OldDatabaseDisposalStatus? disposalStatus = null) =>
        CommonComponentRenderHarness.RenderAsync<AdminHome>(
            configureServices: services =>
            {
                services.AddSingleton<IConfigurationReloadService>(new FakeReloadService(pendingKeys));
                services.AddSingleton<IEmailNotificationAdminService>(
                    new FakeEmailNotificationAdminService(emailStatus ?? EmailStatus(enabled: false)));
                services.AddSingleton<IOldDatabaseDisposalService>(new FakeOldDatabaseDisposalService
                {
                    Status = disposalStatus ?? FakeOldDatabaseDisposalService.NotApplicable(),
                });

                if (storageAvailability is not null)
                {
                    services.AddSingleton(storageAvailability);
                }
            });

    private static EmailNotificationStatus EmailStatus(
        bool enabled,
        DateTimeOffset? lastSuccessAt = null,
        string? lastFailureKind = null,
        bool disabledByInvalidConfiguration = false) =>
        new(
            Enabled: enabled,
            From: enabled ? "yagura@example.com" : null,
            To: enabled ? ["ops@example.com"] : [],
            SmtpHost: enabled ? "smtp.example.com" : null,
            SmtpPort: 25,
            Security: "auto",
            Username: null,
            PasswordConfigured: false,
            Health: new EmailNotificationChannelHealth(
                LastSuccessAt: lastSuccessAt,
                LastFailureKind: lastFailureKind,
                LastFailureDetail: lastFailureKind is null ? null : "説明",
                QueueDepth: 0,
                DroppedCount: 3,
                SuppressedCount: 5,
                LastSuppressedAt: null,
                SuppressedCountByEventId: new Dictionary<int, int>(),
                DisabledByInvalidConfiguration: disabledByInvalidConfiguration,
                ConfigurationFileError: null));

    // ------------------------------------------------------------------
    // メール通知チャネルの健全性カード（ADR-0017 決定 5。Issue #386）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Render_EmailNotificationEnabled_ShowsTheChannelHealthCardOnTheEntryPage()
    {
        // 「日常動線」= 管理面入口での常設表示（設定画面は設定時にしか開かれない——決定 5）。
        var html = await RenderAsync(
            [], EmailStatus(enabled: true, lastSuccessAt: new DateTimeOffset(2026, 7, 20, 1, 2, 3, TimeSpan.Zero)));

        Assert.Contains(UiText.AdminEmailHealthCardTitle, html);
        Assert.Contains(UiText.EmailNotificationHealthLastSuccess, html);
        Assert.Contains(UiText.EmailNotificationHealthDropped, html);
        Assert.Contains("/admin/email-notification", html);
    }

    [Fact]
    public async Task Render_EmailNotificationDisabled_DoesNotShowTheCard()
    {
        // 未使用の環境に常設ノイズを出さない。
        var html = await RenderAsync([], EmailStatus(enabled: false));

        Assert.DoesNotContain(UiText.AdminEmailHealthCardTitle, html);
    }

    [Fact]
    public async Task Render_EnabledButDisabledByInvalidConfiguration_SurfacesTheDegradedState()
    {
        // 「有効にしたつもりで送られていない」（決定 2 の縮退）は入口でも最も目立たせる。
        var html = await RenderAsync(
            [], EmailStatus(enabled: true, disabledByInvalidConfiguration: true));

        Assert.Contains(UiText.EmailNotificationDisabledByInvalidConfiguration, html);
    }

    /// <summary>初期描画（GetStatusAsync のみ）に応答する表示確認用のフェイク。</summary>
    private sealed class FakeEmailNotificationAdminService(EmailNotificationStatus status)
        : IEmailNotificationAdminService
    {
        public Task<EmailNotificationStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(status);

        public Task<EmailNotificationConfigureResult> ConfigureAsync(
            EmailNotificationSettings settings,
            string? operatorAddress = null,
            string? operatorScheme = null,
            string? operatorPrincipal = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EmailNotificationTestResult> SendTestAsync(
            EmailNotificationSettings settings,
            string? operatorAddress = null,
            string? operatorScheme = null,
            string? operatorPrincipal = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>初期描画（GetPendingRestartKeys のみ）に応答する表示確認用のフェイク。</summary>
    private sealed class FakeReloadService(IReadOnlyList<PendingRestartKey> pendingKeys)
        : IConfigurationReloadService
    {
        public IReadOnlyList<PendingRestartKey> GetPendingRestartKeys() => pendingKeys;

        public Task<ConfigurationReloadResult> ReloadAsync(
            string? operatorAddress,
            string? authenticationScheme,
            string? authenticatedPrincipal,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
