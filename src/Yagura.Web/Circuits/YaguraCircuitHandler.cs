using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;
using Yagura.Web.Administration;

namespace Yagura.Web.Circuits;

/// <summary>
/// circuit のライフサイクルを台帳（<see cref="CircuitRegistry"/>）へ反映する
/// <see cref="CircuitHandler"/>（M8-4。Issue #71。security.md §2.2 の基盤）。
/// </summary>
/// <remarks>
/// <para>
/// <b>スコープ付きで登録する</b>: CircuitHandler は circuit ごとの DI スコープから解決される
/// ため、スコープ付き登録により circuit 1 本 = 本クラス 1 インスタンスとなり、同一スコープの
/// <see cref="YaguraCircuitContext"/> と自然に対応づく。
/// </para>
/// <para>
/// <b>リスナ帰属の取得（レビュー注視点）</b>: circuit 確立時の接続（SignalR の WebSocket 要求）の
/// <c>HttpContext.Connection.LocalPort</c> を <see cref="IHttpContextAccessor"/> 経由で読む。
/// <c>OnCircuitOpenedAsync</c> は接続確立処理の実行文脈で呼ばれるため <c>HttpContext</c> が
/// 取得できる想定だが、Blazor の対話的描画の内側では <c>HttpContext</c> が無効になり得るという
/// 公式ドキュメントの一般注意があるため、<b>取得できなかった場合は帰属不明（= 閲覧相当の
/// 安全側）へ倒す</b>。帰属不明の circuit では管理画面が対話的に動作しない（fail-closed。
/// <c>AdminScreenAccessPolicy</c> 参照）——実ブラウザでの帰属取得の成立確認は E2E（M8-5 以降）の
/// 検証対象として申し送る。
/// </para>
/// <para>
/// <b>最終活動時刻（SEC-8 の「操作」）</b>: <see cref="CreateInboundActivityHandler"/> で
/// circuit への inbound activity（UI イベント・JS interop 応答等）を最終活動時刻として記録する。
/// サーバ発の表示更新（掲示ダッシュボードの自動更新）は inbound ではないため「操作」に
/// 数えない——security.md §2.2 の掲示用途を殺さない方向であり、この定義自体が SEC-8 の
/// 確定対象（<see cref="CircuitGovernanceDefaults"/> の remarks）。
/// </para>
/// </remarks>
public sealed class YaguraCircuitHandler : CircuitHandler
{
    private readonly CircuitRegistry _registry;
    private readonly YaguraCircuitContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly YaguraAdminListenerPort _adminPort;
    private readonly YaguraCircuitAuthenticationStateProvider _authenticationStateProvider;
    private readonly TimeProvider _timeProvider;

