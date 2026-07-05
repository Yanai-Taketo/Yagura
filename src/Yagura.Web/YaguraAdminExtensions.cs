using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Yagura.Web;

/// <summary>
/// Yagura.Web（管理リスナ）を Host から組み込むための拡張メソッド（M6-1。Issue #51）。
/// </summary>
/// <remarks>
/// <b>ルート登録は本クラスの <see cref="MapYaguraAdmin"/> の 1 箇所に集約する</b>
/// （<see cref="YaguraWebViewerExtensions.MapYaguraWebViewer"/> と対の集約点）。
/// 管理系ルート（設定変更・昇格・circuit 管理等。ui.md §4 の「設定（ウィザード群）」画面）は
/// すべてここへ登録する。現時点（M6-1）では管理画面は未実装のため、プレースホルダの
/// 最小構成のみ提供する——将来の設定ウィザード（configuration.md §3〜§7）がここに乗る。
/// </remarks>
public static class YaguraAdminExtensions
{
    /// <summary>
    /// 管理リスナ専用のルートを 1 箇所に集約してマップする。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>登録した全エンドポイントは <see cref="ListenerPortGuardMiddleware"/> による
    /// ポートゲートの対象になる</b>: <see cref="ListenerPortGuardEndpointMetadata"/> を
    /// メタデータとして付与し、実際に接続を受けたローカルポートが管理リスナのポートと
    /// 一致しない場合は 404 を返す（閲覧リスナ（8514）経由では管理系ルートへ絶対に
    /// 到達できない——security.md §1 L-3b の前提となる構造。Program.cs 参照）。
    /// </para>
    /// <para>
    /// <b>プレースホルダの内容（M6-1 時点）</b>: 管理画面の実装は本 Issue のスコープ外の
    /// ため、疎通確認用の最小 GET エンドポイント 1 本のみを置く。将来の設定ウィザード
    /// （configuration.md §3〜§7・ui.md §4「設定（ウィザード群）」画面）はこの集約点の
    /// 配下に追加していく。
    /// </para>
    /// </remarks>
    public static IEndpointRouteBuilder MapYaguraAdmin(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/admin", () => Results.Text(
                "Yagura 管理画面はまだ実装されていません(M6-1 時点のプレースホルダ)。",
                "text/plain; charset=utf-8"))
            .WithMetadata(ListenerPortGuardEndpointMetadata.Admin);

        return endpoints;
    }

    /// <summary>
    /// <see cref="ListenerPortGuardMiddleware"/> をパイプラインに組み込む。
    /// </summary>
    /// <param name="app">アプリケーションビルダー。</param>
    /// <param name="adminPort">管理リスナの実ポート番号（<c>ResolvedYaguraConfiguration.AdminHttpPort</c>）。</param>
    /// <remarks>
    /// <para>
    /// <c>UseRouting</c>（または暗黙にルーティングを組み込む <c>MapRazorComponents</c> 等）の
    /// 後、エンドポイントの実処理が始まる前に呼び出すこと——<c>HttpContext.GetEndpoint()</c>
    /// がルーティング確定後のメタデータを返すために必要な順序。Program.cs 参照。
    /// </para>
    /// <para>
    /// <b>DI 前提（M6-2）</b>: <see cref="ListenerPortGuardMiddleware"/> は明示引数
    /// <c>adminPort</c> 以外に <c>Yagura.Storage.Auditing.IAuditRecorder</c> と
    /// <c>Yagura.Web.Diagnostics.WebGuardMetrics</c> をコンストラクタで要求する。
    /// <c>UseMiddleware</c> は明示引数以外のコンストラクタパラメータを DI コンテナから
    /// 解決するため、呼び出し前に両方のサービスを <c>IServiceCollection</c> へ登録しておくこと
    /// （<see cref="IAuditRecorder"/> の実体は <c>Yagura.Host</c> 側が結線する。
    /// <c>WebGuardMetrics</c> は本クラスと対称のシングルトンとして Program.cs が登録する）。
    /// </para>
    /// </remarks>
    public static IApplicationBuilder UseYaguraListenerPortGuard(this IApplicationBuilder app, int adminPort)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<ListenerPortGuardMiddleware>(adminPort);
    }
}
