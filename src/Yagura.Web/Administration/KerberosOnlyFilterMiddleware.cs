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
/// デコード結果に NTLM トークンの既知の署名（ASCII <c>"NTLMSSP\0"</c>）が含まれる場合は
/// ハンドラへ渡さず 403 で拒否する（ADR-0010 検証 1: 「ヘッダーの Base64 デコード結果
/// （<c>HTTP</c> = Kerberos、<c>NTLM</c> = NTLM）でしか判別できない」の実装）。
/// 署名の検査はトークン先頭だけでなく全体を対象にスキャンする——生の NTLM トークンは
/// オフセット 0 に署名が現れるが、HTTP Negotiate クライアントが送る SPNEGO（GSS-API）
/// トークンは 0x60 で始まり、NTLM の mechToken（署名を含む）が非ゼロオフセットに
/// 埋め込まれるため、先頭一致だけでは SPNEGO でラップされた NTLM を見逃す。全体スキャンは
/// 両方のケースを一つの判定でカバーする保守的なヒューリスティックであり、正規の Kerberos
/// AP-REQ・SPNEGO-Kerberos トークンが ASCII バイト列 <c>"NTLMSSP\0"</c> を含むことはない
/// という性質から誤検知（false positive）は生じない。完全な SPNEGO ASN.1 パースという
/// 代替もあるが、この性質がある以上不要と判断した。なお、SPNEGO でラップされた NTLM が
/// 実機のクライアントで実際にこの形（mechToken に生の NTLM 署名を含む）で提示されることの
/// 実地検証（ライブ確認）はまだ行っていない——本変更は判定を追加する方向にしか作用しない
/// （拒否が増える方向のみ）ため fail-safe ではあるが、正直な限界として記録する。
/// </remarks>
public sealed class KerberosOnlyFilterMiddleware
{
    private static readonly byte[] NtlmSignature = "NTLMSSP\0"u8.ToArray();

    private readonly RequestDelegate _next;
    private readonly bool _adminKerberosOnly;
    private readonly bool _viewerKerberosOnly;
    private readonly IAuditRecorder _auditRecorder;
    private readonly TimeProvider _timeProvider;
    private readonly YaguraAdminListenerPort _adminPort;
    private readonly ILogger<KerberosOnlyFilterMiddleware> _logger;

    public KerberosOnlyFilterMiddleware(
        RequestDelegate next,
        bool adminKerberosOnly,
        bool viewerKerberosOnly,
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
        _adminKerberosOnly = adminKerberosOnly;
        _viewerKerberosOnly = viewerKerberosOnly;
        _auditRecorder = auditRecorder;
        _timeProvider = timeProvider;
        _adminPort = adminPort;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Kerberos-only はリスナ別の opt-in（ADR-0010 Phase 4）: 接続の実ローカルポートが管理リスナ帰属
        // なら管理側の設定（Admin:Authentication:Windows:KerberosOnly）を、そうでなければ閲覧側の設定
        // （Viewer:Authentication:Windows:KerberosOnly）を適用する。判定は ListenerPortGuardMiddleware と
        // 同じく接続の実ローカルポート（クライアントが偽装できない値）で行う。当該リスナで Kerberos-only が
        // 無効なら NTLM 検査自体を行わずに通過させる——一方のリスナの opt-in が他方の応答を変えないため。
        var onAdminListener = _adminPort.Contains(context.Connection.LocalPort);
        var kerberosOnlyApplies = onAdminListener ? _adminKerberosOnly : _viewerKerberosOnly;
        if (!kerberosOnlyApplies)
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

            // 署名はトークン先頭だけでなく全体から探す: 生の NTLM はオフセット 0 に署名を持つが、
            // SPNEGO の NegTokenInit/NegTokenResp は NTLM の mechToken（署名を含む）を非ゼロ
            // オフセットに埋め込むため、先頭一致だけでは SPNEGO でラップされた NTLM を通してしまう。
            if (tokenBytes is not null && ContainsNtlmSignature(tokenBytes))
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

    /// <summary>
    /// デコード済みトークン全体から NTLM 署名（<see cref="NtlmSignature"/>）を探す。
    /// 生の NTLM トークンはオフセット 0 に、SPNEGO でラップされた NTLM は非ゼロオフセットの
    /// mechToken 内に署名を持つため、位置を問わず出現を検査する。
    /// </summary>
    private static bool ContainsNtlmSignature(byte[] token)
        => token.AsSpan().IndexOf(NtlmSignature.AsSpan()) >= 0;
}
