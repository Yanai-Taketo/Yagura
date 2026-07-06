using Microsoft.Extensions.DependencyInjection;
using Yagura.Web.Administration.Screens;
using Yagura.Web.Components.Common;
using Yagura.Web.ForwarderKit;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// フォワーダキット生成画面の表示確認（ADR-0008。既定選択なし・Security 初期オフ・
/// 候補の説明名表示を <see cref="CommonComponentRenderHarness"/> の初期描画で検証する）。
/// </summary>
public sealed class ForwarderKitScreenRenderTests
{
    [Fact]
    public async Task InitialRender_NoCandidateChecked_AndSecurityUnchecked()
    {
        var html = await RenderAsync(new FakeNicCandidateSource(
        [
            new NicCandidate("192.168.1.10", "Ethernet (Intel(R) Ethernet Connection)"),
        ]));

        // 候補ラジオ・手入力ラジオのいずれも checked="checked" を含まない(既定選択なし)。
        Assert.DoesNotContain("checked=\"checked\"", html);
        Assert.DoesNotContain("checked=\"true\"", html);

        Assert.Contains(UiText.ForwarderKitDestinationNote, html);
    }

    [Fact]
    public async Task InitialRender_ShowsCandidateAddressAndDescription()
    {
        var html = await RenderAsync(new FakeNicCandidateSource(
        [
            new NicCandidate("192.168.1.10", "Ethernet (Intel(R) Ethernet Connection)"),
            new NicCandidate("10.0.0.5", "Wi-Fi (Realtek 8821CE)"),
        ]));

        Assert.Contains("192.168.1.10", html);
        Assert.Contains("Ethernet (Intel(R) Ethernet Connection)", html);
        Assert.Contains("10.0.0.5", html);
        Assert.Contains("Wi-Fi (Realtek 8821CE)", html);
    }

    [Fact]
    public async Task InitialRender_NoCandidates_ShowsNoticeAndManualOption()
    {
        var html = await RenderAsync(new FakeNicCandidateSource([]));

        Assert.Contains(UiText.ForwarderKitNoCandidates, html);
        Assert.Contains(UiText.ForwarderKitManualEntryOption, html);
    }

    [Fact]
    public async Task InitialRender_ChannelCheckboxes_SystemAndApplicationOnSecurityOff()
    {
        var html = await RenderAsync(new FakeNicCandidateSource([]));

        Assert.Contains(UiText.ForwarderKitChannelSystem, html);
        Assert.Contains(UiText.ForwarderKitChannelApplication, html);
        Assert.Contains(UiText.ForwarderKitChannelSecurity, html);
        // Security の警告文言は初期状態(オフ)では表示されない。
        Assert.DoesNotContain(UiText.ForwarderKitSecurityChannelWarning, html);
    }

    [Fact]
    public async Task InitialRender_ShowsVerifiedVersionAndMsiNotIncludedNote()
    {
        var html = await RenderAsync(new FakeNicCandidateSource([]));

        Assert.Contains(ForwarderKitConstraints.VerifiedFluentBitVersion, html);
        Assert.Contains(UiText.ForwarderKitMsiNotIncludedNote, html);
    }

    private static Task<string> RenderAsync(INicCandidateSource nicCandidateSource) =>
        CommonComponentRenderHarness.RenderAsync<ForwarderKitScreen>(
            configureServices: services =>
            {
                services.AddSingleton(nicCandidateSource);
            });

    private sealed class FakeNicCandidateSource(IReadOnlyList<NicCandidate> candidates) : INicCandidateSource
    {
        public IReadOnlyList<NicCandidate> GetCandidates() => candidates;
    }
}
