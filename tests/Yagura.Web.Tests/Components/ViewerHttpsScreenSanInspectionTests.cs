using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Yagura.Abstractions.Administration;
using Yagura.Web.Administration.Screens;
using Yagura.Web.Circuits;
using Yagura.Web.Components.Common;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// 閲覧 HTTPS 設定画面の SAN 助言検査が、証明書の選択（転写・一覧の更新を含む）に追随することの検証。
/// </summary>
/// <remarks>
/// SAN 検査は「警告が出続ける証明書」を保存前に見分けるための面である（ADR-0022 決定 4）。
/// 選択を変えても表示が追随しなければ、利用者は前に選んだ証明書の結果を見て判断してしまう。
/// 選択変更は共通パネルの <c>SelectedThumbprintChanged</c> を直接呼んで起こし、MudRadio の
/// DOM 構造には依存しない。
/// </remarks>
public sealed class ViewerHttpsScreenSanInspectionTests : IAsyncLifetime
{
    private const string ThumbprintA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string ThumbprintB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    private static readonly string[] ServerNames = ["yagura01", "yagura01.example.local"];

    // A: 短名が SAN に無い（不整合）。B: 短名・FQDN とも SAN にある（整合）。
    private static readonly ViewerHttpsSanInspection InspectionA = new(
        RequiredServerNames: ServerNames,
        MissingServerNames: ["yagura01"],
        CertificateDnsNames: ["yagura01.example.local"],
        HasSanExtension: true);

    private static readonly ViewerHttpsSanInspection InspectionB = new(
        RequiredServerNames: ServerNames,
        MissingServerNames: [],
        CertificateDnsNames: ["yagura01.example.local", "yagura01"],
        HasSanExtension: true);

    private static readonly string MissingTextA =
        string.Format(UiText.ViewerHttpsSanMissingFormat, "yagura01");

    private static readonly string SatisfiedTextB =
        string.Format(UiText.ViewerHttpsSanSatisfiedFormat, string.Join("、", ServerNames));

    private readonly BunitContext _ctx = new();
    private readonly SwitchableStoreReader _store = new();
    private readonly ScriptedViewerHttpsAdminService _service = new();

