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

    private static Task<string> RenderAsync(IReadOnlyList<PendingRestartKey> pendingKeys) =>
        CommonComponentRenderHarness.RenderAsync<AdminHome>(
            configureServices: services =>
                services.AddSingleton<IConfigurationReloadService>(new FakeReloadService(pendingKeys)));

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
