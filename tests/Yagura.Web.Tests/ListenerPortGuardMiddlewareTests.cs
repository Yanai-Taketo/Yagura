using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace Yagura.Web.Tests;

/// <summary>
/// <see cref="ListenerPortGuardMiddleware"/> の単体テスト（M6-1。Issue #51）。
/// </summary>
/// <remarks>
/// security.md §1 L-3b の前提となる「管理系ルートが閲覧リスナ経由で絶対に実行されない」
/// 構造を検証する。<c>HttpContext.Connection.LocalPort</c> による判定が、管理リスナの
/// ポート番号と一致するときのみ管理系エンドポイントの実行を許すことを確認する。
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

        var middleware = new ListenerPortGuardMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, AdminPort);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled, "管理ポート経由の管理系エンドポイントは実行されるべき。");
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_AdminEndpoint_ViaViewerPort_ReturnsNotFoundAndDoesNotCallNext()
    {
        // security.md §1 L-3b の前提: 閲覧リスナ(8514)経由での管理系エンドポイント到達は
        // 実行されない(404)。「拒否 + 監査記録」自体は後続 Issue #52 のスコープ。
        var context = CreateContext(localPort: ViewerPort, endpointMetadata: ListenerPortGuardEndpointMetadata.Admin);
        var nextCalled = false;

        var middleware = new ListenerPortGuardMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, AdminPort);

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled, "閲覧ポート経由では管理系エンドポイントを実行してはならない。");
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_ViewerEndpoint_NoGuardMetadata_ViaViewerPort_CallsNext()
    {
        // 閲覧系エンドポイント(ガードメタデータなし)は閲覧ポート経由で通常どおり実行される。
        var context = CreateContext(localPort: ViewerPort, endpointMetadata: null);
        var nextCalled = false;

        var middleware = new ListenerPortGuardMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, AdminPort);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_ViewerEndpoint_NoGuardMetadata_ViaAdminPort_CallsNext()
    {
        // 設計判断: 閲覧系ルートは管理リスナからも到達できる(ui.md §4 の不変条件は
        // 「閲覧リスナに書き込み系を置かない」であり、管理リスナ(loopback 限定)からの
        // 閲覧系ルート到達を妨げる理由はない)。
        var context = CreateContext(localPort: AdminPort, endpointMetadata: null);
        var nextCalled = false;

        var middleware = new ListenerPortGuardMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, AdminPort);

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

        var middleware = new ListenerPortGuardMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, AdminPort);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    private static DefaultHttpContext CreateContext(int localPort, ListenerPortGuardEndpointMetadata? endpointMetadata)
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpConnectionFeature>(new HttpConnectionFeature { LocalPort = localPort });

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
}
