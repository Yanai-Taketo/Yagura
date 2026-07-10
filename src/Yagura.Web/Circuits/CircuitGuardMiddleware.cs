using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Yagura.Abstractions.Auditing;
using Yagura.Web.Administration;
using Yagura.Web.Components.Common;
using Yagura.Web.Diagnostics;

namespace Yagura.Web.Circuits;

/// <summary>
/// circuit 確立経路のガード（M8-4。Issue #71）: origin 検証（security.md §2.1）と
/// circuit 数上限（同 §2.2。SEC-1 仮値）。
/// </summary>
/// <remarks>
/// <para>
/// <b>origin 検証（§2.1。v0.1: 完動）</b>: <c>/_blazor</c> 配下（circuit の確立・維持経路）への
/// 要求に <c>Origin</c> ヘッダが付いており、それが要求先（スキーム + ホスト + ポート）と
/// 一致しない場合、403 で拒否する。ブラウザは cross-site のスクリプトから発する WebSocket /
/// fetch に本物の <c>Origin</c> を強制付与し、スクリプトからは偽装できないため、悪意のある
/// サイトからの circuit 確立（cross-site WebSocket hijacking）をこの突合で常時拒否できる。
/// <c>Origin</c> ヘッダを送らないクライアント（ブラウザ外のツール等）は拒否しない——
/// この検証の目的は「第三者サイトに仕込まれたスクリプトが閲覧者のブラウザを踏み台にする」
/// 経路の遮断であり、ブラウザ外クライアントはその踏み台にならない。拒否は計測し
/// （§2.1「拒否は計測する」）、監査記録（3000 番台 = ID 3002）に残す（§4.1「origin 検証拒否」）。
/// </para>
/// <para>
/// <b>circuit 数上限（§2.2。SEC-1 仮値）</b>: 新規の画面表示（Razor Components ページへの
/// GET——エンドポイントの <see cref="ComponentTypeMetadata"/> で機械判定）の時点で、要求が
/// 到達したリスナの circuit 数が上限に達している場合、Blazor アプリの代わりに静的な案内
/// （circuit を要しないページ。現在の閲覧者数・上限値と管理者への連絡による解放の導線を含む
/// ——§2.2 の要件）を返す。<b><c>/_blazor</c>（negotiate を含む）は上限判定の対象にしない</b>:
/// 確立済み circuit の再接続も同じ経路を通るため、そこで塞ぐと「既存を守り、新規を拒否する」
/// （§2.2）の既存側を壊す。ページ表示を塞げば新規 circuit の発生源は絶たれる——ページを
/// 経由せず negotiate を直接叩く経路は残るが、その経路で確立された circuit も台帳・回収
/// （SEC-8）の統治下にあり、v0.1 の骨格ではこの覆域とする。
/// </para>
/// <para>
/// <b>配置</b>: ルーティング確定後（<see cref="ComponentTypeMetadata"/> の参照に必要）・
/// エンドポイント実行前。<c>ListenerPortGuardMiddleware</c> の直後に置く（管理系の 404 判定が
/// 先——存在を漏らさない応答が上限案内より優先）。
/// </para>
/// </remarks>
public sealed class CircuitGuardMiddleware
{
    private static readonly PathString BlazorPathPrefix = new("/_blazor");

    private readonly RequestDelegate _next;
    private readonly CircuitRegistry _registry;
    private readonly YaguraAdminListenerPort _adminPort;
    private readonly IAuditRecorder _auditRecorder;
    private readonly WebGuardMetrics _metrics;
    private readonly TimeProvider _timeProvider;

