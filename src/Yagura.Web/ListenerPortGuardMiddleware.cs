using Microsoft.AspNetCore.Http;

namespace Yagura.Web;

/// <summary>
/// 管理系エンドポイント（<see cref="ListenerPortGuardEndpointMetadata.Admin"/> を持つもの）が
/// 管理リスナ以外のポートから実行されないことを強制するミドルウェア（M6-1。Issue #51）。
/// </summary>
/// <remarks>
/// <para>
/// <b>判定に使う値</b>: <c>HttpContext.Connection.LocalPort</c>（このリクエストが実際に
/// 接続を受け付けた TCP ソケットのローカルポート。Kestrel のトランスポート層が accept 時に
/// 設定する値であり、HTTP <c>Host</c> ヘッダと異なりクライアントが偽装できない）。
/// <see cref="ListenerPortGuardEndpointMetadata"/> のコメント参照——<c>RequireHost</c> を
/// 採らなかった理由と対をなす設計判断。
/// </para>
/// <para>
/// <b>配置</b>: <c>UseRouting</c> の後・エンドポイント実行（<c>UseEndpoints</c> 相当）の前に
/// 登録する。この位置であれば <c>HttpContext.GetEndpoint()</c> でルーティング確定後の
/// エンドポイントメタデータを参照でき、かつエンドポイントの実処理（Razor Components の
/// 描画・SignalR ハブへのアップグレード等）が始まる前に拒否できる。
/// </para>
/// <para>
/// <b>Blazor circuit（SignalR ハブ）への配慮</b>: <c>MapRazorComponents().AddInteractiveServerRenderMode()</c>
/// は <c>/_blazor</c> ハブを同じエンドポイントルート集合に自動登録する。管理系ページを
/// 将来 Interactive Server で実装する場合、当該ページと同じ集約点（<see cref="YaguraAdminExtensions.MapYaguraAdmin"/>）
/// 配下に登録される限り、本ミドルウェアの判定はハブへの接続確立要求（ネゴシエーション・
/// WebSocket アップグレード）にも同じ経路で適用される——ルーティングは接続ごとに毎回
/// 評価されるため、閲覧リスナ経由の接続が管理リスナ側のハブへ「逃げる」余地はない。
/// </para>
/// <para>
/// <b>拒否時の応答</b>: 404 Not Found を返す（存在しないルートと区別しない——管理系パスの
/// 存在自体を非 loopback クライアントへ漏らさない）。拒否の監査記録（security.md §1 L-3b
/// 「拒否 + 監査記録」）は後続 Issue #52 のスコープであり、本ミドルウェアは「実行されない」
/// 構造のみを提供する。
/// </para>
/// </remarks>
public sealed class ListenerPortGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly int _adminPort;

    public ListenerPortGuardMiddleware(RequestDelegate next, int adminPort)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
        _adminPort = adminPort;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        var guard = endpoint?.Metadata.GetMetadata<ListenerPortGuardEndpointMetadata>();

        if (guard is { Kind: ListenerKind.Admin } && context.Connection.LocalPort != _adminPort)
        {
            // 閲覧リスナ（または将来追加され得る他ポート）経由での管理系エンドポイント到達を
            // 拒否する。存在自体を漏らさないため 404（NotFound）で応答する。
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await _next(context).ConfigureAwait(false);
    }
}
