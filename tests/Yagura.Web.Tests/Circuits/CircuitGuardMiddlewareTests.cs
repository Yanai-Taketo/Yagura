using System.Linq;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Yagura.Abstractions.Auditing;
using Yagura.Web.Administration;
using Yagura.Web.Circuits;
using Yagura.Web.Diagnostics;

namespace Yagura.Web.Tests.Circuits;

/// <summary>
/// <see cref="CircuitGuardMiddleware"/> の単体テスト（M8-4。Issue #71。
/// security.md §2.1 origin 検証・§2.2 circuit 数上限（SEC-1 仮値））。
/// </summary>
public sealed class CircuitGuardMiddlewareTests
{
    private const int AdminPort = 8515;
    private const int ViewerPort = 8514;

    // ---- origin 検証（security.md §2.1） ----

    [Fact]
    public async Task BlazorRequest_CrossOrigin_IsRejectedWithAuditAndCounter()
    {
        var registry = new CircuitRegistry();
        var audit = new RecordingAuditRecorder();
        var (middleware, metrics) = CreateMiddleware(registry, audit, out var nextCalled);

        var context = CreateContext(ViewerPort, path: "/_blazor");
        context.Request.Headers.Origin = "http://evil.example";
        context.Request.Host = new HostString("yagura-server", ViewerPort);
        using var collector = new MetricCollector<long>(metrics.CircuitOriginRejectedCounter);

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        var recorded = Assert.Single(audit.RecordedEvents);
        Assert.Equal(AuditEventKind.CircuitOriginRejected, recorded.Kind);
        Assert.Contains("evil.example", recorded.Detail);
        Assert.Equal(1, collector.GetMeasurementSnapshot().Sum(m => m.Value));
    }

