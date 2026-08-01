using System.Net;
using System.Text.RegularExpressions;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.TestSupport.Fakes;
using Yagura.Web.ForwarderKit;
using Yagura.Web.Tests.ArchitectureTests;

namespace Yagura.Web.Tests.Administration;

/// <summary>
/// アップロード系 HTTP エンドポイントの専用認可ポリシー（ADR-0021 決定 1）の実 HTTP 検証
/// （委任 1 の回帰テスト①②の HTTP 面。circuit 面は
/// <see cref="ForwarderMsiPlacementServiceAuthorizationTests"/>）。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ViewerHostHarness"/> は管理リスナの loopback 束縛ポートに実際に bind するため、
/// 本テストの接続はすべて「loopback 経由」である——**loopback からの要求であっても、
/// 実際にサインインした管理セッションなしでは stage / antiforgery 配布へ到達できない**
/// （AdminPolicy の暗黙 loopback バイパスが本ポリシーには存在しない）ことを実 HTTP で固定する。
/// これは ADR-0020 決定 1 (ii)（RequireForLoopback 必須）を撤廃できる根拠そのものの回帰テスト。
/// </para>
/// <para>
/// 認可拒否の監査（3014 <c>reason=operation-auth-required</c>——「拒否が見えること」）も
/// 同時に固定する（<c>ForwarderMsiUploadAuthorizationAuditHandler</c>）。
/// </para>
/// </remarks>
public sealed class ForwarderMsiUploadEndpointAuthorizationTests
{
    private static readonly Regex AntiforgeryTokenPattern = new(
        "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
        RegexOptions.Compiled);

    [Fact]
    public async Task AntiforgeryToken_UnauthenticatedLoopback_Returns401_AndAuditsRejection()
    {
        var audit = new RecordingAuditRecorder();
        await using var harness = await ViewerHostHarness.StartAsync(
            appAuthEnabled: true,
            auditRecorder: audit,
            forwarderMsiUploadEnabled: true,
            forwarderMsiStore: new ThrowingForwarderMsiStore());

        using var client = CreateClient(harness);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/forwarder-kit/msi/antiforgery");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var rejected = Assert.Single(audit.Recorded, e => e.Kind == AuditEventKind.ForwarderMsiUploadRejected);
        Assert.Contains("operation=antiforgery-token", rejected.Detail);
        Assert.Contains("reason=operation-auth-required", rejected.Detail);
        Assert.Contains("connection=loopback", rejected.Detail);
    }

    [Fact]
    public async Task Stage_UnauthenticatedLoopback_Returns401WithoutTouchingStore_AndAuditsRejection()
    {
        var audit = new RecordingAuditRecorder();
        var store = new ThrowingForwarderMsiStore();
        await using var harness = await ViewerHostHarness.StartAsync(
            appAuthEnabled: true,
            auditRecorder: audit,
            forwarderMsiUploadEnabled: true,
            forwarderMsiStore: store);

        using var client = CreateClient(harness);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/forwarder-kit/msi/stage?architecture=x64")
        {
            Content = new ByteArrayContent([0x00, 0x01, 0x02]),
        };
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var response = await client.SendAsync(request);

        // 認可はハンドラ（antiforgery 検証・本文読み取り）より前に拒否する——ストアは触られない。
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(store.WasTouched);
        var rejected = Assert.Single(audit.Recorded, e => e.Kind == AuditEventKind.ForwarderMsiUploadRejected);
        Assert.Contains("operation=stage", rejected.Detail);
        Assert.Contains("reason=operation-auth-required", rejected.Detail);
    }

    [Fact]
    public async Task Stage_UnauthenticatedLoopback_WithoutXRequestedWith_RedirectsToLogin()
    {
        // ブラウザ遷移（X-Requested-With なし）は Cookie ハンドラの既定どおりログインへ 302 誘導
        // される——401 と 302 のどちらでも「未認証では到達できない」ことに変わりはない。
        await using var harness = await ViewerHostHarness.StartAsync(
            appAuthEnabled: true,
            forwarderMsiUploadEnabled: true,
            forwarderMsiStore: new ThrowingForwarderMsiStore());

        using var client = CreateClient(harness);
        var response = await client.GetAsync("/admin/forwarder-kit/msi/antiforgery");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        // Cookie ハンドラの既定 RedirectUri は絶対 URL（http://127.0.0.1:<port>/admin/login?ReturnUrl=…）。
        Assert.Equal("/admin/login", response.Headers.Location?.AbsolutePath);
    }

