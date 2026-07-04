using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Yagura.Web.Components;

namespace Yagura.Web;

/// <summary>
/// Yagura.Web（閲覧リスナ）を Host から組み込むための拡張メソッド。
/// </summary>
/// <remarks>
/// <b>ルート登録は本クラスの <see cref="MapYaguraWebViewer"/> の 1 箇所に集約する</b>。
/// 閲覧リスナは書き込みエンドポイントを持たず（architecture.md §6・ui.md §4 の不変条件）、
/// 将来 security.md L-5 で導入予定の「全ルート許可リスト」アーキテクチャテストは、
/// この集約点を検査対象にする想定である。新しい閲覧系ページを追加する場合も、
/// ルート登録は必ずこのメソッド経由で行うこと（Host 側で個別に <c>MapGet</c> 等を
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

        return services;
    }

    /// <summary>
    /// 閲覧ページのルートを 1 箇所に集約してマップする。書き込み系エンドポイントは追加しない。
    /// </summary>
    public static IEndpointRouteBuilder MapYaguraWebViewer(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // AddInteractiveServerRenderMode は Interactive Server の circuit エンドポイント
        // （SignalR。既定パス /_blazor）を有効化する。circuit 数の上限・失効の反映等の
        // 統治は security.md（ADR-0004 決定 6）の管轄で M6/M8 スコープ。
        endpoints.MapRazorComponents<YaguraWebApp>()
            .AddInteractiveServerRenderMode();

        return endpoints;
    }
}
