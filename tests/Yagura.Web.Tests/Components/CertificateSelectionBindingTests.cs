using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Yagura.Abstractions.Administration;
using Yagura.Web.Administration.Screens;
using Yagura.Web.Circuits;
using Yagura.Web.Components.Common;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// 証明書選択パネルを持つ 3 画面（TLS 受信 / 管理リモート HTTPS / 閲覧 HTTPS）が、
/// 列挙に成功したときに**候補一覧を描画する**ことの回帰検証。
/// </summary>
/// <remarks>
/// <para>
/// <b>この検査が要る理由</b>: 3 画面はいずれも
/// <c>CertificateListError="_certificateListError"</c> と書かれており、**string 型の
/// コンポーネントパラメータに `@` を付け忘れると Razor はリテラル文字列として束縛する**。
/// 結果、パネルは常に「列挙に失敗した」分岐へ入り、**証明書候補が 1 つも描画されない**
/// （画面にはプレースホルダ名 <c>_certificateListError</c> がそのまま表示される）。
/// </para>
/// <para>
/// <b>コンパイルでは捕まらない</b>: 同じ書き方でも <c>Candidates="_candidates"</c>
/// （<c>IReadOnlyList</c>）や <c>IsDarkMode="_isDarkMode"</c>（<c>bool</c>）は式として
/// 解釈され正しく動く。**string 型のパラメータだけが黙ってリテラルになる**ため、
/// 型検査もアナライザも素通りする。実機検証（2026-08-08 の lab）で初めて発覚した。
/// </para>
/// <para>
/// したがって固定するのは「束縛の書き方」ではなく<b>結果として候補が描画されること</b>と
/// <b>失敗文言が出ないこと</b>である——書き方を検査すると同じ罠の別形を見逃す。
/// </para>
/// </remarks>
public sealed class CertificateSelectionBindingTests : IAsyncLifetime
{
    private const string Thumbprint = "AABBCCDDEEFF00112233445566778899AABBCCDD";
    private const string SubjectCommonName = "yagura-lab.example.local";

    private readonly BunitContext _ctx = new();

    public CertificateSelectionBindingTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();
        _ctx.Services.AddSingleton<ICertificateStoreReader>(new SingleCandidateStoreReader());
        _ctx.Services.AddSingleton(new YaguraCircuitContext());
        _ctx.Services.AddSingleton(new YaguraCircuitAuthenticationStateProvider());
        _ctx.Services.AddScoped<IYaguraNotifier, YaguraSnackbarNotifier>();
        _ctx.Services.AddSingleton<IConfigurationReloadService>(new NoopReloadService());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor の一部サービスは IAsyncDisposable のみを実装するため非同期に破棄する
    // （IngestionTlsScreenReloadTests と同じ理由）。
    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    [Fact]
    public void IngestionTlsScreen_WithCertificates_RendersCandidatesInsteadOfEnumerationFailure()
    {
        _ctx.Services.AddSingleton<IIngestionTlsAdminService>(new StubIngestionTlsAdminService());

        var markup = _ctx.Render<IngestionTlsScreen>().Markup;

        AssertCandidatesRendered(markup, UiText.IngestionTlsEnumerationFailedFormat);
    }

    [Fact]
    public void AdminRemoteAccessScreen_WithCertificates_RendersCandidatesInsteadOfEnumerationFailure()
    {
        _ctx.Services.AddSingleton<IAdminRemoteAccessAdminService>(new StubAdminRemoteAccessAdminService());

        var markup = _ctx.Render<AdminRemoteAccessScreen>().Markup;

        AssertCandidatesRendered(markup, UiText.AdminRemoteAccessEnumerationFailedFormat);
    }

    [Fact]
    public void ViewerHttpsScreen_WithCertificates_RendersCandidatesInsteadOfEnumerationFailure()
    {
        _ctx.Services.AddSingleton<IViewerHttpsAdminService>(new StubViewerHttpsAdminService());

        var markup = _ctx.Render<ViewerHttpsScreen>().Markup;

        AssertCandidatesRendered(markup, UiText.ViewerHttpsEnumerationFailedFormat);
    }

    private static void AssertCandidatesRendered(string markup, string enumerationFailedFormat)
    {
        // ①候補が実際に出ていること（列挙成功の分岐へ入っている）。
        Assert.Contains(SubjectCommonName, markup, StringComparison.Ordinal);

        // ②「列挙に失敗した」表示が出ていないこと。書式の定型部分（{0} の前）で照合する。
        var failurePrefix = enumerationFailedFormat[..enumerationFailedFormat.IndexOf("{0}", StringComparison.Ordinal)];
        Assert.DoesNotContain(failurePrefix, markup, StringComparison.Ordinal);

        // ③フィールド名がそのまま画面へ出ていないこと（リテラル束縛の直接の症状）。
        Assert.DoesNotContain("_certificateListError", markup, StringComparison.Ordinal);
    }

    private sealed class SingleCandidateStoreReader : ICertificateStoreReader
    {
        public IReadOnlyList<CertificateCandidate> ListServerAuthCertificates() =>
        [
            new CertificateCandidate(
                Thumbprint,
                SubjectCommonName,
                Issuer: "CN=yagura-lab.example.local",
                NotBefore: DateTimeOffset.UtcNow.AddDays(-1),
                NotAfter: DateTimeOffset.UtcNow.AddYears(1),
                IsExpired: false,
                IsPrivateKeyReadable: true),
        ];
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

    // 保存系は本検査の対象外（初期表示の描画だけを見る）ため、呼ばれたら気づけるよう落とす。
    private sealed class StubIngestionTlsAdminService : IIngestionTlsAdminService
    {
        public Task<IngestionTlsStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new IngestionTlsStatus(Enabled: false, CertificateThumbprint: null, Port: "6514"));

        public Task<IngestionTlsConfigureResult> ConfigureAsync(
            bool enabled, string? certificateThumbprint, string? port,
            string? operatorAddress = null, string? operatorScheme = null, string? operatorPrincipal = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubAdminRemoteAccessAdminService : IAdminRemoteAccessAdminService
    {
        public Task<AdminRemoteAccessStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AdminRemoteAccessStatus(
                RemoteBindingEnabled: false, HttpsEnabled: false, CertificateThumbprint: null,
                HttpsPort: "8516", WindowsAuthEnabled: false, AppAuthEnabled: false));

        public Task<AdminRemoteAccessConfigureResult> ConfigureAsync(
            bool remoteBindingEnabled, bool httpsEnabled, string? certificateThumbprint, string? httpsPort,
            string? operatorAddress = null, string? operatorScheme = null, string? operatorPrincipal = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubViewerHttpsAdminService : IViewerHttpsAdminService
    {
        public Task<ViewerHttpsStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ViewerHttpsStatus(
                Enabled: false, CertificateThumbprint: null, ViewerPort: "8514",
                AdminHttpsCertificateThumbprint: null, EmailNotificationEnabled: false));

        public Task<ViewerHttpsConfigureResult> ConfigureAsync(
            bool enabled, string? certificateThumbprint,
            string? operatorAddress = null, string? operatorScheme = null, string? operatorPrincipal = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ViewerHttpsSanInspection?> InspectCertificateAsync(
            string certificateThumbprint, CancellationToken cancellationToken = default) =>
            Task.FromResult<ViewerHttpsSanInspection?>(null);
    }
}
