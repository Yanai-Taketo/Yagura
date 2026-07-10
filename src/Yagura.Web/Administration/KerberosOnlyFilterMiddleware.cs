using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Yagura.Abstractions.Auditing;

namespace Yagura.Web.Administration;

/// <summary>
/// Kerberos-only モード（ADR-0010 決定 2・委任事項 12）の NTLM トークン遮断ミドルウェア。
/// </summary>
/// <remarks>
/// <see cref="AdminAuthenticationExtensions"/> の remarks に記したライブ検証結果のとおり、
/// <c>NegotiateOptions</c> 自体には NTLM を無効化する組み込みオプションが存在しないため、
/// Negotiate ハンドラの手前で <c>Authorization: Negotiate &lt;Base64&gt;</c> ヘッダーを検査し、
/// デコード結果が NTLM トークンの既知の署名（ASCII <c>"NTLMSSP\0"</c>）で始まる場合は
/// ハンドラへ渡さず 403 で拒否する（ADR-0010 検証 1: 「ヘッダーの Base64 デコード結果
/// （<c>HTTP</c> = Kerberos、<c>NTLM</c> = NTLM）でしか判別できない」の実装）。
/// </remarks>
public sealed class KerberosOnlyFilterMiddleware
{
    private static readonly byte[] NtlmSignature = "NTLMSSP\0"u8.ToArray();

    private readonly RequestDelegate _next;
    private readonly IAuditRecorder _auditRecorder;
    private readonly TimeProvider _timeProvider;
    private readonly YaguraAdminListenerPort _adminPort;
    private readonly ILogger<KerberosOnlyFilterMiddleware> _logger;

    public KerberosOnlyFilterMiddleware(
        RequestDelegate next,
        IAuditRecorder auditRecorder,
        TimeProvider timeProvider,
        YaguraAdminListenerPort adminPort,
        ILogger<KerberosOnlyFilterMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(auditRecorder);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(adminPort);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _auditRecorder = auditRecorder;
        _timeProvider = timeProvider;
        _adminPort = adminPort;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 検査対象は管理リスナ（Negotiate 認証を要求する唯一のリスナ）に限る（PR #217
        // レビュー指摘への対応）: 閲覧リスナ（8514）は Negotiate を要求せず、誤って
        // Negotiate ヘッダーを送ったクライアントに対してもヘッダーは単に無視されるのが
        // 自然な挙動であり、本 opt-in（管理面の保護水準の選択）が閲覧面の応答を変える
        // べきではない。判定は ListenerPortGuardMiddleware と同じく接続の実ローカルポート
        // （クライアントが偽装できない値）で行う。
        if (!_adminPort.Contains(context.Connection.LocalPort))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var authorizationHeader = context.Request.Headers.Authorization.ToString();

        if (authorizationHeader.StartsWith("Negotiate ", StringComparison.OrdinalIgnoreCase))
        {
            var tokenBase64 = authorizationHeader["Negotiate ".Length..].Trim();

            byte[]? tokenBytes = null;
            try
            {
                tokenBytes = Convert.FromBase64String(tokenBase64);
            }
            catch (FormatException)
            {
                // Base64 として不正な値はここでは判断せず、通常の Negotiate ハンドラの
                // エラー処理（OnAuthenticationFailed）に委ねる。
            }

            if (tokenBytes is { Length: >= 8 } && tokenBytes.AsSpan(0, 8).SequenceEqual(NtlmSignature))
            {
                await _auditRecorder.RecordAsync(new AuditEvent(
                    OccurredAt: _timeProvider.GetUtcNow(),
                    Kind: AuditEventKind.WindowsAuthenticationHandshakeFailed,
                    RemoteAddress: context.Connection.RemoteIpAddress?.ToString(),
                    RemotePort: context.Connection.RemotePort,
                    AttemptedPath: context.Request.Path,
                    ReachedListenerPort: context.Connection.LocalPort,
                    Detail: "ntlm-rejected-by-kerberos-only-policy"),
                    context.RequestAborted).ConfigureAwait(false);

                _logger.LogWarning(
                    "[kerberos-only] Kerberos-only モードが有効なため NTLM トークンを拒否しました（接続元 {RemoteAddress}）。",
                    context.Connection.RemoteIpAddress);

                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync(
                    "Kerberos-only mode is enabled; NTLM authentication is not accepted. " +
                    "Ensure Kerberos SSO is available (correct SPN registration and domain membership), " +
                    "or use the application-specific account instead.",
                    context.RequestAborted).ConfigureAwait(false);
                return;
            }
        }

        await _next(context).ConfigureAwait(false);
    }
}
