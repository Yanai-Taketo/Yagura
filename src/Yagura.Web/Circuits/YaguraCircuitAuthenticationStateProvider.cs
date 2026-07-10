using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Yagura.Web.Circuits;

/// <summary>
/// circuit 単位の認証状態を明示的に汲み直す <see cref="AuthenticationStateProvider"/>
/// （ADR-0010 決定 2・委任事項 2）。
/// </summary>
/// <remarks>
/// <para>
/// <b>公式パターンの実装</b>: Microsoft Learn "ASP.NET Core server-side and Blazor Web App
/// additional security scenarios" が示す「Circuit handler to capture users for custom services」
/// パターン（<c>CircuitHandler.OnConnectionUpAsync</c> + <c>AuthenticationStateProvider.
/// AuthenticationStateChanged</c>）をそのまま採用する（ADR-0010 検証 3）。既定の
/// <c>FixedAuthenticationStateProvider</c>（circuit 生存期間中は接続確立時の状態を固定として
/// 扱う）に頼らず、<see cref="YaguraCircuitHandler.OnConnectionUpAsync"/> が SignalR の
/// 再接続のたびに現在の <c>HttpContext.User</c> を汲み直し、<see cref="SetAuthenticationState"/>
/// 経由でこのプロバイダへ反映する——<c>WindowsPrincipal</c>（Negotiate）が元の HTTP 接続に
/// 紐づく OS ハンドルであり、接続をまたいで使い続けられる保証がないという Negotiate 固有の
/// 技術的制約（同検証 3）の下でも、security.md §2.3「操作のたびに現在の認証状態で認可する」を
/// 満たす。
/// </para>
/// <para>
/// <b>スコープ付きで登録する</b>（<c>AddScoped</c>）: circuit ごとの DI スコープから解決され、
/// <see cref="YaguraCircuitHandler"/>・<c>AdminScreenLayout</c> と同一スコープの
/// インスタンスを共有する（<see cref="YaguraCircuitContext"/> と同じ設計）。
/// </para>
/// </remarks>
public sealed class YaguraCircuitAuthenticationStateProvider : AuthenticationStateProvider
{
    private AuthenticationState _currentState = new(new ClaimsPrincipal(new ClaimsIdentity()));

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(_currentState);

    /// <summary>
    /// 現在の認証状態を更新し、購読者（<c>&lt;AuthorizeView&gt;</c>・
    /// <c>Task&lt;AuthenticationState&gt;</c> カスケードパラメータの消費者）へ変化を通知する。
    /// </summary>
    public void SetAuthenticationState(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        _currentState = new AuthenticationState(user);
        NotifyAuthenticationStateChanged(Task.FromResult(_currentState));
    }
}
