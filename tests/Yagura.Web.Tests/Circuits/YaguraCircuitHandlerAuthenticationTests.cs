using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;
using Yagura.Web.Administration;
using Yagura.Web.Circuits;

namespace Yagura.Web.Tests.Circuits;

/// <summary>
/// <see cref="YaguraCircuitHandler"/> の認証状態の汲み直し（ADR-0010 決定 2・委任事項 2）の
/// 単体テスト。
/// </summary>
/// <remarks>
/// <para>
/// Microsoft Learn が示す公式パターン（<c>OnConnectionUpAsync</c> + <c>AuthenticationStateProvider.
/// AuthenticationStateChanged</c>）が実装どおりに動作すること——「接続確立時に固定された
/// スナップショットに頼らない」ことをテストで固定する（Phase 1 受け入れ条件 (i)）。
/// </para>
/// <para>
/// <b><see cref="Circuit"/> の構築について</b>: <c>Circuit</c> は内部コンストラクタ
/// （<c>Circuit(CircuitHost)</c>）のみを持ち、公開の構築手段がない（.NET 10 ソース確認）。
/// <see cref="YaguraCircuitHandler.OnConnectionUpAsync"/> の実装は <c>circuit</c> 引数自体を
/// 参照しない（<c>HttpContext.User</c> のみを参照する）ため、
/// <see cref="RuntimeHelpers.GetUninitializedObject"/> で構築した未初期化インスタンスを
/// そのまま渡せる——<c>circuit.Id</c> にアクセスする経路（<c>OnCircuitOpenedAsync</c>）は
/// 本テストの対象外（既存の <c>CircuitRegistry</c> 系テストの管轄）。
/// </para>
/// </remarks>
public sealed class YaguraCircuitHandlerAuthenticationTests
{
    private const int AdminPort = 8515;

    [Fact]
    public async Task OnConnectionUpAsync_RefetchesCurrentHttpContextUser_AndNotifiesChange()
    {
        // 検証3の公式パターン: OnConnectionUpAsync は再接続のたびに現在の HttpContext.User を
        // 汲み直す——接続確立時に固定されたスナップショット(FixedAuthenticationStateProvider)に
        // 頼らない実装であることを固定する。
        var (handler, authStateProvider, httpContextAccessor) = CreateHandler(AdminPort);

        var notified = false;
        authStateProvider.AuthenticationStateChanged += _ => notified = true;

        var reconnectedUser = CreatePrincipal("YaguraAppAuth", "admin1");
        httpContextAccessor.HttpContext = CreateHttpContext(reconnectedUser, AdminPort);

        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);

