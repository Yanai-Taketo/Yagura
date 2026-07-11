using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Web.Tests.ArchitectureTests;

namespace Yagura.Web.Tests.Administration;

/// <summary>
/// アプリ独自認証のログイン/ログアウトエンドポイント（ADR-0010 Phase 1・ADR-0011 三層防御）の
/// 実 HTTP フロー検証（PR #217 レビュー指摘 1 の (a)(d)——サインイン成功の監査記録と
/// 「誰が」欄の実効化をアサーション付きで固定する。ADR-0011 決定 3・6・9 の応答統一・監査分離を
/// 固定する）。
/// </summary>
/// <remarks>
/// <para>
/// <b>フロー</b>: GET <c>/admin/login</c>（prerender 済み HTML からアンチフォージェリトークンを
/// 抽出。対応する Cookie は応答の Set-Cookie で受領）→ POST <c>/admin/login/app</c>（フォーム +
/// トークン + Cookie）→ 302 / Set-Cookie（認証 Cookie）/ 監査記録を検証する。実 Kestrel
/// （loopback・OS 採番ポート）に対する実 HTTP であり、アンチフォージェリ検証・Cookie 発行を
/// 含む本物のパイプラインを通る。<see cref="ViewerHostHarness"/> は管理リスナの
/// loopback 束縛ポートに実際に bind するため、本テストの接続は常に
/// <c>IsLoopbackAdminConnection</c> が真になる面である——三層防御の判定そのもの（IP レート制限・
/// バックオフの計算過程）は <c>AdminAuthFailureDefenseTests</c>・<c>AppAdminAuthenticationServiceTests</c>
/// の単体テストで検証し、本ファイルは「<see cref="IAppAdminAuthenticator"/> が返す
/// <see cref="AppAuthenticationOutcome"/> を HTTP 応答・監査記録へどう変換するか」（エンドポイントの
/// 責務）を固定する。
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
            new AppAuthenticationOutcome(AppAuthenticationResult.Success, "admin1", null, AdminAuthDenialLayer.None));

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
            new AppAuthenticationOutcome(AppAuthenticationResult.InvalidCredentials, "admin1", null, AdminAuthDenialLayer.None));

        await using var harness = await ViewerHostHarness.StartAsync(
            appAuthEnabled: true, appAuthenticator: authenticator, auditRecorder: audit);

        using var client = CreateClient(harness);
        var (token, _) = await FetchLoginFormAsync(client);

        var response = await PostLoginAsync(client, token, "admin1", "wrong-password");

        // 失敗理由は応答で区別しない（ユーザー列挙耐性——汎用の error=1 のみ、wait は付かない）。
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/login?error=1", response.Headers.Location?.ToString());

        // 監査には試行されたユーザー名と理由種別が残る（security.md §4.3）。
        var failed = Assert.Single(audit.Recorded, e => e.Kind == AuditEventKind.AppAuthenticationLoginFailed);
        Assert.Contains("username=admin1", failed.Detail);
        Assert.Contains("reason=InvalidCredentials", failed.Detail);
        Assert.DoesNotContain(audit.Recorded, e => e.Kind == AuditEventKind.AdminLoginSucceeded);
    }

    [Fact]
    public async Task AppLogin_DeniedByBackoff_RedirectsWithGenericError_ByteIdenticalToInvalidCredentials()
    {
        // ADR-0011 決定 3（列挙耐性の核心）: バックオフ待機中の失敗は、非実在ユーザー名・誤パスワードと
        // バイト単位で同一の応答（error=1・wait パラメータなし）にする。wait= を Location に載せると
        // curl の生ヘッダだけで実在アカウントの存在を判別できてしまう（決定 3 が名指しで排除した経路）。
        var audit = new RecordingAuditRecorder();
        var authenticator = new FakeAppAuthenticator(
            new AppAuthenticationOutcome(AppAuthenticationResult.Denied, "admin1", 4, AdminAuthDenialLayer.Backoff, BackoffCapReached: false));

        await using var harness = await ViewerHostHarness.StartAsync(
            appAuthEnabled: true, appAuthenticator: authenticator, auditRecorder: audit);

        using var client = CreateClient(harness);
        var (token, _) = await FetchLoginFormAsync(client);

        var response = await PostLoginAsync(client, token, "admin1", "wrong-password");

        // 429 は使わず（バックオフ遅延は TryAuthenticateAsync 側で既に発生済み）、wait= も付けない。
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/login?error=1", response.Headers.Location?.ToString());

        // 監査には層の別（reason=Backoff）が残る——利用者応答では区別しないが監査では区別する（決定 9）。
        var failed = Assert.Single(audit.Recorded, e => e.Kind == AuditEventKind.AppAuthenticationLoginFailed);
        Assert.Contains("username=admin1", failed.Detail);
        Assert.Contains("reason=Backoff", failed.Detail);
        Assert.DoesNotContain(audit.Recorded, e => e.Kind == AuditEventKind.AdminAuthBackoffCapReached);
    }

    [Fact]
    public async Task AppLogin_DeniedByBackoffAtCap_StillGenericError_ButRecordsBothFailureAndCapReachedAudit()
    {
        var audit = new RecordingAuditRecorder();
        var authenticator = new FakeAppAuthenticator(
            new AppAuthenticationOutcome(AppAuthenticationResult.Denied, "admin1", 30, AdminAuthDenialLayer.Backoff, BackoffCapReached: true));

        await using var harness = await ViewerHostHarness.StartAsync(
            appAuthEnabled: true, appAuthenticator: authenticator, auditRecorder: audit);

        using var client = CreateClient(harness);
        var (token, _) = await FetchLoginFormAsync(client);

        var response = await PostLoginAsync(client, token, "admin1", "wrong-password");

        // cap 到達でも利用者応答は誤パスワードと同一（列挙耐性）。cap 到達は監査 3006 にのみ残す。
        Assert.Equal("/admin/login?error=1", response.Headers.Location?.ToString());

        Assert.Single(audit.Recorded, e => e.Kind == AuditEventKind.AppAuthenticationLoginFailed);
        var capReached = Assert.Single(audit.Recorded, e => e.Kind == AuditEventKind.AdminAuthBackoffCapReached);
        Assert.Contains("username=admin1", capReached.Detail);
        Assert.Contains("waitSeconds=30", capReached.Detail);
    }

    [Fact]
    public async Task AppLogin_BackoffWaiting_And_NonexistentUser_ProduceByteIdenticalClientResponse_EnumerationResistance()
    {
        // ADR-0011 決定 3 の列挙耐性の直接の突合テスト（レビュー指摘で欠けていた検証）:
        // 閾値超えの実在アカウント（バックオフ待機中。Denied+Backoff+cap 到達）と、非実在ユーザー名
        // （InvalidCredentials）を同一の反復・同一のフォームで叩き、クライアントが観測する応答
        // （ステータス・Location ヘッダ・本文）が完全に一致することを固定する。ここが不一致だと、
        // 反復試行で実在/非実在を判別できる列挙オラクルになる。
        var backoffAuth = new FakeAppAuthenticator(
            new AppAuthenticationOutcome(AppAuthenticationResult.Denied, "realadmin", 30, AdminAuthDenialLayer.Backoff, BackoffCapReached: true));
        var nonexistentAuth = new FakeAppAuthenticator(
            new AppAuthenticationOutcome(AppAuthenticationResult.InvalidCredentials, "ghost", null, AdminAuthDenialLayer.None));

        await using var backoffHarness = await ViewerHostHarness.StartAsync(
            appAuthEnabled: true, appAuthenticator: backoffAuth, auditRecorder: new RecordingAuditRecorder());
        await using var nonexistentHarness = await ViewerHostHarness.StartAsync(
            appAuthEnabled: true, appAuthenticator: nonexistentAuth, auditRecorder: new RecordingAuditRecorder());

        using var backoffClient = CreateClient(backoffHarness);
        using var nonexistentClient = CreateClient(nonexistentHarness);

        var (backoffToken, _) = await FetchLoginFormAsync(backoffClient);
        var (nonexistentToken, _) = await FetchLoginFormAsync(nonexistentClient);

        var backoffResponse = await PostLoginAsync(backoffClient, backoffToken, "realadmin", "wrong-password");
        var nonexistentResponse = await PostLoginAsync(nonexistentClient, nonexistentToken, "ghost", "wrong-password");

        // ステータスコード・Location ヘッダが完全に一致する（実在アカウントのバックオフ待機が
        // クライアントから観測できない）。
        Assert.Equal(nonexistentResponse.StatusCode, backoffResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, backoffResponse.StatusCode);
        Assert.Equal(
            nonexistentResponse.Headers.Location?.ToString(),
            backoffResponse.Headers.Location?.ToString());
        Assert.Equal("/admin/login?error=1", backoffResponse.Headers.Location?.ToString());

        // 本文も一致（302 の本文は空——どちらも wait= を含む HTML 等を返さない）。
        var backoffBody = await backoffResponse.Content.ReadAsStringAsync();
        var nonexistentBody = await nonexistentResponse.Content.ReadAsStringAsync();
        Assert.Equal(nonexistentBody, backoffBody);
    }

    [Theory]
    [InlineData(AdminAuthDenialLayer.IpRateLimit, "layer=ip-rate-limit")]
    [InlineData(AdminAuthDenialLayer.GlobalBucket, "layer=global-bucket")]
    public async Task AppLogin_DeniedByRateLimitLayer_Returns429WithRetryAfter_AndUnifiedWaitMessage_AndRecordsRateLimitedAudit(
        AdminAuthDenialLayer layer, string expectedDetailPrefix)
    {
        var audit = new RecordingAuditRecorder();
        var authenticator = new FakeAppAuthenticator(
            new AppAuthenticationOutcome(AppAuthenticationResult.Denied, "admin1", 12, layer));

        await using var harness = await ViewerHostHarness.StartAsync(
            appAuthEnabled: true, appAuthenticator: authenticator, auditRecorder: audit);

        using var client = CreateClient(harness);
        var (token, _) = await FetchLoginFormAsync(client);

        var response = await PostLoginAsync(client, token, "admin1", "wrong-password");

        // 決定 5.1: 待たせず即座に 429 + 有限 Retry-After。①②層は送信元 IP 単位・プロセス全体の
        // 状態のみで判定しユーザー名の実在有無に依存しない（決定 4）ため、同一送信元からの実在/
        // 非実在の試行は同一の 429 + カウントダウンを受ける——この層に限り待機表示（決定 6）を出す。
        Assert.Equal((HttpStatusCode)429, response.StatusCode);
        Assert.Equal("12", Assert.Single(response.Headers.GetValues("Retry-After")));

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("しばらくお待ちください。あと 12 秒で再試行できます。", body);

        var rateLimited = Assert.Single(audit.Recorded, e => e.Kind == AuditEventKind.AdminAuthRateLimited);
        Assert.Contains(expectedDetailPrefix, rateLimited.Detail);
        Assert.Contains("retryAfterSeconds=12", rateLimited.Detail);
        Assert.DoesNotContain(audit.Recorded, e => e.Kind == AuditEventKind.AppAuthenticationLoginFailed);
    }

    [Fact]
    public async Task AppLogin_MissingAntiforgeryToken_IsRejectedWithoutReachingAuthenticator()
    {
        var audit = new RecordingAuditRecorder();
        var authenticator = new FakeAppAuthenticator(
            new AppAuthenticationOutcome(AppAuthenticationResult.Success, "admin1", null, AdminAuthDenialLayer.None));

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
    public async Task AppLogin_ReachesAuthenticator_WithLoopbackContext()
    {
        // ADR-0011 決定 4: 三層防御の loopback 判定は AdminAuthenticationExtensions.
        // IsLoopbackAdminConnection と単一の判定点を共有する。ViewerHostHarness は管理リスナの
        // loopback 束縛ポートへ実際に bind するため、エンドポイントが認証者へ渡す
        // AdminAuthAttemptContext.IsLoopback は常に true になることを固定する。
        var audit = new RecordingAuditRecorder();
        var authenticator = new FakeAppAuthenticator(
            new AppAuthenticationOutcome(AppAuthenticationResult.InvalidCredentials, "admin1", null, AdminAuthDenialLayer.None));

        await using var harness = await ViewerHostHarness.StartAsync(
            appAuthEnabled: true, appAuthenticator: authenticator, auditRecorder: audit);

        using var client = CreateClient(harness);
        var (token, _) = await FetchLoginFormAsync(client);

        await PostLoginAsync(client, token, "admin1", "wrong-password");

        Assert.True(authenticator.WasCalled);
        Assert.NotNull(authenticator.LastContext);
        Assert.True(authenticator.LastContext!.IsLoopback);
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

        public AdminAuthAttemptContext? LastContext { get; private set; }

        public Task<AppAuthenticationOutcome> TryAuthenticateAsync(
            string username, string password, AdminAuthAttemptContext context, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            LastContext = context;
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
