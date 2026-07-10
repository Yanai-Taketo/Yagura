using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Yagura.Abstractions.Auditing;
using Yagura.Web.Administration;

namespace Yagura.Web.Tests.Administration;

/// <summary>
/// <see cref="KerberosOnlyFilterMiddleware"/>（ADR-0010 決定 2・委任事項 12）の単体テスト。
/// </summary>
public sealed class KerberosOnlyFilterMiddlewareTests
{
    private const int AdminPort = 8515;
    private const int ViewerPort = 8514;

    // NTLM Type-1 メッセージの先頭（ASCII 署名 "NTLMSSP\0" + type=1）。
    private static readonly string NtlmToken = Convert.ToBase64String(
        [.. "NTLMSSP\0"u8.ToArray(), 0x01, 0x00, 0x00, 0x00]);

    // Kerberos（SPNEGO）トークンは NTLM 署名で始まらない任意のバイト列で代表する。
    private static readonly string KerberosLikeToken = Convert.ToBase64String(
        [0x60, 0x82, 0x01, 0x00, 0x06, 0x06, 0x2b, 0x06]);

    [Fact]
    public async Task AdminPort_NtlmToken_IsRejectedWith403AndAudit()
    {
        var audit = new RecordingAuditRecorder();
        var (middleware, nextCalled) = CreateMiddleware(audit);

        var context = CreateContext(AdminPort, $"Negotiate {NtlmToken}");
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);

        var recorded = Assert.Single(audit.Recorded);
        Assert.Equal(AuditEventKind.WindowsAuthenticationHandshakeFailed, recorded.Kind);
        Assert.Contains("ntlm-rejected", recorded.Detail);
    }

    [Fact]
    public async Task AdminPort_KerberosLikeToken_PassesThrough()
    {
        var audit = new RecordingAuditRecorder();
        var (middleware, nextCalled) = CreateMiddleware(audit);

        var context = CreateContext(AdminPort, $"Negotiate {KerberosLikeToken}");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled());
        Assert.Empty(audit.Recorded);
    }

    [Fact]
    public async Task AdminPort_NoAuthorizationHeader_PassesThrough()
    {
        var audit = new RecordingAuditRecorder();
        var (middleware, nextCalled) = CreateMiddleware(audit);

        var context = CreateContext(AdminPort, authorizationHeader: null);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled());
        Assert.Empty(audit.Recorded);
    }

    [Fact]
    public async Task ViewerPort_NtlmToken_PassesThrough_MiddlewareIsAdminListenerScoped()
    {
        // リスナ限定（PR #217 レビュー指摘への対応）: Kerberos-only opt-in は管理面の
        // 保護水準の選択であり、閲覧リスナ（8514）へ誤って Negotiate ヘッダーが送られても
        // 応答を変えない（ヘッダーは単に無視される——Negotiate を要求しないリスナの自然な挙動）。
        var audit = new RecordingAuditRecorder();
        var (middleware, nextCalled) = CreateMiddleware(audit);

        var context = CreateContext(ViewerPort, $"Negotiate {NtlmToken}");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled());
        Assert.Empty(audit.Recorded);
    }

    [Fact]
    public async Task AdminPort_MalformedBase64_PassesThroughToNegotiateHandler()
    {
        // Base64 として不正な値は本ミドルウェアでは判断せず、Negotiate ハンドラの
        // エラー処理（OnAuthenticationFailed → 監査 3003）に委ねる。
        var audit = new RecordingAuditRecorder();
        var (middleware, nextCalled) = CreateMiddleware(audit);

        var context = CreateContext(AdminPort, "Negotiate !!!not-base64!!!");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled());
        Assert.Empty(audit.Recorded);
    }

    private static (KerberosOnlyFilterMiddleware Middleware, Func<bool> NextCalled) CreateMiddleware(
        RecordingAuditRecorder audit)
    {
        var nextCalled = false;
        var middleware = new KerberosOnlyFilterMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            audit,
            TimeProvider.System,
            new YaguraAdminListenerPort(AdminPort),
            NullLogger<KerberosOnlyFilterMiddleware>.Instance);

        return (middleware, () => nextCalled);
    }

    private static DefaultHttpContext CreateContext(int localPort, string? authorizationHeader)
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = localPort;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        context.Request.Path = "/admin/login/windows";

        if (authorizationHeader is not null)
        {
            context.Request.Headers.Authorization = authorizationHeader;
        }

        return context;
    }

    private sealed class RecordingAuditRecorder : IAuditRecorder
    {
        public List<AuditEvent> Recorded { get; } = [];

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Recorded.Add(auditEvent);
            return Task.CompletedTask;
        }
    }
}
