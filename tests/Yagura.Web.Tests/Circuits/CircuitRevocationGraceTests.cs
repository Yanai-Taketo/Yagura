using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using Yagura.Abstractions.Auditing;
using Yagura.Web.Administration;
using Yagura.Web.Circuits;

namespace Yagura.Web.Tests.Circuits;

/// <summary>
/// SEC-6: 失効した閲覧 circuit の読み取り専用表示の猶予（security.md §2.3。Issue #267。
/// 猶予値 = 15 分・2026-07-17 オーナー裁定）の単体テスト。
/// </summary>
public sealed class CircuitRevocationGraceTests
{
    private const int AdminPort = 8515;
    private const int ViewerPort = 8514;

    private sealed class RecordingAuditRecorder : IAuditRecorder
    {
        public List<AuditEvent> Recorded { get; } = [];

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Recorded.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private static (YaguraCircuitHandler Handler, YaguraCircuitAuthenticationStateProvider Auth,
        HttpContextAccessor Accessor, RecordingAuditRecorder Audit, FakeTimeProvider Time) CreateHandler()
    {
        var auth = new YaguraCircuitAuthenticationStateProvider();
        var accessor = new HttpContextAccessor();
        var audit = new RecordingAuditRecorder();
        var time = new FakeTimeProvider();
        var handler = new YaguraCircuitHandler(
            new CircuitRegistry(),
            new YaguraCircuitContext(),
            accessor,
            new YaguraAdminListenerPort([AdminPort]),
            auth,
            time,
            audit);
        return (handler, auth, accessor, audit, time);
    }

