using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;
using Yagura.Abstractions.Auditing;
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
    private readonly IAuditRecorder? _auditRecorder;

    private string? _circuitId;
    private DateTimeOffset _openedAt;
    private ITimer? _graceTimer;
    private string? _gracePrincipalLabel;
    private bool _graceEnded;

    public YaguraCircuitHandler(
        CircuitRegistry registry,
        YaguraCircuitContext context,
        IHttpContextAccessor httpContextAccessor,
        YaguraAdminListenerPort adminPort,
        YaguraCircuitAuthenticationStateProvider authenticationStateProvider,
        TimeProvider? timeProvider = null,
        IAuditRecorder? auditRecorder = null)
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
        _auditRecorder = auditRecorder;
    }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var httpContext = _httpContextAccessor.HttpContext;

        RefreshListenerAttribution(httpContext);
        _context.RemoteAddress = httpContext?.Connection.RemoteIpAddress?.ToString();

        _circuitId = circuit.Id;
        _openedAt = _timeProvider.GetUtcNow();
        _registry.Register(new CircuitRecord(
            circuit.Id,
            _context.RemoteAddress,
            _openedAt,
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
    public override async Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var httpContext = _httpContextAccessor.HttpContext;

        RefreshListenerAttribution(httpContext);

        if (httpContext?.User is { } user)
        {
            // SEC-6（security.md §2.3。Issue #267）: 閲覧リスナ帰属の circuit で「認証あり →
            // 認証なし」への遷移（= 失効の検知点）を捉えた場合、読み取り専用の掲示表示を失効の
            // 瞬間に切らず、猶予（CircuitGovernanceDefaults.RevocationGracePeriod = SEC-6 確定値
            // 15 分）の間だけ表示を維持する。
            //
            // <b>権限昇格の防止（多層防御。オーナー指示 2026-07-17）</b>: 本猶予が管理権限の失効を
            // 遅延させて「権限なく管理画面へ到達できる」穴にならないよう、次の三重で守る:
            //  ①管理セッション（AdminSessionClaimType を持つ principal）には猶予を与えず即時反映
            //    する——SEC-6 は掲示用途（閲覧専用）のための機構であり、管理者の失効は遅延させない
            //  ②猶予中に維持する状態は SanitizeForGrace で管理標識（AdminSessionClaimType）と
            //    グループ SID（544 等）を除去した無害化 principal にする——万一 ① を素通りしても
            //    管理権限判定（IsAdminSessionAuthenticated / IsWindowsAdministrator）は構造的に不成立
            //  ③そもそも猶予対象は閲覧リスナ帰属（IsAdminListener == false）のみで、その circuit は
            //    AdminScreenAccessPolicy.Decide が管理画面の描画自体を拒否する（既存の防御）
            var previous = (await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
            var wasAuthenticated = previous.Identity?.IsAuthenticated == true;
            var isAuthenticated = user.Identity?.IsAuthenticated == true;

            if (wasAuthenticated && !isAuthenticated && _context.IsAdminListener == false
                && !AdminAuthenticationExtensions.IsAdminSessionAuthenticated(previous))
            {
                if (_graceTimer is null)
                {
                    // 維持するのは元 principal ではなく無害化コピー（防御 ②）。
                    _authenticationStateProvider.SetAuthenticationState(SanitizeForGrace(previous));
                    await StartRevocationGraceAsync(previous).ConfigureAwait(false);
                }

                // 猶予中の以降の無認証再接続では、無害化済みの状態を維持する（再設定しない）。
                return;
            }

            if (isAuthenticated && _graceTimer is not null)
            {
                // 再認証で猶予を解除する（通常状態へ復帰。終了記録は「再認証」として残す）。
                await EndRevocationGraceAsync("再認証").ConfigureAwait(false);
            }

            _authenticationStateProvider.SetAuthenticationState(user);
        }
    }

    /// <summary>
    /// 猶予中に維持する「無害化された」認証状態を作る（オーナー指示 2026-07-17。多層防御 ②）。
    /// 認証済み（<c>IsAuthenticated = true</c>）は維持して閲覧の掲示表示を継続させるが、
    /// <b>管理権限に繋がるクレームをすべて除去する</b>: 管理セッション標識
    /// （<see cref="AdminAuthenticationExtensions.AdminSessionClaimType"/>）と全グループ SID
    /// （<see cref="System.Security.Claims.ClaimTypes.GroupSid"/>——544 = BUILTIN\Administrators を含む）。
    /// これにより <c>IsAdminSessionAuthenticated</c>（管理画面の認可）も
    /// <c>IsWindowsAdministrator</c>（HTTP 側の管理者判定）も構造的に不成立になる。
    /// 閲覧セッション標識（<c>ViewerSessionClaimType</c>）は残すため、閲覧認証が有効な構成でも
    /// 掲示表示は継続する。
    /// </summary>
    private static System.Security.Claims.ClaimsPrincipal SanitizeForGrace(System.Security.Claims.ClaimsPrincipal previous)
    {
        var source = previous.Identity as System.Security.Claims.ClaimsIdentity;
        var safeClaims = (source?.Claims ?? previous.Claims)
            .Where(c => c.Type != AdminAuthenticationExtensions.AdminSessionClaimType)
            .Where(c => c.Type != System.Security.Claims.ClaimTypes.GroupSid)
            .Select(c => new System.Security.Claims.Claim(c.Type, c.Value, c.ValueType, c.Issuer))
            .ToList();

        var sanitized = new System.Security.Claims.ClaimsIdentity(
            safeClaims,
            source?.AuthenticationType,
            source?.NameClaimType ?? System.Security.Claims.ClaimTypes.Name,
            source?.RoleClaimType ?? System.Security.Claims.ClaimTypes.Role);

        return new System.Security.Claims.ClaimsPrincipal(sanitized);
    }

    /// <summary>
    /// SEC-6 の猶予を開始する: 継続許容の監査（3010）+ 満了タイマー（満了時は認証状態を
    /// 無認証へ落とし、circuit の協調切断を要求して監査 3011 を残す）。
    /// </summary>
    private async Task StartRevocationGraceAsync(System.Security.Claims.ClaimsPrincipal previous)
    {
        var deadline = _timeProvider.GetUtcNow() + CircuitGovernanceDefaults.RevocationGracePeriod;
        _gracePrincipalLabel = previous.Identity?.Name ?? "(不明)";
        _graceEnded = false;

        if (_auditRecorder is not null)
        {
            await _auditRecorder.RecordAsync(
                new AuditEvent(
                    OccurredAt: _timeProvider.GetUtcNow(),
                    Kind: AuditEventKind.CircuitRevocationGraceGranted,
                    RemoteAddress: _context.RemoteAddress,
                    RemotePort: null,
                    Detail: $"利用者={_gracePrincipalLabel} 確立時刻={_openedAt:O} 猶予満了予定={deadline:O}" +
                        $"（読み取り専用表示の継続——新着ログの購読も継続する。security.md §2.3）",
                    AuthenticatedPrincipal: _gracePrincipalLabel),
                CancellationToken.None).ConfigureAwait(false);
        }

        _graceTimer = _timeProvider.CreateTimer(
            _ => _ = OnGraceExpiredAsync(),
            state: null,
            CircuitGovernanceDefaults.RevocationGracePeriod,
            Timeout.InfiniteTimeSpan);
    }

    private async Task OnGraceExpiredAsync()
    {
        await EndRevocationGraceAsync("猶予満了").ConfigureAwait(false);

        // 満了: 認証状態を実状態（無認証）へ落とし、circuit の協調切断を要求する（再認証誘導は
        // 切断後の案内ページ——CircuitGovernor——が担う）。
        _authenticationStateProvider.SetAuthenticationState(
            new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity()));
        if (_circuitId is { } circuitId)
        {
            await _registry.RequestDisconnectAsync(circuitId, CircuitTerminationReasons.RevocationGraceExpired)
                .ConfigureAwait(false);
        }
    }

    /// <summary>猶予を終了として記録する（満了・切断・全切断・再認証のいずれか 1 回だけ）。</summary>
    private async Task EndRevocationGraceAsync(string reason)
    {
        if (_graceEnded)
        {
            return;
        }

        _graceEnded = true;
        _graceTimer?.Dispose();
        _graceTimer = null;

        if (_auditRecorder is not null)
        {
            await _auditRecorder.RecordAsync(
                new AuditEvent(
                    OccurredAt: _timeProvider.GetUtcNow(),
                    Kind: AuditEventKind.CircuitRevocationGraceEnded,
                    RemoteAddress: _context.RemoteAddress,
                    RemotePort: null,
                    Detail: $"利用者={_gracePrincipalLabel} 終了理由={reason}（circuit={_circuitId ?? "(unknown)"}）",
                    AuthenticatedPrincipal: _gracePrincipalLabel),
                CancellationToken.None).ConfigureAwait(false);
        }
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

    public override async Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // 猶予中の切断（利用者の離脱・全切断を含む）も 3011 で閉じる——3010 と対にして
        // 「見得た期間」を監査で確定させる（security.md §2.3）。
        if (_graceTimer is not null && !_graceEnded)
        {
            await EndRevocationGraceAsync("切断").ConfigureAwait(false);
        }

        _registry.Unregister(circuit.Id);
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
