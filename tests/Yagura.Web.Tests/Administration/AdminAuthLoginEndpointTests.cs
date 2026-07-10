using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Web.Tests.ArchitectureTests;

namespace Yagura.Web.Tests.Administration;

/// <summary>
/// アプリ独自認証のログイン/ログアウトエンドポイント（ADR-0010 Phase 1・委任事項 9）の
/// 実 HTTP フロー検証（PR #217 レビュー指摘 1 の (a)(d)——サインイン成功の監査記録と
/// 「誰が」欄の実効化をアサーション付きで固定する）。
/// </summary>
/// <remarks>
/// <para>
/// <b>フロー</b>: GET <c>/admin/login</c>（prerender 済み HTML からアンチフォージェリトークンを
/// 抽出。対応する Cookie は応答の Set-Cookie で受領）→ POST <c>/admin/login/app</c>（フォーム +
/// トークン + Cookie）→ 302 / Set-Cookie（認証 Cookie）/ 監査記録を検証する。実 Kestrel
/// （loopback・OS 採番ポート）に対する実 HTTP であり、アンチフォージェリ検証・Cookie 発行を
/// 含む本物のパイプラインを通る。
/// </para>
/// <para>
/// <b>Windows 統合認証（Negotiate）のログインフローは本テストの対象外</b>: Negotiate の
/// ハンドシェイクは OS の SSPI に依存し、インプロセスのテストハーネスでは再現できない
/// （conventions.md「実環境依存の機能は lab 検証」——AD/Kerberos の実機検証は ADR-0010
/// Phase 3 の lab 検証と合流する）。Windows 側のサインイン成功監査（同じ ID 2008）の発火
/// 経路はアプリ独自側と同型のコードであることをもって申し送る。
/// </para>
/// </remarks>
public sealed class AdminAuthLoginEndpointTests
{
    private static readonly Regex AntiforgeryTokenPattern = new(
        "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
        RegexOptions.Compiled);

    [Fact]
    public async Task AppLogin_Success_SignsInAndRecordsLoginSucceededAuditWithPrincipal()
    {
        var audit = new RecordingAuditRecorder();
        var authenticator = new FakeAppAuthenticator(
            new AppAuthenticationOutcome(AppAuthenticationResult.Success, "admin1", null));

        await using var harness = await ViewerHostHarness.StartAsync(
            appAuthEnabled: true, appAuthenticator: authenticator, auditRecorder: audit);

        using var client = CreateClient(harness);
        var (token, _) = await FetchLoginFormAsync(client);

        var response = await PostLoginAsync(client, token, "admin1", "correct-password");

        // 302 → /admin + 認証 Cookie の発行。
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin", response.Headers.Location?.ToString());
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            cookie => cookie.StartsWith("Yagura.AdminAuth=", StringComparison.Ordinal));

