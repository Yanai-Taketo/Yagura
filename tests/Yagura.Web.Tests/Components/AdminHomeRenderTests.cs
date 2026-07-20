using Microsoft.Extensions.DependencyInjection;
using Yagura.Abstractions.Administration;
using Yagura.Web.Administration.Screens;
using Yagura.Web.Components.Common;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// 設定トップ（/admin）の表示確認。再起動待ちキーの常設カード（Issue #286。2026-07-18
/// オーナー裁定——認証済み管理面のみ・検出時刻併記・再起動で自然消滅）の出し分けを固定する。
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

    private static Task<string> RenderAsync(
        IReadOnlyList<PendingRestartKey> pendingKeys, EmailNotificationStatus? emailStatus = null) =>
        CommonComponentRenderHarness.RenderAsync<AdminHome>(
            configureServices: services =>
            {
                services.AddSingleton<IConfigurationReloadService>(new FakeReloadService(pendingKeys));
                services.AddSingleton<IEmailNotificationAdminService>(
                    new FakeEmailNotificationAdminService(emailStatus ?? EmailStatus(enabled: false)));
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