    public CircuitGuardMiddleware(
        RequestDelegate next,
        CircuitRegistry registry,
        YaguraAdminListenerPort adminPort,
        IAuditRecorder auditRecorder,
        WebGuardMetrics metrics,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(adminPort);
        ArgumentNullException.ThrowIfNull(auditRecorder);
        ArgumentNullException.ThrowIfNull(metrics);

        _next = next;
        _registry = registry;
        _adminPort = adminPort;
        _auditRecorder = auditRecorder;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // --- origin 検証（security.md §2.1）---
        if (context.Request.Path.StartsWithSegments(BlazorPathPrefix))
        {
            var origin = context.Request.Headers.Origin;
            if (!StringValues.IsNullOrEmpty(origin) && !IsSameOrigin(origin.ToString(), context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;

                _metrics.RecordCircuitOriginRejected();

                // Origin 値は秘密情報ではなく、初動解析（どのサイトが踏み台にされたか）の
                // 手がかりそのものであるため Detail に残す（security.md §4.4 の「初動解析の
                // 手がかりを失わない」と同じ思想）。
                await _auditRecorder.RecordAsync(
                    new AuditEvent(
                        OccurredAt: _timeProvider.GetUtcNow(),
                        Kind: AuditEventKind.CircuitOriginRejected,
                        RemoteAddress: context.Connection.RemoteIpAddress?.ToString(),
                        RemotePort: context.Connection.RemotePort,
                        AttemptedPath: context.Request.Path.Value,
                        ReachedListenerPort: context.Connection.LocalPort,
                        Detail: $"origin={origin}"),
                    CancellationToken.None).ConfigureAwait(false);

                return;
            }
        }

        // --- circuit 数上限（security.md §2.2。SEC-1 仮値）---
        if (HttpMethods.IsGet(context.Request.Method) &&
            context.GetEndpoint()?.Metadata.GetMetadata<ComponentTypeMetadata>() is not null)
        {
            var isAdminListener = _adminPort.Contains(context.Connection.LocalPort);
            var limit = isAdminListener
                ? CircuitGovernanceDefaults.AdminCircuitLimit
                : CircuitGovernanceDefaults.ViewerCircuitLimit;
            var current = _registry.Count(isAdminListener);

            if (current >= limit)
            {
                // 「新規を拒否する」の応答は circuit を要しない静的な案内（§2.2）。
                // 上限到達は一時的な状態であり 503（Service Unavailable）が意味的に一致する。
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentType = "text/html; charset=utf-8";

                _metrics.RecordCircuitLimitRejected();

                await context.Response.WriteAsync(
                    BuildLimitNoticeHtml(current, limit),
                    context.RequestAborted).ConfigureAwait(false);

                return;
            }
        }

        await _next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>Origin</c> ヘッダが要求先と同一オリジンかを判定する（判定部を分離して単体テスト可能に
    /// する）。<c>Origin: null</c>（不透明オリジン——サンドボックス化された iframe 等）は
    /// 同一サイトと確認できないため拒否側に倒す。
    /// </summary>
    internal static bool IsSameOrigin(string originHeader, HttpRequest request)
    {
        if (!Uri.TryCreate(originHeader, UriKind.Absolute, out var origin))
        {
            return false;
        }

        if (!string.Equals(origin.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var requestHost = request.Host;
        if (!string.Equals(origin.Host, requestHost.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Host ヘッダにポートがない場合はスキーム既定ポート（http=80/https=443）。
        // Uri.Port も既定ポートを補完するため、双方を実効ポートで比較する。
        var requestPort = requestHost.Port ?? (string.Equals(request.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
        return origin.Port == requestPort;
    }

    /// <summary>
    /// 上限到達の案内ページ（security.md §2.2: 現在の閲覧者数・上限値と、管理者への連絡による
    /// 解放の導線を含める）。文言は UiText（ui.md §7 の文言カタログ）に集約する。
    /// </summary>
    private static string BuildLimitNoticeHtml(int current, int limit) =>
        "<!DOCTYPE html><html lang=\"ja\"><head><meta charset=\"utf-8\" />" +
        $"<title>{UiText.CircuitLimitNoticeTitle}</title></head><body>" +
        $"<h1>{UiText.CircuitLimitNoticeTitle}</h1>" +
        $"<p>{UiText.FormatCircuitLimitNoticeBody(current, limit)}</p>" +
        $"<p>{UiText.CircuitLimitNoticeContactHint}</p>" +
        "</body></html>";
}