    public ViewerHttpsScreenSanInspectionTests()
    {
        _store.Candidates = [Candidate(ThumbprintA, "viewer-a"), Candidate(ThumbprintB, "viewer-b")];
        _service.Inspections[ThumbprintA] = InspectionA;
        _service.Inspections[ThumbprintB] = InspectionB;

        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();
        _ctx.Services.AddSingleton<IViewerHttpsAdminService>(_service);
        _ctx.Services.AddSingleton<ICertificateStoreReader>(_store);
        _ctx.Services.AddSingleton(new YaguraCircuitContext());
        _ctx.Services.AddSingleton(new YaguraCircuitAuthenticationStateProvider());
        _ctx.Services.AddScoped<IYaguraNotifier, YaguraSnackbarNotifier>();
        _ctx.Services.AddSingleton<IConfigurationReloadService>(new NoopReloadService());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor の一部サービスは IAsyncDisposable のみを実装するため非同期に破棄する。
    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    [Fact]
    public async Task ChangingSelection_ReinspectsAndShowsTheResultForTheCurrentCertificate()
    {
        // 初回設定（拇印を一度も保存していない）でも、選んだ時点で結果が出なければならない。
        var cut = _ctx.Render<ViewerHttpsScreen>();
        Assert.DoesNotContain(UiText.ViewerHttpsSanTitle, cut.Markup, StringComparison.Ordinal);

        await SelectAsync(cut, ThumbprintA);
        Assert.Contains(MissingTextA, cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(SatisfiedTextB, cut.Markup, StringComparison.Ordinal);

        await SelectAsync(cut, ThumbprintB);
        Assert.Contains(SatisfiedTextB, cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(MissingTextA, cut.Markup, StringComparison.Ordinal);

        await SelectAsync(cut, ThumbprintA);
        Assert.Contains(MissingTextA, cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(SatisfiedTextB, cut.Markup, StringComparison.Ordinal);

        Assert.Equal([ThumbprintA, ThumbprintB, ThumbprintA], _service.InspectedThumbprints);
    }

    [Fact]
    public async Task SelectingNone_HidesTheSanPanelWithoutInspecting()
    {
        _service.Status = StatusWith(certificateThumbprint: ThumbprintA);
        var cut = _ctx.Render<ViewerHttpsScreen>();
        Assert.Contains(MissingTextA, cut.Markup, StringComparison.Ordinal);

        await SelectAsync(cut, string.Empty);

        Assert.DoesNotContain(UiText.ViewerHttpsSanTitle, cut.Markup, StringComparison.Ordinal);
        Assert.Equal([ThumbprintA], _service.InspectedThumbprints);
    }

    [Fact]
    public async Task StaleInspectionResponse_DoesNotOverwriteTheLatestSelection()
    {
        var cut = _ctx.Render<ViewerHttpsScreen>();
        var pendingA = _service.DeferNext(ThumbprintA);

        // A の検査が返らないうちに B を選び、その後で A の応答を届ける。
        var selectA = SelectAsync(cut, ThumbprintA);
        Assert.False(selectA.IsCompleted);

        await SelectAsync(cut, ThumbprintB);
        Assert.Contains(SatisfiedTextB, cut.Markup, StringComparison.Ordinal);

        pendingA.SetResult(InspectionA);
        await selectA;

        Assert.Contains(SatisfiedTextB, cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(MissingTextA, cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyFromAdminHttps_InspectsTheCopiedCertificate()
    {
        _service.Status = StatusWith(adminHttpsCertificateThumbprint: ThumbprintB);
        var cut = _ctx.Render<ViewerHttpsScreen>();

        await ClickButtonAsync(cut, UiText.ViewerHttpsCopyFromAdminButton);

        Assert.Contains(SatisfiedTextB, cut.Markup, StringComparison.Ordinal);
        Assert.Equal([ThumbprintB], _service.InspectedThumbprints);
    }

    [Fact]
    public async Task RefreshCertificates_ReinspectsTheSelectedCertificate()
    {
        // 永続値の拇印がストアに無い（検査は解決不能）状態から、証明書を取り込んで一覧を更新する。
        _service.Status = StatusWith(certificateThumbprint: ThumbprintA);
        _service.Inspections[ThumbprintA] = null;
        _store.Candidates = [Candidate(ThumbprintB, "viewer-b")];
        var cut = _ctx.Render<ViewerHttpsScreen>();
        Assert.DoesNotContain(UiText.ViewerHttpsSanTitle, cut.Markup, StringComparison.Ordinal);

        _service.Inspections[ThumbprintA] = InspectionA;
        _store.Candidates = [Candidate(ThumbprintA, "viewer-a"), Candidate(ThumbprintB, "viewer-b")];
        await ClickButtonAsync(cut, UiText.ViewerHttpsRefreshButton);

        Assert.Contains(MissingTextA, cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingSelectionAfterSave_ClearsTheSavedSummaryAndTheAfterEnableGuidance()
    {
        // 保存後案内の新 URL は検査結果から組み立てるため、選択を変えた後も残すと
        // 未保存の証明書の URL を「反映後の閲覧 URL」として見せてしまう。
        _service.Status = StatusWith(enabled: true, certificateThumbprint: ThumbprintA);
        _service.NextChangedKeys = ["Viewer:Https:Enabled"];
        var cut = _ctx.Render<ViewerHttpsScreen>();

        var savedPrefix = UiText.ViewerHttpsSavedFormat[..UiText.ViewerHttpsSavedFormat.IndexOf("{0}", StringComparison.Ordinal)];

        await ClickButtonAsync(cut, "適用する");
        Assert.Contains(savedPrefix, cut.Markup, StringComparison.Ordinal);
        Assert.Contains(UiText.ViewerHttpsAfterEnableTitle, cut.Markup, StringComparison.Ordinal);
        Assert.Contains(
            string.Format(UiText.ViewerHttpsAfterEnableNewUrlFormat, "https://yagura01.example.local:8514/"),
            cut.Markup,
            StringComparison.Ordinal);

        await SelectAsync(cut, ThumbprintB);

        Assert.DoesNotContain(savedPrefix, cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(UiText.ViewerHttpsAfterEnableTitle, cut.Markup, StringComparison.Ordinal);
        Assert.Contains(SatisfiedTextB, cut.Markup, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 操作
    // ------------------------------------------------------------------

    private static Task SelectAsync(IRenderedComponent<ViewerHttpsScreen> cut, string thumbprint) =>
        cut.InvokeAsync(() =>
            cut.FindComponent<YaguraCertificateSelectionPanel>().Instance.SelectedThumbprintChanged.InvokeAsync(thumbprint));

    /// <summary>
    /// 探索とクリックを 1 つの <c>InvokeAsync</c> に収め、その間の再描画でイベントハンドラ ID が
    /// 失効する競合を閉じる（SourceSilenceScreenEditTests と同じ理由）。
    /// </summary>
    private static Task ClickButtonAsync(IRenderedComponent<ViewerHttpsScreen> cut, string buttonText) =>
        cut.InvokeAsync(() =>
            cut.FindAll("button").Single(b => b.TextContent.Contains(buttonText, StringComparison.Ordinal))
                .Click());

    // ------------------------------------------------------------------
    // フェイク
    // ------------------------------------------------------------------

    private static ViewerHttpsStatus StatusWith(
        bool enabled = false,
        string? certificateThumbprint = null,
        string? adminHttpsCertificateThumbprint = null) =>
        new(
            Enabled: enabled,
            CertificateThumbprint: certificateThumbprint,
            ViewerPort: null,
            AdminHttpsCertificateThumbprint: adminHttpsCertificateThumbprint,
            EmailNotificationEnabled: true);

    private static CertificateCandidate Candidate(string thumbprint, string subjectCommonName) =>
        new(
            thumbprint,
            subjectCommonName,
            Issuer: $"CN={subjectCommonName}",
            NotBefore: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            NotAfter: new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero),
            IsExpired: false,
            IsPrivateKeyReadable: true);

    private sealed class SwitchableStoreReader : ICertificateStoreReader
    {
        internal IReadOnlyList<CertificateCandidate> Candidates { get; set; } = [];

        public IReadOnlyList<CertificateCandidate> ListServerAuthCertificates() => Candidates;
    }

    private sealed class ScriptedViewerHttpsAdminService : IViewerHttpsAdminService
    {
        private readonly Dictionary<string, TaskCompletionSource<ViewerHttpsSanInspection?>> _deferred = [];

        internal ViewerHttpsStatus Status { get; set; } = StatusWith();

        internal Dictionary<string, ViewerHttpsSanInspection?> Inspections { get; } = [];

        internal IReadOnlyList<string> NextChangedKeys { get; set; } = [];

        internal List<string> InspectedThumbprints { get; } = [];

        /// <summary>指定拇印の次の検査応答を、返り値の完了まで保留する。</summary>
        internal TaskCompletionSource<ViewerHttpsSanInspection?> DeferNext(string thumbprint)
        {
            var pending = new TaskCompletionSource<ViewerHttpsSanInspection?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _deferred[thumbprint] = pending;
            return pending;
        }

        public Task<ViewerHttpsStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Status);

        public Task<ViewerHttpsConfigureResult> ConfigureAsync(
            bool enabled,
            string? certificateThumbprint,
            string? operatorAddress = null,
            string? operatorScheme = null,
            string? operatorPrincipal = null,
            CancellationToken cancellationToken = default)
        {
            Status = Status with { Enabled = enabled, CertificateThumbprint = certificateThumbprint };
            var inspection = certificateThumbprint is null ? null : Inspections.GetValueOrDefault(certificateThumbprint);
            return Task.FromResult(new ViewerHttpsConfigureResult(
                NextChangedKeys,
                NextChangedKeys.Count > 0 ? ConfigurationApplyEffect.RestartRequired : ConfigurationApplyEffect.Immediate,
                inspection,
                Status));
        }

        public Task<ViewerHttpsSanInspection?> InspectCertificateAsync(
            string certificateThumbprint, CancellationToken cancellationToken = default)
        {
            InspectedThumbprints.Add(certificateThumbprint);

            if (_deferred.Remove(certificateThumbprint, out var pending))
            {
                return pending.Task;
            }

            return Task.FromResult(Inspections.GetValueOrDefault(certificateThumbprint));
        }
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
