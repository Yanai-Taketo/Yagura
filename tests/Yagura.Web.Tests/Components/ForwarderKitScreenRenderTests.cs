using Microsoft.Extensions.DependencyInjection;
using Yagura.Web.Administration.Screens;
using Yagura.Web.Components.Common;
using Yagura.Web.ForwarderKit;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// フォワーダキット生成画面の表示確認（ADR-0008。既定選択なし・Security 初期オフ・
/// 候補の説明名表示・MSI 同梱の検出状態別表示（設計条件 9）を
/// <see cref="CommonComponentRenderHarness"/> の初期描画で検証する）。
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

    [Fact]
    public async Task InitialRender_MsiNotFound_ShowsPathAndGuidance()
    {
        var html = await RenderAsync(
            new FakeNicCandidateSource([]),
            new FakeForwarderMsiSource(@"C:\ProgramData\Yagura\forwarder", ForwarderMsiLookup.NotFound()));

        Assert.Contains(@"C:\ProgramData\Yagura\forwarder", html);
        // 既定は x64（ForwarderKitScreen 初期選択）。
        Assert.Contains(string.Format(UiText.ForwarderKitMsiNotFoundFormat, ForwarderMsiConstraints.FileNamePattern), html);
        // チェックボックスは出さない。
        Assert.DoesNotContain(UiText.ForwarderKitMsiIncludeCheckbox, html);
    }

    [Fact]
    public async Task InitialRender_MsiSingleDetected_ShowsCheckboxInitiallyUnchecked()
    {
        var details = new ForwarderMsiDetails(
            @"C:\ProgramData\Yagura\forwarder\fluent-bit-4.0.14-win64.msi",
            "fluent-bit-4.0.14-win64.msi",
            "4.0.14",
            "abc123",
            12345);

        var html = await RenderAsync(
            new FakeNicCandidateSource([]),
            new FakeForwarderMsiSource(@"C:\ProgramData\Yagura\forwarder", ForwarderMsiLookup.Single(details)));

        Assert.Contains(UiText.ForwarderKitMsiIncludeCheckbox, html);
        // チェックボックス自体は表示されるが、オプトインのため初期状態は未チェック
        // ——チェック時のみ表示される詳細（ファイル名・版・SHA256）は出ていないはず。
        Assert.DoesNotContain(details.Sha256, html);
    }

    [Fact]
    public async Task InitialRender_MsiArchitectureSelector_DefaultsToX64()
    {
        // ADR-0009 決定7・委任 #4: 収集対象端末のアーキ選択は既定で x64（迷ったら x64）。
        var html = await RenderAsync(new FakeNicCandidateSource([]));

        Assert.Contains(UiText.ForwarderKitMsiArchitectureTitle, html);
        Assert.Contains(UiText.ForwarderKitMsiArchitectureX64, html);
        Assert.Contains(UiText.ForwarderKitMsiArchitectureArm64, html);
        Assert.Contains("value=\"x64\"", html);
        Assert.Contains("value=\"arm64\"", html);
    }

    [Fact]
    public async Task InitialRender_MsiMultipleDetected_ShowsErrorAndFileList()
    {
        var html = await RenderAsync(
            new FakeNicCandidateSource([]),
            new FakeForwarderMsiSource(
                @"C:\ProgramData\Yagura\forwarder",
                ForwarderMsiLookup.Multiple(["fluent-bit-4.0.14-win64.msi", "fluent-bit-4.0.13-win64.msi"])));

        Assert.Contains(UiText.ForwarderKitMsiMultipleErrorTitle, html);
        Assert.Contains("fluent-bit-4.0.14-win64.msi", html);
        Assert.Contains("fluent-bit-4.0.13-win64.msi", html);
        Assert.DoesNotContain(UiText.ForwarderKitMsiIncludeCheckbox, html);
    }

    private static Task<string> RenderAsync(
        INicCandidateSource nicCandidateSource,
        IForwarderMsiSource? forwarderMsiSource = null,
        Yagura.Web.ForwarderKit.ForwarderMsiUploadRuntimeOptions? uploadOptions = null,
        Yagura.Web.Administration.AdminAuthenticationRuntimeOptions? authOptions = null,
        IForwarderMsiStore? forwarderMsiStore = null) =>
        CommonComponentRenderHarness.RenderAsync<ForwarderKitScreen>(
            configureServices: services =>
            {
                services.AddSingleton(nicCandidateSource);
                services.AddSingleton(forwarderMsiSource ?? new FakeForwarderMsiSource(
                    @"C:\ProgramData\Yagura\forwarder",
                    ForwarderMsiLookup.NotFound()));

                // ADR-0020（MSI アップロード）: 既定は機能無効の構成で描画する（既定 false は
                // 本番の既定と同じ——アップロード区画は「無効の理由 + 手動配置ガイド導線」を表示する）。
                services.AddSingleton(uploadOptions ?? new Yagura.Web.ForwarderKit.ForwarderMsiUploadRuntimeOptions(false));
                services.AddSingleton(authOptions ?? new Yagura.Web.Administration.AdminAuthenticationRuntimeOptions(
                    RequireAuthentication: false, WindowsAuthEnabled: false, AppAuthEnabled: false));
                services.AddSingleton(forwarderMsiStore ?? new FakeForwarderMsiStore());
                services.AddSingleton<Yagura.Web.Administration.IForwarderMsiPlacementService>(
                    new FakeForwarderMsiPlacementService());
                services.AddSingleton(new Yagura.Web.Circuits.YaguraCircuitContext());
            });

    private sealed class FakeNicCandidateSource(IReadOnlyList<NicCandidate> candidates) : INicCandidateSource
    {
        public IReadOnlyList<NicCandidate> GetCandidates() => candidates;
    }

    private sealed class FakeForwarderMsiSource(string folderPath, ForwarderMsiLookup lookup) : IForwarderMsiSource
    {
        public string FolderPath => folderPath;

        public ForwarderMsiLookup Lookup(ForwarderMsiArchitecture architecture) => lookup;
    }

    private sealed class FakeForwarderMsiStore : IForwarderMsiStore
    {
        public ForwarderMsiWriteAccess WriteAccess { get; set; } = new(false, "test");

        public string FolderPath => @"C:\ProgramData\Yagura\forwarder";

        public ForwarderMsiWriteAccess CheckWriteAccess() => WriteAccess;

        public int CleanupStagingFiles() => 0;

        public Task<ForwarderMsiStageResult> StageAsync(
            ForwarderMsiArchitecture architecture, Stream content, long? declaredLength, CancellationToken cancellationToken) =>
            throw new NotSupportedException("初期描画テストでは呼ばれない。");

        public ForwarderMsiCommitResult Commit(string stagingToken, bool versionMismatchAcknowledged, bool replaceAcknowledged) =>
            throw new NotSupportedException("初期描画テストでは呼ばれない。");

        public ForwarderMsiDiscardResult Discard(string stagingToken) =>
            throw new NotSupportedException("初期描画テストでは呼ばれない。");

        public ForwarderMsiDeleteResult Delete(ForwarderMsiArchitecture architecture, string expectedSha256) =>
            throw new NotSupportedException("初期描画テストでは呼ばれない。");
    }

    private sealed class FakeForwarderMsiPlacementService : Yagura.Web.Administration.IForwarderMsiPlacementService
    {
        public Task<ForwarderMsiCommitResult> CommitAsync(
            string stagingToken, bool versionMismatchAcknowledged, bool replaceAcknowledged,
            ForwarderMsiArchitecture architecture, Yagura.Web.Administration.ForwarderMsiOperatorContext operatorContext) =>
            throw new NotSupportedException("初期描画テストでは呼ばれない。");

        public Task<ForwarderMsiDiscardResult> DiscardAsync(
            string stagingToken, ForwarderMsiArchitecture architecture,
            Yagura.Web.Administration.ForwarderMsiOperatorContext operatorContext) =>
            throw new NotSupportedException("初期描画テストでは呼ばれない。");

        public Task<ForwarderMsiDeleteResult> DeleteAsync(
            ForwarderMsiArchitecture architecture, string expectedSha256,
            Yagura.Web.Administration.ForwarderMsiOperatorContext operatorContext) =>
            throw new NotSupportedException("初期描画テストでは呼ばれない。");
    }
}
