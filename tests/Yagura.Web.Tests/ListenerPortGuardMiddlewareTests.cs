using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Yagura.Storage.Auditing;
using Yagura.Web.Diagnostics;

namespace Yagura.Web.Tests;

/// <summary>
/// <see cref="ListenerPortGuardMiddleware"/> の単体テスト（M6-1・M6-2。Issue #51・#52）。
/// </summary>
/// <remarks>
/// security.md §1 L-3b の前提となる「管理系ルートが閲覧リスナ経由で絶対に実行されない」
/// 構造を検証する。<c>HttpContext.Connection.LocalPort</c> による判定が、管理リスナの
/// ポート番号と一致するときのみ管理系エンドポイントの実行を許すことを確認する。
/// M6-2 では、拒否時に監査記録・拒否カウンタが呼ばれること、監査記録の書き込み失敗が
/// 要求処理（404 応答）を妨げないことも検証する。
/// </remarks>
public sealed class ListenerPortGuardMiddlewareTests
{
    private const int AdminPort = 8515;
    private const int ViewerPort = 8514;

    [Fact]
    public async Task InvokeAsync_AdminEndpoint_ViaAdminPort_CallsNext()
    {
        var context = CreateContext(localPort: AdminPort, endpointMetadata: ListenerPortGuardEndpointMetadata.Admin);
        var nextCalled = false;
        var auditRecorder = new RecordingAuditRecorder();

        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, auditRecorder, out _);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled, "管理ポート経由の管理系エンドポイントは実行されるべき。");
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Empty(auditRecorder.RecordedEvents);
    }

    [Fact]
    public async Task InvokeAsync_AdminEndpoint_ViaViewerPort_ReturnsNotFoundAndDoesNotCallNext()
    {
        // security.md §1 L-3b の前提: 閲覧リスナ(8514)経由での管理系エンドポイント到達は
        // 実行されない(404)。
        var context = CreateContext(localPort: ViewerPort, endpointMetadata: ListenerPortGuardEndpointMetadata.Admin);
        var nextCalled = false;
        var auditRecorder = new RecordingAuditRecorder();

        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, auditRecorder, out _);

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled, "閲覧ポート経由では管理系エンドポイントを実行してはならない。");
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_AdminEndpoint_ViaViewerPort_RecordsAuditEventAndCounter()
    {
        // M6-2（Issue #52）: 拒否時に監査記録(IAuditRecorder)へ1件記録され、
        // 拒否カウンタ(WebGuardMetrics)へ計上されることを確認する。
        var context = CreateContext(
            localPort: ViewerPort,
            endpointMetadata: ListenerPortGuardEndpointMetadata.Admin,
            remoteAddress: System.Net.IPAddress.Parse("203.0.113.5"),
            remotePort: 54321,
            path: "/admin");
        var auditRecorder = new RecordingAuditRecorder();

        var middleware = CreateMiddleware(_ => Task.CompletedTask, auditRecorder, out var metrics);
        using var collector = new Microsoft.Extensions.Diagnostics.Metrics.Testing.MetricCollector<long>(
            metrics.ListenerGuardRejectedCounter);

        await middleware.InvokeAsync(context);

        var recorded = Assert.Single(auditRecorder.RecordedEvents);
        Assert.Equal(AuditEventKind.ViewerListenerAdminRequestRejected, recorded.Kind);
        Assert.Equal("203.0.113.5", recorded.RemoteAddress);
        Assert.Equal(54321, recorded.RemotePort);
        Assert.Equal("/admin", recorded.AttemptedPath);
        Assert.Equal(ViewerPort, recorded.ReachedListenerPort);

        Assert.Equal(1, collector.GetMeasurementSnapshot().Sum(m => m.Value));
    }

    [Fact]
    public async Task InvokeAsync_AuditRecorderThrows_StillReturnsNotFound()
    {
        // ADR-0004 決定 7「監査記録の書き込み不能は要求処理を妨げない」: IAuditRecorder の
        // 実装が万一例外を投げても(契約違反だが)、404 応答自体は既に確定済みであり
        // 要求処理を止めない設計であることを確認する。
        var context = CreateContext(localPort: ViewerPort, endpointMetadata: ListenerPortGuardEndpointMetadata.Admin);
        var auditRecorder = new ThrowingAuditRecorder();

        var middleware = CreateMiddleware(_ => Task.CompletedTask, auditRecorder, out _);

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

        // 例外発生前に応答コードは既に設定済みである(拒否の判断自体は監査記録より先に確定する)。
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_ViewerEndpoint_NoGuardMetadata_ViaViewerPort_CallsNext()
    {
        // 閲覧系エンドポイント(ガードメタデータなし)は閲覧ポート経由で通常どおり実行される。
        var context = CreateContext(localPort: ViewerPort, endpointMetadata: null);
        var nextCalled = false;
        var auditRecorder = new RecordingAuditRecorder();

        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, auditRecorder, out _);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Empty(auditRecorder.RecordedEvents);
    }

    [Fact]
    public async Task InvokeAsync_ViewerEndpoint_NoGuardMetadata_ViaAdminPort_CallsNext()
    {
        // 設計判断: 閲覧系ルートは管理リスナからも到達できる(ui.md §4 の不変条件は
        // 「閲覧リスナに書き込み系を置かない」であり、管理リスナ(loopback 限定)からの
        // 閲覧系ルート到達を妨げる理由はない)。
        var context = CreateContext(localPort: AdminPort, endpointMetadata: null);
        var nextCalled = false;
        var auditRecorder = new RecordingAuditRecorder();

        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, auditRecorder, out _);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_NoEndpointResolvedYet_CallsNext()
    {
        // ルーティング未確定(GetEndpoint が null を返す)の段階では判定できないため通す
        // (実際の配置順序では UseRouting の後に本ミドルウェアを置くため、この状況は
        // 通常発生しない——防御的な既定動作の確認)。
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpConnectionFeature>(new HttpConnectionFeature { LocalPort = ViewerPort });
        var nextCalled = false;
        var auditRecorder = new RecordingAuditRecorder();

        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, auditRecorder, out _);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    private static ListenerPortGuardMiddleware CreateMiddleware(
        RequestDelegate next,
        IAuditRecorder auditRecorder,
        out WebGuardMetrics metrics)
    {
        metrics = new WebGuardMetrics();
        return new ListenerPortGuardMiddleware(next, AdminPort, auditRecorder, metrics);
    }

    private static DefaultHttpContext CreateContext(
        int localPort,
        ListenerPortGuardEndpointMetadata? endpointMetadata,
        System.Net.IPAddress? remoteAddress = null,
        int remotePort = 0,
        string path = "/")
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpConnectionFeature>(new HttpConnectionFeature
        {
            LocalPort = localPort,
            RemoteIpAddress = remoteAddress,
            RemotePort = remotePort,
        });
        context.Request.Path = path;

        var metadata = endpointMetadata is null
            ? new EndpointMetadataCollection()
            : new EndpointMetadataCollection(endpointMetadata);
        var endpoint = new Endpoint(requestDelegate: null, metadata, displayName: "test-endpoint");
        context.Features.Set<IEndpointFeature>(new EndpointFeature { Endpoint = endpoint });

        return context;
    }

    private sealed class EndpointFeature : IEndpointFeature
    {
        public Endpoint? Endpoint { get; set; }
    }

    /// <summary>テスト用の <see cref="IAuditRecorder"/>: 呼び出しを記録するだけで実 I/O は行わない。</summary>
    private sealed class RecordingAuditRecorder : IAuditRecorder
    {
        public List<AuditEvent> RecordedEvents { get; } = new();

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            RecordedEvents.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// テスト用の <see cref="IAuditRecorder"/>: <see cref="IAuditRecorder.RecordAsync"/> の
    /// 「例外を投げない」契約に実装が違反した場合でも、ミドルウェア側の 404 応答は既に
    /// 確定済みであることを確認するためのフィクスチャ。
    /// </summary>
    private sealed class ThrowingAuditRecorder : IAuditRecorder
    {
        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("監査記録の書き込みに失敗した(テスト用の契約違反シミュレーション)。");
    }
}
