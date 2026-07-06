using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MudBlazor.Services;
using Yagura.Web.Circuits;
using Yagura.Web.Components;
using Yagura.Web.Components.Common;

namespace Yagura.Web;

/// <summary>
/// Yagura.Web（閲覧リスナ）を Host から組み込むための拡張メソッド。
/// </summary>
/// <remarks>
/// <b>ルート登録は本クラスの <see cref="MapYaguraWebViewer"/> の 1 箇所に集約する</b>。
/// 閲覧リスナは書き込みエンドポイントを持たず（architecture.md §6・ui.md §4 の不変条件）、
/// security.md §1 L-5 の「全ルート許可リスト」アーキテクチャテスト
/// （<c>ViewerEndpointAllowlistTests</c>）がこの集約点を検査対象にする。新しい閲覧系ページを
/// 追加する場合も、ルート登録は必ずこのメソッド経由で行うこと（Host 側で個別に <c>MapGet</c> 等を
/// 追加しない）。
/// </remarks>
public static class YaguraWebViewerExtensions
{
    /// <summary>
    /// 閲覧ページ（Razor Components）が要求する DI サービスを登録する。
    /// </summary>
    public static IServiceCollection AddYaguraWebViewer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 全画面 Interactive Server 単一モード（ADR-0003 決定 1）。方式を混在させない
        // ——静的 SSR との 2 方式を画面ごとに使い分ける規約は持たない、が同 ADR の
        // 採用理由の 1 つ（選択肢 (b) の却下理由）。
        // prerender は既定の有効のままとする（初回応答の HTML に描画結果が含まれ、
        // circuit 確立前でも内容が見える。E2E smoke はこの prerender 出力を検証する）。
        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // MudBlazor（M8-1。ADR-0003 決定 3）。AddMudServices が登録するのは
        // ダイアログ・トースト・ポップオーバー等の DI サービスのみで、
        // エンドポイントは追加しない（ルート登録は下の MapYaguraWebViewer に集約されたまま）。
        services.AddMudServices();

        // 通知（トースト）の共通経路（M8-2。ui.md §3.1 通知規約）。ページは ISnackbar を
        // 直接使わず本経路を使う（低レベル API をページに散乱させない——ui.md §1）。
        // ISnackbar（AddMudServices が Scoped 登録）に合わせて Scoped。
        services.AddScoped<Components.Common.IYaguraNotifier, Components.Common.YaguraSnackbarNotifier>();

        // ---- circuit 統治（M8-4。Issue #71。security.md §2）----
        //
        // - CircuitRegistry: プロセス内の全 circuit の台帳（一覧・上限・回収の共通基盤）
        // - YaguraCircuitContext / YaguraCircuitHandler: circuit スコープの帰属・切断伝達。
        //   CircuitHandler は circuit ごとの DI スコープから解決されるため、Scoped 登録で
        //   circuit 1 本 = 1 インスタンスの対応になる
        // - IHttpContextAccessor: circuit 確立時の接続（WebSocket 要求）の実ローカルポートから
        //   リスナ帰属を判定するために使う（YaguraCircuitHandler のコメント参照——取得不能時は
        //   帰属不明 = 閲覧相当の安全側へ倒す）
        // - CircuitIdleReclaimService: 無操作 circuit の定期回収（SEC-8 仮値）
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<CircuitRegistry>();
        services.AddScoped<YaguraCircuitContext>();
        services.AddScoped<CircuitHandler, YaguraCircuitHandler>();
        services.AddHostedService<CircuitIdleReclaimService>();

        // ---- 送信元の逆引きホスト名（ADR-0007。ui.md §4）----
        //
        // - ReverseDnsDisplayOptions は Host が設定読み込み結果（Viewer:ReverseDns:Enabled）から
        //   構築して先に登録する。未登録のホスト（テストハーネス等）では TryAdd の既定 =
        //   「無効」に倒す——構成を持たない実行形態が外向き DNS クエリを発しないため（決定 4 の
        //   縮小側と同じ向き）
        // - 解決 API の呼び出しは IReverseDnsLookup の実装 1 点に集約する（オフ時・対象帯域外で
        //   下位 API へ到達しないことの単体テスト固定——security.md §1.1）
        services.TryAddSingleton(new ReverseDns.ReverseDnsDisplayOptions(Enabled: false));
        services.TryAddSingleton<ReverseDns.IReverseDnsLookup, ReverseDns.SystemDnsReverseLookup>();
        services.AddSingleton<Diagnostics.ReverseDnsMetrics>();
        services.AddSingleton<ReverseDns.IReverseDnsResolver, ReverseDns.ReverseDnsResolver>();