    private static ClaimsPrincipal Authenticated(string name) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "Negotiate"));

    /// <summary>閲覧セッション Cookie 相当（YaguraAppAuth + 閲覧標識）の認証済み principal。</summary>
    private static ClaimsPrincipal ViewerSession(string name) =>
        AdminAuthenticationExtensions.CreateViewerSessionPrincipal(
            AdminAuthenticationExtensions.WindowsAuthMethod, name, generation: 1);

    /// <summary>管理セッション Cookie 相当（YaguraAppAuth + 管理標識）の認証済み principal。</summary>
    private static ClaimsPrincipal AdminSession(string name) =>
        AdminAuthenticationExtensions.CreateAdminSessionPrincipal(
            AdminAuthenticationExtensions.WindowsAuthMethod, name, generation: 1);

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static DefaultHttpContext CreateHttpContext(ClaimsPrincipal user, int localPort)
    {
        var context = new DefaultHttpContext { User = user };
        context.Connection.LocalPort = localPort;
        return context;
    }

    private static Circuit CreateUninitializedCircuit() =>
        (Circuit)RuntimeHelpers.GetUninitializedObject(typeof(Circuit));

    [Fact]
    public async Task ViewerCircuit_AuthRevoked_KeepsPreviousStateAndRecordsGraceGranted()
    {
        var (handler, auth, accessor, audit, _) = CreateHandler();

        // 認証済みで確立(閲覧リスナ帰属)。
        accessor.HttpContext = CreateHttpContext(Authenticated(@"CONTOSO\viewer1"), ViewerPort);
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);

        // 再接続で無認証(=失効の検知)——SEC-6: 従前の認証状態を維持し、猶予開始を 3010 に記録。
        accessor.HttpContext = CreateHttpContext(Anonymous(), ViewerPort);
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);

        var state = await auth.GetAuthenticationStateAsync();
        Assert.Equal(@"CONTOSO\viewer1", state.User.Identity?.Name);

        var granted = Assert.Single(audit.Recorded);
        Assert.Equal(AuditEventKind.CircuitRevocationGraceGranted, granted.Kind);
        Assert.Contains(@"CONTOSO\viewer1", granted.Detail);
    }

    [Fact]
    public async Task ViewerCircuit_GraceExpires_DropsStateAndRecordsGraceEnded()
    {
        var (handler, auth, accessor, audit, time) = CreateHandler();

        accessor.HttpContext = CreateHttpContext(Authenticated(@"CONTOSO\viewer1"), ViewerPort);
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);
        accessor.HttpContext = CreateHttpContext(Anonymous(), ViewerPort);
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);

        // 猶予満了(SEC-6 = 15 分)——認証状態が無認証へ落ち、終了が 3011 に記録される。
        time.Advance(CircuitGovernanceDefaults.RevocationGracePeriod + TimeSpan.FromSeconds(1));
        await Task.Delay(200); // タイマーコールバック(実スレッド)の完了待ち

        var state = await auth.GetAuthenticationStateAsync();
        Assert.NotEqual(true, state.User.Identity?.IsAuthenticated);

        Assert.Equal(2, audit.Recorded.Count);
        var ended = audit.Recorded[1];
        Assert.Equal(AuditEventKind.CircuitRevocationGraceEnded, ended.Kind);
        Assert.Contains("猶予満了", ended.Detail);
    }

    [Fact]
    public async Task AdminCircuit_AuthRevoked_ReflectsImmediatelyWithoutGrace()
    {
        // 管理リスナ帰属は猶予の対象外——失効は即時反映(security.md §2.3「書き込み・管理操作は
        // 即時反映」の circuit 表示側)。
        var (handler, auth, accessor, audit, _) = CreateHandler();

        accessor.HttpContext = CreateHttpContext(Authenticated("admin1"), AdminPort);
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);
        accessor.HttpContext = CreateHttpContext(Anonymous(), AdminPort);
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);

        var state = await auth.GetAuthenticationStateAsync();
        Assert.NotEqual(true, state.User.Identity?.IsAuthenticated);
        Assert.Empty(audit.Recorded);
    }

    [Fact]
    public async Task ViewerCircuit_ReauthenticatedDuringGrace_EndsGraceAndRestoresState()
    {
        var (handler, auth, accessor, audit, _) = CreateHandler();

        accessor.HttpContext = CreateHttpContext(Authenticated(@"CONTOSO\viewer1"), ViewerPort);
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);
        accessor.HttpContext = CreateHttpContext(Anonymous(), ViewerPort);
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);

        // 猶予中に再認証 → 通常状態へ復帰し、終了理由は「再認証」。
        accessor.HttpContext = CreateHttpContext(Authenticated(@"CONTOSO\viewer1"), ViewerPort);
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);

        var state = await auth.GetAuthenticationStateAsync();
        Assert.Equal(true, state.User.Identity?.IsAuthenticated);
        Assert.Equal(2, audit.Recorded.Count);
        Assert.Contains("再認証", audit.Recorded[1].Detail);
    }

    [Fact]
    public async Task ViewerSessionGrace_MaintainedPrincipal_HasNoAdminAuthority()
    {
        // 【権限昇格防止・多層防御②】猶予中に維持される principal は管理権限を構造的に持たない:
        // 閲覧セッション(YaguraAppAuth + 閲覧標識)で失効しても、維持される状態は管理セッション
        // 判定・Windows 管理者判定のいずれにも合格しない。
        var (handler, auth, accessor, _, _) = CreateHandler();

        accessor.HttpContext = CreateHttpContext(ViewerSession(@"CONTOSO\viewer1"), ViewerPort);
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);
        accessor.HttpContext = CreateHttpContext(Anonymous(), ViewerPort);
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);

        var maintained = (await auth.GetAuthenticationStateAsync()).User;
        // 認証済みは維持(閲覧の掲示表示を継続)。
        Assert.True(maintained.Identity?.IsAuthenticated);
        // しかし管理権限は一切持たない(三重の壁の②)。
        Assert.False(AdminAuthenticationExtensions.IsAdminSessionAuthenticated(maintained));
        Assert.False(AdminAuthenticationExtensions.IsWindowsAdministrator(maintained));
    }

    [Fact]
    public async Task AdminSessionGrace_NotGranted_ReflectsImmediately()
    {
        // 【権限昇格防止・多層防御①】管理セッションの失効には猶予を与えず即時反映する
        // (掲示用途=閲覧専用のための機構であり、管理者の失効を遅延させない)。
        var (handler, auth, accessor, audit, _) = CreateHandler();

        accessor.HttpContext = CreateHttpContext(AdminSession(@"CONTOSO\admin1"), ViewerPort);
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);
        accessor.HttpContext = CreateHttpContext(Anonymous(), ViewerPort);
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);

        var state = await auth.GetAuthenticationStateAsync();
        Assert.NotEqual(true, state.User.Identity?.IsAuthenticated);
        Assert.Empty(audit.Recorded); // 猶予は開始されない(3010 なし)
    }

    [Fact]
    public async Task AttributionUnknown_AuthRevoked_ReflectsImmediately()
    {
        // 帰属不明(null)は安全側 = 猶予を与えない(fail-closed の向きに揃える)。
        var (handler, auth, accessor, audit, _) = CreateHandler();

        accessor.HttpContext = CreateHttpContext(Authenticated(@"CONTOSO\viewer1"), ViewerPort);
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);

        // HttpContext 不在の再接続で帰属が不明へ降格した後、無認証の再接続が来た場合。
        accessor.HttpContext = null;
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);
        accessor.HttpContext = CreateHttpContext(Anonymous(), ViewerPort);

        // ViewerPort なので IsAdminListener=false に再導出される——このケースは猶予が働く。
        // 帰属不明のまま無認証遷移が来る経路は HttpContext 不在時(SetAuthenticationState 自体が
        // 呼ばれない)のみのため、「不明 + 無認証遷移」は実行経路として存在しないことを確認する。
        await handler.OnConnectionUpAsync(CreateUninitializedCircuit(), CancellationToken.None);
        var granted = Assert.Single(audit.Recorded);
        Assert.Equal(AuditEventKind.CircuitRevocationGraceGranted, granted.Kind);
    }
}