    [Fact]
    public async Task Stage_AuthenticatedAdminSession_PassesPolicyAndReachesStore()
    {
        // 委任 1 回帰テスト②: 認証済み管理者（アプリ独自認証）でポリシーを通過し、
        // antiforgery トークンの取得 → stage 本体へ到達できること（ストアの失敗応答が
        // 返る = ハンドラ本体まで進んだ証拠）。
        var authenticator = new FakeAppAuthenticator(
            new AppAuthenticationOutcome(AppAuthenticationResult.Success, "admin1", null, AdminAuthDenialLayer.None));
        var store = new StageFailingForwarderMsiStore();
        await using var harness = await ViewerHostHarness.StartAsync(
            appAuthEnabled: true,
            appAuthenticator: authenticator,
            forwarderMsiUploadEnabled: true,
            forwarderMsiStore: store);

        using var client = CreateClient(harness);

        // サインイン（実 HTTP ログインフロー——AdminAuthLoginEndpointTests と同じ経路）。
        var loginHtml = await client.GetStringAsync("/admin/login");
        var loginToken = AntiforgeryTokenPattern.Match(loginHtml);
        Assert.True(loginToken.Success);
        var loginResponse = await client.PostAsync(
            "/admin/login/app",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = loginToken.Groups[1].Value,
                ["username"] = "admin1",
                ["password"] = "correct-password",
            }));
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);

        // 認証済みで antiforgery トークン配布 → stage（JS の stage() と同じ 2 段）。
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/forwarder-kit/msi/antiforgery");
        tokenRequest.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var tokenResponse = await client.SendAsync(tokenRequest);
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var tokenJson = System.Text.Json.JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        var token = tokenJson.RootElement.GetProperty("token").GetString();
        var headerName = tokenJson.RootElement.GetProperty("headerName").GetString();
        Assert.False(string.IsNullOrEmpty(token));

        using var stageRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/forwarder-kit/msi/stage?architecture=x64")
        {
            Content = new ByteArrayContent([0x00, 0x01, 0x02]),
        };
        stageRequest.Headers.Add("X-Requested-With", "XMLHttpRequest");
        stageRequest.Headers.Add(headerName ?? "RequestVerificationToken", token);
        var stageResponse = await client.SendAsync(stageRequest);

        // ストアのフェイクは WriteFailed を返す → 400 write-failed。401/302 でないことが
        // 「ポリシーを通過してハンドラ本体まで到達した」ことの証拠。
        Assert.Equal(HttpStatusCode.BadRequest, stageResponse.StatusCode);
        Assert.True(store.WasTouched);
        Assert.Contains("write-failed", await stageResponse.Content.ReadAsStringAsync());
    }

    private static HttpClient CreateClient(ViewerHostHarness harness)
    {
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = false,
        };

        return new HttpClient(handler) { BaseAddress = harness.GetBaseAddress() };
    }

    private class ThrowingForwarderMsiStore : IForwarderMsiStore
    {
        public bool WasTouched { get; protected set; }

        public string FolderPath => @"C:\unused";

        public ForwarderMsiWriteAccess CheckWriteAccess() => new(true);

        public int CleanupStagingFiles() => 0;

        public virtual Task<ForwarderMsiStageResult> StageAsync(
            ForwarderMsiArchitecture architecture, Stream content, long? declaredLength, CancellationToken cancellationToken)
        {
            WasTouched = true;
            throw new NotSupportedException("未認証テストでストアへ到達してはならない。");
        }

        public ForwarderMsiCommitResult Commit(string stagingToken, bool versionMismatchAcknowledged, bool replaceAcknowledged)
            => throw new NotSupportedException();

        public ForwarderMsiDiscardResult Discard(string stagingToken) => throw new NotSupportedException();

        public ForwarderMsiDeleteResult Delete(ForwarderMsiArchitecture architecture, string expectedSha256)
            => throw new NotSupportedException();
    }

    private sealed class StageFailingForwarderMsiStore : ThrowingForwarderMsiStore
    {
        public override Task<ForwarderMsiStageResult> StageAsync(
            ForwarderMsiArchitecture architecture, Stream content, long? declaredLength, CancellationToken cancellationToken)
        {
            WasTouched = true;
            return Task.FromResult(ForwarderMsiStageResult.Failed(ForwarderMsiStageError.WriteFailed));
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