        Assert.True(notified);
        var state = await authStateProvider.GetAuthenticationStateAsync();
        Assert.Equal("admin1", state.User.Identity?.Name);
        Assert.Equal("YaguraAppAuth", state.User.Identity?.AuthenticationType);
    }

    [Fact]
    public async Task OnConnectionUpAsync_ChangedUserBetweenReconnects_ReflectsLatestPrincipal()
    {
        var (handler, authStateProvider, httpContextAccessor) = CreateHandler(AdminPort);

        httpContextAccessor.HttpContext = CreateHttpContext(CreatePrincipal("Negotiate", "CONTOSO\\jdoe"), AdminPort);
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);
        Assert.Equal("CONTOSO\\jdoe", (await authStateProvider.GetAuthenticationStateAsync()).User.Identity?.Name);

        // 別の接続への切り替え(circuit 再接続)で、直前のスナップショットではなく現在の
        // HttpContext.User がそのまま反映されること。
        httpContextAccessor.HttpContext = CreateHttpContext(CreatePrincipal("YaguraAppAuth", "admin1"), AdminPort);
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);
        Assert.Equal("admin1", (await authStateProvider.GetAuthenticationStateAsync()).User.Identity?.Name);
    }

    [Fact]
    public async Task OnConnectionUpAsync_HttpContextUnavailable_LeavesPreviousStateUnchanged()
    {
        var (handler, authStateProvider, httpContextAccessor) = CreateHandler(AdminPort);
        authStateProvider.SetAuthenticationState(CreatePrincipal("Negotiate", "CONTOSO\\jdoe"));

        httpContextAccessor.HttpContext = null;
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);

        var state = await authStateProvider.GetAuthenticationStateAsync();
        Assert.Equal("CONTOSO\\jdoe", state.User.Identity?.Name);
    }

    // ---- リスナ帰属の汲み直し（ADR-0010 Phase 2。PR #224 レビュー指摘 #1） ----

    [Fact]
    public async Task OnConnectionUpAsync_ReconnectViaRemoteHttpsPort_DowngradesLoopbackAttribution()
    {
        // loopback 束縛ポート(8515)で確立した circuit が、リモート HTTPS ポート(8516)経由の
        // 物理コネクションで再接続した場合、IsLoopbackListener が false へ更新されること
        // (「リモート経由の管理操作は常に認証必須」——ADR-0010 決定 1——の不変条件を再接続
        // 経路でも成立させる。確立時のスナップショットに帰属が固定されない)。
        // OnCircuitOpenedAsync は circuit.Id へアクセスするため未初期化 Circuit を渡せない
        // (クラス remarks 参照)。OnConnectionUpAsync は初回接続時にも呼ばれる公式フックであり、
        // 帰属の導出はどちらの経路でも同一の RefreshListenerAttribution を通る——初回接続の
        // 模擬にも OnConnectionUpAsync を使う(以下のテストも同じ)。
        const int loopbackPort = 8515;
        const int remoteHttpsPort = 8516;
        var (handler, context, httpContextAccessor) = CreateHandlerWithContext([loopbackPort, remoteHttpsPort]);

        httpContextAccessor.HttpContext = CreateHttpContext(CreatePrincipal("YaguraAppAuth", "admin1"), loopbackPort);
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);
        Assert.True(context.IsAdminListener);
        Assert.True(context.IsLoopbackListener);

        // 再接続: 物理コネクションがリモート HTTPS ポートへ切り替わる。
        httpContextAccessor.HttpContext = CreateHttpContext(CreatePrincipal("YaguraAppAuth", "admin1"), remoteHttpsPort);
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);

        Assert.True(context.IsAdminListener, "リモート HTTPS ポートも管理リスナ帰属であること(描画可否の判定)。");
        Assert.False(context.IsLoopbackListener, "リモート HTTPS ポート経由の再接続後は loopback 帰属を持ち越してはならない(認証必須側)。");
    }

    [Fact]
    public async Task OnConnectionUpAsync_ReconnectWithoutHttpContext_DowngradesAttributionToUnknown()
    {
        // 再接続時に HttpContext を取得できない場合、直前の帰属(loopback = 無認証許可側)を
        // 持ち越さず不明(null)へ降格すること(fail-closed。YaguraCircuitHandler.
        // RefreshListenerAttribution の remarks 参照)。
        const int loopbackPort = 8515;
        var (handler, context, httpContextAccessor) = CreateHandlerWithContext([loopbackPort]);

        httpContextAccessor.HttpContext = CreateHttpContext(CreatePrincipal("YaguraAppAuth", "admin1"), loopbackPort);
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);
        Assert.True(context.IsLoopbackListener);

        httpContextAccessor.HttpContext = null;
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);

        Assert.Null(context.IsAdminListener);
        Assert.Null(context.IsLoopbackListener);
    }

    [Fact]
    public async Task OnConnectionUpAsync_ReconnectBackToLoopback_RestoresLoopbackAttribution()
    {
        // 帰属の汲み直しは双方向: リモート経由の後に loopback へ戻れば loopback 帰属も回復する
        // (毎回の再導出であり、片方向のラッチではないことの確認)。
        const int loopbackPort = 8515;
        const int remoteHttpsPort = 8516;
        var (handler, context, httpContextAccessor) = CreateHandlerWithContext([loopbackPort, remoteHttpsPort]);

        httpContextAccessor.HttpContext = CreateHttpContext(CreatePrincipal("YaguraAppAuth", "admin1"), remoteHttpsPort);
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);
        Assert.False(context.IsLoopbackListener);

        httpContextAccessor.HttpContext = CreateHttpContext(CreatePrincipal("YaguraAppAuth", "admin1"), loopbackPort);
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);

        Assert.True(context.IsLoopbackListener);
    }

    [Fact]
    public void IsWindowsAdministrator_RequiresNegotiateSchemeAndBuiltinAdministratorsGroupSid()
    {
        var adminUser = CreatePrincipal("Negotiate", "CONTOSO\\jdoe");
        ((ClaimsIdentity)adminUser.Identity!).AddClaim(new Claim(ClaimTypes.GroupSid, AdminAuthenticationExtensions.BuiltinAdministratorsSid));

        var nonAdminUser = CreatePrincipal("Negotiate", "CONTOSO\\bob");

        var appAuthUser = CreatePrincipal("YaguraAppAuth", "admin1");

        Assert.True(AdminAuthenticationExtensions.IsWindowsAdministrator(adminUser));
        Assert.False(AdminAuthenticationExtensions.IsWindowsAdministrator(nonAdminUser));
        Assert.False(AdminAuthenticationExtensions.IsWindowsAdministrator(appAuthUser));
    }

    [Fact]
    public void IsAppAuthenticated_RequiresAppAuthScheme()
    {
        var appAuthUser = CreatePrincipal("YaguraAppAuth", "admin1");
        var windowsUser = CreatePrincipal("Negotiate", "CONTOSO\\jdoe");

        Assert.True(AdminAuthenticationExtensions.IsAppAuthenticated(appAuthUser));
        Assert.False(AdminAuthenticationExtensions.IsAppAuthenticated(windowsUser));
    }

    private static Circuit CreateUninitializedCircuit() =>
        (Circuit)RuntimeHelpers.GetUninitializedObject(typeof(Circuit));

    private static (YaguraCircuitHandler Handler, YaguraCircuitAuthenticationStateProvider AuthStateProvider, TestHttpContextAccessor HttpContextAccessor)
        CreateHandler(int adminPort)
    {
        var registry = new CircuitRegistry();
        var context = new YaguraCircuitContext();
        var httpContextAccessor = new TestHttpContextAccessor();
        var authStateProvider = new YaguraCircuitAuthenticationStateProvider();

        var handler = new YaguraCircuitHandler(
            registry,
            context,
            httpContextAccessor,
            new YaguraAdminListenerPort(adminPort),
            authStateProvider);

        return (handler, authStateProvider, httpContextAccessor);
    }

    /// <summary>
    /// リスナ帰属の検証用（<see cref="YaguraCircuitContext"/> を返す変種。ADR-0010 Phase 2 の
    /// 複数管理ポート——loopback + リモート HTTPS——を渡せる）。
    /// </summary>
    private static (YaguraCircuitHandler Handler, YaguraCircuitContext Context, TestHttpContextAccessor HttpContextAccessor)
        CreateHandlerWithContext(IReadOnlyList<int> adminPorts)
    {
        var registry = new CircuitRegistry();
        var context = new YaguraCircuitContext();
        var httpContextAccessor = new TestHttpContextAccessor();
        var authStateProvider = new YaguraCircuitAuthenticationStateProvider();

        var handler = new YaguraCircuitHandler(
            registry,
            context,
            httpContextAccessor,
            new YaguraAdminListenerPort(adminPorts),
            authStateProvider);

        return (handler, context, httpContextAccessor);
    }

    private static HttpContext CreateHttpContext(ClaimsPrincipal user, int localPort)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.LocalPort = localPort;
        httpContext.User = user;
        return httpContext;
    }

    private static ClaimsPrincipal CreatePrincipal(string authenticationType, string name)
    {
        var identity = new ClaimsIdentity(authenticationType);
        identity.AddClaim(new Claim(ClaimTypes.Name, name));
        return new ClaimsPrincipal(identity);
    }

    private sealed class TestHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
