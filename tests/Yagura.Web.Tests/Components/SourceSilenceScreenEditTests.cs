using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Observability;
using Yagura.Web.Administration.Screens;
using Yagura.Web.Circuits;
using Yagura.Web.Components.Common;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// ウォッチリスト設定画面の「閾値省略の保持」（ADR-0018 決定 4。Issue #383）の検証。
/// </summary>
/// <remarks>
/// 手編集で閾値を省略した既存エントリは、画面から表示名だけを直しても「省略のまま」で
/// 書き戻せなければならない。従来は編集時に既定値（1440 分）がフォームへ展開され、更新で
/// 明示指定として書き戻り、変更なしのつもりが監査 2023 に載っていた（Issue #383）。
/// </remarks>
public sealed class SourceSilenceScreenEditTests : IAsyncLifetime
{
    private readonly BunitContext _ctx = new();
    private readonly RecordingAdminService _service = new();

    public SourceSilenceScreenEditTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();
        _ctx.Services.AddSingleton<ISourceSilenceAdminService>(_service);
        _ctx.Services.AddSingleton(new YaguraCircuitContext());
        _ctx.Services.AddSingleton(new YaguraCircuitAuthenticationStateProvider());
        _ctx.Services.AddScoped<IYaguraNotifier, YaguraSnackbarNotifier>();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    private static SourceSilenceAdminStatus StatusWith(params SourceSilenceWatchlistItem[] watchlist) =>
        new(
            DefaultThresholdMinutes: 1440,
            MaxWatchlistEntries: 1000,
            MinThresholdMinutes: 10,
            MaxThresholdMinutes: 43200,
            Watchlist: watchlist,
            RuntimeStates: []);

    [Fact]
    public async Task EditingLabelOnly_KeepsTheOmittedThreshold_InsteadOfMaterializingTheDefault()
    {
        // 閾値を省略した既存エントリ（ThresholdMinutes = null）。
        _service.Status = StatusWith(new SourceSilenceWatchlistItem("192.0.2.10", "旧名", ThresholdMinutes: null));
        var cut = _ctx.Render<SourceSilenceScreen>();

        // 「編集」→ 表示名だけ変更 →「更新」→「適用する」。
        cut.FindAll("button").Single(b => b.TextContent.Contains(UiText.SourceSilenceEditButton))
            .Click();

        // 閾値欄は空（省略の保持）。ラベルだけ書き換える。
        var labelInput = cut.FindAll("input")
            .First(i => i.GetAttribute("value") == "旧名");
        await labelInput.ChangeAsync(new ChangeEventArgs { Value = "新名" });

        cut.FindAll("button").Single(b => b.TextContent.Contains(UiText.SourceSilenceUpdateButton))
            .Click();
        await cut.FindAll("button").Single(b => b.TextContent.Contains(UiText.SourceSilenceApplyButton))
            .ClickAsync(new MouseEventArgs());

        var saved = Assert.Single(_service.LastSaved!.Watchlist);
        Assert.Equal("192.0.2.10", saved.Address);
        Assert.Equal("新名", saved.Label);
        Assert.Null(saved.ThresholdMinutes); // 省略のまま——既定値の明示指定に変わっていない
    }

    private sealed class RecordingAdminService : ISourceSilenceAdminService
    {
        internal SourceSilenceAdminStatus Status { get; set; } =
            new(1440, 1000, 10, 43200, [], []);

        internal SourceSilenceSettings? LastSaved { get; private set; }

        public Task<SourceSilenceAdminStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Status);

        public Task<IReadOnlyList<SourceSilenceCandidate>> GetCandidatesAsync(
            int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SourceSilenceCandidate>>([]);

        public Task<SourceSilenceConfigureResult> ConfigureAsync(
            SourceSilenceSettings settings,
            string? operatorAddress = null,
            string? operatorScheme = null,
            string? operatorPrincipal = null,
            CancellationToken cancellationToken = default)
        {
            LastSaved = settings;
            return Task.FromResult(new SourceSilenceConfigureResult([], [], ["192.0.2.10"], Status));
        }
    }
}
