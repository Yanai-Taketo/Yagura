using Microsoft.AspNetCore.Components;
using Yagura.Web.Circuits;

namespace Yagura.Web.Components;

/// <summary>
/// circuit 統治の受け手コンポーネント（M8-4。Issue #71）。ルートコンポーネント
/// （<c>YaguraWebRoot</c>）に常駐し、描画は行わない。
/// </summary>
/// <remarks>
/// <para>
/// 管理者による個別切断（security.md §2.2）・無操作回収（SEC-8）の要求
/// （<see cref="YaguraCircuitContext.TerminationRequested"/>）を受け、circuit を要しない
/// 接続終了の案内ページ（<see cref="CircuitEndedPagePath"/>）への強制の全ページ遷移
/// （<c>forceLoad</c>）を実行する。全ページ遷移によりブラウザは circuit を正常終了させ、
/// 枠が解放される——サーバ側から circuit を直接終了させる公開 API は存在しないため
/// （<see cref="YaguraCircuitContext"/> の remarks 参照）、この協調方式を採る。
/// </para>
/// <para>
/// 本コンポーネントは閲覧・管理の両 circuit に常駐するが、注入するのは
/// <see cref="YaguraCircuitContext"/>（書き込み系サービスではない）と
/// <see cref="NavigationManager"/> のみであり、閲覧リスナの参照分離
/// （<c>ViewerComponentReferenceIsolationTests</c>）に抵触しない。
/// </para>
/// </remarks>
public sealed class CircuitGovernor : ComponentBase, IDisposable
{
    /// <summary>接続終了の案内ページ（circuit を要しない静的応答。MapYaguraWebViewer が登録する）。</summary>
    public const string CircuitEndedPagePath = "/circuit-ended";

    /// <summary>案内ページの理由クエリパラメータ名（<see cref="CircuitTerminationReasons"/> の値が入る）。</summary>
    public const string ReasonQueryParameter = "reason";

    [Inject]
    private YaguraCircuitContext CircuitContext { get; set; } = default!;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    protected override void OnInitialized()
    {
        // prerender（HTTP 要求スコープ）でも購読自体は無害——そのスコープの context に切断要求が
        // 来ることはない。対話的描画（circuit スコープ）での購読が本命。
        CircuitContext.TerminationRequested += OnTerminationRequestedAsync;
    }

    private Task OnTerminationRequestedAsync(string reason) =>
        InvokeAsync(() => Navigation.NavigateTo(
            $"{CircuitEndedPagePath}?{ReasonQueryParameter}={Uri.EscapeDataString(reason)}",
            forceLoad: true));

    public void Dispose()
    {
        CircuitContext.TerminationRequested -= OnTerminationRequestedAsync;
    }
}