    [Fact]
    public async Task BlazorRequest_SameOrigin_IsAllowed()
    {
        var registry = new CircuitRegistry();
        var audit = new RecordingAuditRecorder();
        var (middleware, _) = CreateMiddleware(registry, audit, out var nextCalled);

        var context = CreateContext(ViewerPort, path: "/_blazor");
        context.Request.Headers.Origin = $"http://yagura-server:{ViewerPort}";
        context.Request.Host = new HostString("yagura-server", ViewerPort);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled());
        Assert.Empty(audit.RecordedEvents);
    }

    [Fact]
    public async Task BlazorRequest_WithoutOriginHeader_IsAllowed()
    {
        // Origin を送らないクライアント（ブラウザ外）は拒否しない（CircuitGuardMiddleware の
        // remarks——遮断対象は「第三者サイトのスクリプトが閲覧者のブラウザを踏み台にする」経路）。
        var registry = new CircuitRegistry();
        var audit = new RecordingAuditRecorder();
        var (middleware, _) = CreateMiddleware(registry, audit, out var nextCalled);

        var context = CreateContext(ViewerPort, path: "/_blazor");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled());
    }

    [Theory]
    [InlineData("null")]                                // 不透明オリジン → 拒否側
    [InlineData("http://yagura-server:9999")]           // ポート不一致
    [InlineData("https://yagura-server:8514")]          // スキーム不一致
    [InlineData("http://other-host:8514")]              // ホスト不一致
    public void IsSameOrigin_RejectsMismatches(string origin)
    {
        var context = CreateContext(ViewerPort, path: "/_blazor");
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("yagura-server", ViewerPort);

        Assert.False(CircuitGuardMiddleware.IsSameOrigin(origin, context.Request));
    }

    [Fact]
    public void IsSameOrigin_DefaultPortIsNormalized()
    {
        // Host ヘッダにポートがない場合はスキーム既定ポートとして突合する。
        var context = CreateContext(80, path: "/_blazor");
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("yagura-server");

        Assert.True(CircuitGuardMiddleware.IsSameOrigin("http://yagura-server", context.Request));
        Assert.True(CircuitGuardMiddleware.IsSameOrigin("http://yagura-server:80", context.Request));
    }

    // ---- circuit 数上限（security.md §2.2。SEC-1 仮値） ----

    [Fact]
    public async Task PageRequest_ViewerAtLimit_ReturnsStaticNoticeWithCountsAndCounter()
    {
        var registry = new CircuitRegistry();
        FillCircuits(registry, isAdmin: false, CircuitGovernanceDefaults.ViewerCircuitLimit);

        var audit = new RecordingAuditRecorder();
        var (middleware, metrics) = CreateMiddleware(registry, audit, out var nextCalled);
        using var collector = new MetricCollector<long>(metrics.CircuitLimitRejectedCounter);

        var context = CreateContext(ViewerPort, path: "/", componentPage: true);
        var body = new MemoryStream();
        context.Response.Body = body;

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);

        // 案内は現在の閲覧者数・上限値を含む（security.md §2.2 の要件）。
        var html = System.Text.Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains($"{CircuitGovernanceDefaults.ViewerCircuitLimit}", html);
        Assert.Equal(1, collector.GetMeasurementSnapshot().Sum(m => m.Value));

        // 上限到達の拒否は監査記録の対象ではない（§4.1 の対象一覧外。カウンタで観測する）。
        Assert.Empty(audit.RecordedEvents);
    }

    [Fact]
    public async Task PageRequest_ViewerBelowLimit_IsAllowed()
    {
        var registry = new CircuitRegistry();
        FillCircuits(registry, isAdmin: false, CircuitGovernanceDefaults.ViewerCircuitLimit - 1);

        var (middleware, _) = CreateMiddleware(registry, new RecordingAuditRecorder(), out var nextCalled);
        var context = CreateContext(ViewerPort, path: "/", componentPage: true);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled());
    }

    [Fact]
    public async Task PageRequest_AdminListener_UsesOwnLimit_NotAffectedByViewerCount()
    {
        // 上限はリスナごと（security.md §2.2「両リスナ合算の単一上限にしない」）:
        // 閲覧側が上限到達でも管理ポートの画面表示は通る。
        var registry = new CircuitRegistry();
        FillCircuits(registry, isAdmin: false, CircuitGovernanceDefaults.ViewerCircuitLimit);

        var (middleware, _) = CreateMiddleware(registry, new RecordingAuditRecorder(), out var nextCalled);
        var context = CreateContext(AdminPort, path: "/admin", componentPage: true);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled());
    }

    [Fact]
    public async Task NegotiateRequest_AtLimit_IsNotBlocked_ToProtectReconnection()
    {
        // 「既存を守り、新規を拒否する」（security.md §2.2）: 確立済み circuit の再接続も
        // /_blazor 経由のため、negotiate は上限判定の対象にしない（CircuitGuardMiddleware の
        // remarks——新規 circuit の発生源はページ表示のガードで絶つ）。
        var registry = new CircuitRegistry();
        FillCircuits(registry, isAdmin: false, CircuitGovernanceDefaults.ViewerCircuitLimit);

        var (middleware, _) = CreateMiddleware(registry, new RecordingAuditRecorder(), out var nextCalled);
        var context = CreateContext(ViewerPort, path: "/_blazor/negotiate");
        context.Request.Method = HttpMethods.Post;

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled());
    }

    private static (CircuitGuardMiddleware Middleware, WebGuardMetrics Metrics) CreateMiddleware(
        CircuitRegistry registry,
        IAuditRecorder audit,
        out Func<bool> nextCalled)
    {
        var called = false;
        var metrics = new WebGuardMetrics();
        var middleware = new CircuitGuardMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            registry,
            new YaguraAdminListenerPort(AdminPort),
            audit,
            metrics);
        nextCalled = () => called;
        return (middleware, metrics);
    }

    private static void FillCircuits(CircuitRegistry registry, bool isAdmin, int count)
    {
        for (var i = 0; i < count; i++)
        {
            registry.Register(new CircuitRecord(
                $"c{i}",
                "127.0.0.1",
                DateTimeOffset.UtcNow,
                new YaguraCircuitContext { IsAdminListener = isAdmin }));
        }
    }

    private static DefaultHttpContext CreateContext(int localPort, string path, bool componentPage = false)
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpConnectionFeature>(new HttpConnectionFeature { LocalPort = localPort });
        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "http";
        context.Request.Path = path;

        if (componentPage)
        {
            // Razor Components のページエンドポイント相当（ComponentTypeMetadata を持つ——
            // CircuitGuardMiddleware の上限判定はこのメタデータで機械判定する）。
            var endpoint = new Endpoint(
                _ => Task.CompletedTask,
                new EndpointMetadataCollection(new ComponentTypeMetadata(typeof(object))),
                path);
            context.SetEndpoint(endpoint);
        }

        return context;
    }

    private sealed class RecordingAuditRecorder : IAuditRecorder
    {
        public List<AuditEvent> RecordedEvents { get; } = [];

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            RecordedEvents.Add(auditEvent);
            return Task.CompletedTask;
        }
    }
}
