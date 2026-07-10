using Microsoft.AspNetCore.Http;
using Yagura.Abstractions.Auditing;
using Yagura.Web.Diagnostics;

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
/// 存在自体を非 loopback クライアントへ漏らさない）。
/// </para>
/// <para>
/// <b>拒否の監査記録（M6-2。Issue #52。security.md §1 L-3b「拒否 + 監査記録」）</b>:
/// 拒否時に <see cref="IAuditRecorder"/>（実体は <c>Yagura.Host</c> が DI で結線する
/// <c>FileAuditRecorder</c>）へ 1 件記録し、<see cref="WebGuardMetrics"/> へ拒否カウンタを
/// 計上する。監査記録の書き込みは非同期だが、**応答（404）は監査記録の完了を待たずに返す
/// ことはしない**——<see cref="IAuditRecorder.RecordAsync"/> 自体が失敗しても例外を投げない
/// 契約（インターフェースの remarks 参照）のため、awaitしても要求処理を妨げない
/// （ADR-0004 決定 7）。
/// </para>
/// <para>
/// <b>「管理系パス」判定方式の限界（SEC-7・security.md §1 L-3b）</b>: 本ミドルウェアは
/// <see cref="ListenerPortGuardEndpointMetadata.Admin"/> を持つ「登録済みエンドポイント」への
/// 到達のみを監査記録・拒否カウンタの対象とする（ルート表からの機械的導出方式）。
/// 閲覧リスナへの <c>/admin/xxx</c> 等、管理系ルート表に一致しない未登録パスへの要求は
/// 通常の 404（ASP.NET Core の既定のルーティング未一致）であり、本ミドルウェアを経由しない
/// ため監査記録・カウンタの対象にならない。この覆域の限界は security.md §1 L-3b に
/// 明記されている（PR で「⚠️ オーナー確認事項」として提起する）。
/// </para>
/// </remarks>
public sealed class ListenerPortGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IReadOnlyList<int> _adminPorts;
    private readonly IAuditRecorder _auditRecorder;
    private readonly WebGuardMetrics _metrics;
    private readonly TimeProvider _timeProvider;

    public ListenerPortGuardMiddleware(
        RequestDelegate next,
        IReadOnlyList<int> adminPorts,
        IAuditRecorder auditRecorder,
        WebGuardMetrics metrics,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(adminPorts);
        ArgumentNullException.ThrowIfNull(auditRecorder);
        ArgumentNullException.ThrowIfNull(metrics);

        _next = next;
        _adminPorts = adminPorts;
        _auditRecorder = auditRecorder;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        var guard = endpoint?.Metadata.GetMetadata<ListenerPortGuardEndpointMetadata>();

        if (guard is { Kind: ListenerKind.Admin } && !_adminPorts.Contains(context.Connection.LocalPort))
        {
            // 閲覧リスナ（または将来追加され得る他ポート）経由での管理系エンドポイント到達を
            // 拒否する。存在自体を漏らさないため 404（NotFound）で応答する。
            context.Response.StatusCode = StatusCodes.Status404NotFound;

            _metrics.RecordListenerGuardRejected();

            var auditEvent = new AuditEvent(
                OccurredAt: _timeProvider.GetUtcNow(),
                Kind: AuditEventKind.ViewerListenerAdminRequestRejected,
                RemoteAddress: context.Connection.RemoteIpAddress?.ToString(),
                RemotePort: context.Connection.RemotePort,
                AttemptedPath: context.Request.Path.Value ?? string.Empty,
                ReachedListenerPort: context.Connection.LocalPort);

            // IAuditRecorder.RecordAsync は失敗しても例外を投げない契約（インターフェースの
            // remarks 参照）。await することで書き込み完了を待つが、要求処理（404 応答）は
            // 既に確定済みであり、本 await 自体が要求処理を妨げることはない
            // （ADR-0004 決定 7「監査記録の書き込み不能は要求処理を妨げない」）。
            // CancellationToken.None を渡す理由: クライアントの切断（RequestAborted）で
            // 監査記録の書き込み自体を打ち切ってはならない——応答が届くかどうかに関わらず
            // 「拒否した事実」は記録する（拒否の判断自体は既に確定済み）。
            await _auditRecorder.RecordAsync(auditEvent, CancellationToken.None).ConfigureAwait(false);

            return;
        }

        await _next(context).ConfigureAwait(false);
    }
}