    public YaguraCircuitHandler(
        CircuitRegistry registry,
        YaguraCircuitContext context,
        IHttpContextAccessor httpContextAccessor,
        YaguraAdminListenerPort adminPort,
        YaguraCircuitAuthenticationStateProvider authenticationStateProvider,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        ArgumentNullException.ThrowIfNull(adminPort);
        ArgumentNullException.ThrowIfNull(authenticationStateProvider);

        _registry = registry;
        _context = context;
        _httpContextAccessor = httpContextAccessor;
        _adminPort = adminPort;
        _authenticationStateProvider = authenticationStateProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var httpContext = _httpContextAccessor.HttpContext;

        RefreshListenerAttribution(httpContext);
        _context.RemoteAddress = httpContext?.Connection.RemoteIpAddress?.ToString();

        _registry.Register(new CircuitRecord(
            circuit.Id,
            _context.RemoteAddress,
            _context.IsAdminListener,
            _timeProvider.GetUtcNow(),
            _context));

        if (httpContext?.User is { } user)
        {
            _authenticationStateProvider.SetAuthenticationState(user);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// circuit の接続確立/再接続のたびに呼ばれる公式フック（ADR-0010 決定 2・委任事項 2。
    /// 検証 3 が引用する Microsoft Learn の "Circuit handler to capture users for custom
    /// services" パターン）。<see cref="OnCircuitOpenedAsync"/> は最初の接続確立時のみ呼ばれるが、
    /// <see cref="OnConnectionUpAsync"/> は SignalR の再接続（circuit 喪失後の再接続を含む）の
    /// たびに呼ばれるため、<b>ここで現在の <c>HttpContext.User</c> を明示的に汲み直す</b>ことが
    /// 「接続確立時に固定されたスナップショットに頼らない」（security.md §2.3）実装の要である。
    /// </summary>
    /// <remarks>
    /// <b>認証状態と同じ理由で、リスナ帰属（<see cref="YaguraCircuitContext.IsAdminListener"/>/
    /// <see cref="YaguraCircuitContext.IsLoopbackListener"/>）もここで汲み直す</b>（ADR-0010
    /// Phase 2。PR #224 レビュー指摘 #1 への対応）: circuit 確立時に固定した帰属のままだと、
    /// loopback 束縛ポート経由で確立された circuit（既定構成では無認証許可）の再接続が別ポート
    /// （リモート HTTPS）の物理コネクションへ切り替わった場合に、`IsLoopbackListener = true` が
    /// 固定され続け「リモート経由の管理操作は常に認証必須」（ADR-0010 決定 1）の不変条件が
    /// 崩れる。帰属も認証状態と同様「接続確立時のスナップショットに頼らない」対象とし、
    /// 再接続のたびに現在の接続の実ローカルポートから再導出する。
    /// </remarks>
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var httpContext = _httpContextAccessor.HttpContext;

        RefreshListenerAttribution(httpContext);

        if (httpContext?.User is { } user)
        {
            _authenticationStateProvider.SetAuthenticationState(user);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 現在の接続の実ローカルポートからリスナ帰属を（再）導出する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><paramref name="httpContext"/> が取得できない場合は帰属不明（null）へ倒す</b>——
    /// 確立時（<see cref="OnCircuitOpenedAsync"/>）は従来からこの挙動（クラスコメント参照）。
    /// 再接続時（<see cref="OnConnectionUpAsync"/>）も同じ扱いとし、**直前の帰属を持ち越さない**:
    /// 「前回は loopback だった」ことは「今回の接続も loopback である」ことを保証しない
    /// （まさにそれが PR #224 レビュー指摘 #1 の攻撃経路）ため、判定できない再接続では帰属を
    /// 不明へ降格する。帰属不明の帰結は fail-closed 側に揃っている——管理画面は描画されず
    /// （<c>AdminScreenAccessPolicy.Decide</c> の Undetermined）、認証充足判定も認証必須側で扱う
    /// （<c>IsAuthenticationSatisfied</c> の <c>isLoopbackListener: null</c>）。復旧はページの
    /// 再読み込み（新しい HTTP 要求で帰属が再確定する）でよい。
    /// </para>
    /// <para>
    /// なお認証状態（<see cref="OnConnectionUpAsync"/> の <c>SetAuthenticationState</c>）が
    /// httpContext 不在時に「前回の状態を維持する」のと対称でないのは意図的である: 認証状態は
    /// Cookie/Negotiate という接続に依存しない資格情報に由来し、古い値を使い続けても「過剰な
    /// 許可」には直結しない（失効の即時反映は操作時の再認可——security.md §2.3——が受ける）。
    /// リスナ帰属は接続そのものの属性であり、古い値の持ち越しが直接「無認証許可の適用範囲」を
    /// 広げるため、不明時は降格する。
    /// </para>
    /// </remarks>
    private void RefreshListenerAttribution(HttpContext? httpContext)
    {
        // 取得できなければ帰属不明（null）= 安全側（remarks 参照）。
        _context.IsAdminListener = httpContext is null
            ? null
            : _adminPort.Contains(httpContext.Connection.LocalPort);

        // loopback 束縛ポート（YaguraAdminListenerPort.Port は常に loopback 用ポートを指す——
        // Program.cs が構築する配列の先頭要素。ADR-0010 Phase 2）との一致を別途判定する
        // （YaguraCircuitContext.IsLoopbackListener の remarks 参照）。
        _context.IsLoopbackListener = httpContext is null
            ? null
            : httpContext.Connection.LocalPort == _adminPort.Port;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _registry.Unregister(circuit.Id);
        return Task.CompletedTask;
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next)
    {
        return async context =>
        {
            _registry.RecordActivity(context.Circuit.Id, _timeProvider.GetUtcNow());
            await next(context).ConfigureAwait(false);
        };
    }
}