        // サインイン成功の監査（ID 2008）が「誰が」欄（scheme + principal）付きで残ること
        // （ADR-0010 決定 6 の実効化——PR #217 レビュー指摘 1 の中核アサーション）。
        var succeeded = Assert.Single(audit.Recorded, e => e.Kind == AuditEventKind.AdminLoginSucceeded);
        Assert.Equal("app", succeeded.AuthenticationScheme);
        Assert.Equal("admin1", succeeded.AuthenticatedPrincipal);
        Assert.NotNull(succeeded.RemoteAddress);
    }

    [Fact]
    public async Task AppLogin_InvalidCredentials_RedirectsWithGenericError_AndRecordsFailureAuditWithUsername()
    {
        var audit = new RecordingAuditRecorder();
        var authenticator = new FakeAppAuthenticator(
            new AppAuthenticationOutcome(AppAuthenticationResult.InvalidCredentials, "admin1", null));

        await using var harness = await ViewerHostHarness.StartAsync(
            appAuthEnabled: true, appAuthenticator: authenticator, auditRecorder: audit);

        using var client = CreateClient(harness);
        var (token, _) = await FetchLoginFormAsync(client);

        var response = await PostLoginAsync(client, token, "admin1", "wrong-password");

        // 失敗理由は応答で区別しない（ユーザー列挙耐性——汎用の error=1 のみ）。
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/login?error=1", response.Headers.Location?.ToString());

        // 監査には試行されたユーザー名と理由種別が残る（security.md §4.3）。
        var failed = Assert.Single(audit.Recorded, e => e.Kind == AuditEventKind.AppAuthenticationLoginFailed);
        Assert.Contains("username=admin1", failed.Detail);
        Assert.Contains("reason=InvalidCredentials", failed.Detail);
        Assert.DoesNotContain(audit.Recorded, e => e.Kind == AuditEventKind.AdminLoginSucceeded);
    }

    [Fact]
    public async Task AppLogin_LockedOutNow_RecordsLockoutAudit_WithSameGenericErrorResponse()
    {
        var audit = new RecordingAuditRecorder();
        var lockoutUntil = DateTimeOffset.Parse("2026-07-10T01:00:00Z");
        var authenticator = new FakeAppAuthenticator(
            new AppAuthenticationOutcome(AppAuthenticationResult.LockedOutNow, "admin1", lockoutUntil));

        await using var harness = await ViewerHostHarness.StartAsync(
            appAuthEnabled: true, appAuthenticator: authenticator, auditRecorder: audit);

        using var client = CreateClient(harness);
        var (token, _) = await FetchLoginFormAsync(client);

        var response = await PostLoginAsync(client, token, "admin1", "wrong-password");

        // ロックアウト到達も応答上は他の失敗と区別しない。
        Assert.Equal("/admin/login?error=1", response.Headers.Location?.ToString());

        var lockedOut = Assert.Single(audit.Recorded, e => e.Kind == AuditEventKind.AdminAccountLockedOut);
        Assert.Contains("username=admin1", lockedOut.Detail);
    }

    [Fact]
    public async Task AppLogin_MissingAntiforgeryToken_IsRejectedWithoutReachingAuthenticator()
    {
        var audit = new RecordingAuditRecorder();
        var authenticator = new FakeAppAuthenticator(
            new AppAuthenticationOutcome(AppAuthenticationResult.Success, "admin1", null));

        await using var harness = await ViewerHostHarness.StartAsync(
            appAuthEnabled: true, appAuthenticator: authenticator, auditRecorder: audit);

        using var client = CreateClient(harness);

        // トークン・Cookie なしの直接 POST（CSRF 試行相当）は csrf エラーで拒否され、
        // 認証処理（IAppAdminAuthenticator）へ到達しない（委任事項 4 の CSRF 対策）。
        var response = await client.PostAsync(
            "/admin/login/app",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "admin1",
                ["password"] = "correct-password",
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/login?error=csrf", response.Headers.Location?.ToString());
        Assert.False(authenticator.WasCalled);
    }

    [Fact]
    public async Task LoginPage_PrerenderedHtml_ContainsAntiforgeryTokenAndAppForm()
    {
        await using var harness = await ViewerHostHarness.StartAsync(appAuthEnabled: true);

        using var client = CreateClient(harness);
        var html = await client.GetStringAsync("/admin/login");

        // prerender 済み HTML にアンチフォージェリトークン入りのフォームが含まれること
        // （AdminLoginScreen——PersistentComponentState による対話的描画への持ち越しの起点）。
        Assert.Matches(AntiforgeryTokenPattern, html);
        Assert.Contains("action=\"/admin/login/app\"", html);
        Assert.Contains("name=\"username\"", html);
        Assert.Contains("name=\"password\"", html);
    }

    private static HttpClient CreateClient(ViewerHostHarness harness)
    {
        // Cookie（アンチフォージェリ Cookie）を GET → POST 間で引き継ぎ、302 を追跡しない
        // （リダイレクト先・Set-Cookie を直接アサートするため）。
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = false,
        };

        return new HttpClient(handler) { BaseAddress = harness.GetBaseAddress() };
    }

    private static async Task<(string Token, string Html)> FetchLoginFormAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/admin/login");
        var match = AntiforgeryTokenPattern.Match(html);
        Assert.True(match.Success, "prerender 済みの /admin/login にアンチフォージェリトークンが見つからない。");
        return (match.Groups[1].Value, html);
    }

    private static Task<HttpResponseMessage> PostLoginAsync(
        HttpClient client, string antiforgeryToken, string username, string password) =>
        client.PostAsync(
            "/admin/login/app",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = antiforgeryToken,
                ["username"] = username,
                ["password"] = password,
            }));

    private sealed class FakeAppAuthenticator(AppAuthenticationOutcome outcome) : IAppAdminAuthenticator
    {
        public bool WasCalled { get; private set; }

        public Task<AppAuthenticationOutcome> TryAuthenticateAsync(
            string username, string password, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(outcome);
        }
    }

    private sealed class RecordingAuditRecorder : IAuditRecorder
    {
        private readonly object _gate = new();
        private readonly List<AuditEvent> _recorded = [];

        public IReadOnlyList<AuditEvent> Recorded
        {
            get
            {
                lock (_gate)
                {
                    return _recorded.ToList();
                }
            }
        }

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _recorded.Add(auditEvent);
            }

            return Task.CompletedTask;
        }
    }
}