        return services;
    }

    /// <summary>
    /// 閲覧ページのルートを 1 箇所に集約してマップする。書き込み系エンドポイントは追加しない。
    /// </summary>
    /// <param name="endpoints">エンドポイントビルダー。</param>
    /// <param name="staticAssetsManifestPath">
    /// 静的アセットの endpoints マニフェストのパス（<c>AppContext.BaseDirectory</c> からの相対）。
    /// <c>null</c> の場合は既定解決（<c>{ApplicationName}.staticwebassets.endpoints.json</c>。
    /// Yagura.Host からの呼び出しはこちらで、build/publish の両出力に存在することを実機確認済み）。
    /// テストハーネスのようにエントリアセンブリがアプリ本体でないホストは、テスト出力に生成される
    /// RCL 単位のマニフェスト <c>Yagura.Web.staticwebassets.endpoints.json</c> を明示指定する
    /// （RCL 単位のマニフェストは publish 出力には含まれないことを実機確認済みのため、
    /// 本番経路の既定にはしない）。
    /// </param>
    /// <returns>
    /// Razor Components エンドポイントの規約ビルダー。<b>呼び出し側は必ずこれを
    /// <see cref="YaguraAdminExtensions.MapYaguraAdmin"/> へ渡すこと</b>——管理画面
    /// （<c>Yagura.Web.Administration.Screens</c> 配下の <c>@page</c> ルート）への
    /// Admin メタデータ付与（リスナ帰属の機械的導出）は MapYaguraAdmin が担う（M8-4）。
    /// </returns>
    public static RazorComponentsEndpointConventionBuilder MapYaguraWebViewer(
        this IEndpointRouteBuilder endpoints,
        string? staticAssetsManifestPath = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // MudBlazor の同梱静的アセット（_content/MudBlazor/ 配下）の配信（M8-1）。
        // UseStaticFiles（ミドルウェア方式）ではなく MapStaticAssets（エンドポイント方式）を
        // 採る理由: 配信経路が EndpointDataSource の列挙に現れ、L-5 の全ルート許可リスト
        // 突合（ViewerEndpointAllowlistTests）の機械検証にそのまま乗るため（security.md §1 L-5）。
        // 静的アセットは GET/HEAD のみで、閲覧リスナの「書き込みエンドポイントを持たない」
        // 不変条件（ui.md §4）を破らない。
        endpoints.MapStaticAssets(staticAssetsManifestPath);

        // 接続終了の案内ページ（M8-4。security.md §2.2 の個別切断・SEC-8 の無操作回収の着地先）。
        // circuit を要しない静的応答であり、読み取り専用（GET のみ）——閲覧リスナの
        // 「書き込みエンドポイントを持たない」不変条件を破らない。L-5 許可リストに登録済み。
        endpoints.MapGet(CircuitGovernor.CircuitEndedPagePath, (HttpContext context) =>
        {
            var reason = context.Request.Query[CircuitGovernor.ReasonQueryParameter].ToString();
            var body = reason == CircuitTerminationReasons.IdleReclaimed
                ? UiText.CircuitEndedByIdleBody
                : UiText.CircuitEndedByAdministratorBody;

            var html =
                "<!DOCTYPE html><html lang=\"ja\"><head><meta charset=\"utf-8\" />" +
                $"<title>{UiText.CircuitEndedTitle}</title></head><body>" +
                $"<h1>{UiText.CircuitEndedTitle}</h1>" +
                $"<p>{body}</p>" +
                $"<p>{UiText.CircuitEndedReloadHint}</p>" +
                "</body></html>";

            return Results.Content(html, "text/html; charset=utf-8");
        });

        // AddInteractiveServerRenderMode は Interactive Server の circuit エンドポイント
        // （SignalR。既定パス /_blazor）を有効化する。circuit 数の上限・origin 検証・無操作回収の
        // 統治は M8-4 で実装済み（CircuitGuardMiddleware・CircuitRegistry。security.md §2）。
        return endpoints.MapRazorComponents<YaguraWebApp>()
            .AddInteractiveServerRenderMode();
    }
}
