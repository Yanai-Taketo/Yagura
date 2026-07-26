using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Yagura.Abstractions.Auditing;

namespace Yagura.Web.Administration;

/// <summary>
/// フォワーダ MSI アップロード系エンドポイントを識別するマーカーメタデータ
/// （<see cref="ForwarderMsiUploadAuthorizationAuditHandler"/> が認可拒否の監査対象を特定する）。
/// </summary>
public sealed class ForwarderMsiUploadOperationMetadata
{
    /// <summary>antiforgery トークン配布（<c>GET …/antiforgery</c>）。</summary>
    public static readonly ForwarderMsiUploadOperationMetadata AntiforgeryToken = new("antiforgery-token");

    /// <summary>アップロード本文の受理（<c>POST …/stage</c>）。</summary>
    public static readonly ForwarderMsiUploadOperationMetadata Stage = new("stage");

    private ForwarderMsiUploadOperationMetadata(string operation) => Operation = operation;

    /// <summary>監査 Detail の <c>operation=</c> に載せる操作名。</summary>
    public string Operation { get; }
}

/// <summary>
/// アップロード専用認可ポリシー（<see cref="AdminAuthenticationExtensions.ForwarderMsiUploadPolicyName"/>）の
/// 拒否を監査 3014 へ記録する <see cref="IAuthorizationMiddlewareResultHandler"/>（ADR-0021 決定 1）。
/// </summary>
/// <remarks>
/// <para>
/// <b>なぜミドルウェア結果ハンドラなのか</b>: 宣言的な <c>RequireAuthorization</c> は拒否時に
/// エンドポイントハンドラを実行しないため、ハンドラ内では拒否を記録できない。かといって
/// 認可をハンドラ内の手続き検査に落とすと、宣言的付与（テストで機械検証できる）を失う。
/// 既定ハンドラをラップし、対象エンドポイント（<see cref="ForwarderMsiUploadOperationMetadata"/>
/// マーカー付き）の拒否のみを記録してから既定の応答（challenge → 401/302）へ委譲する——
/// 「拒否されること」だけでなく「拒否が見えること」が設計条件（無認証 loopback 主体による
/// アップロード系操作の試行は、本 ADR が新設した権限昇格試行のシグナルそのもの）。
/// </para>
/// <para>
/// 登録は <c>AddYaguraAdmin</c>（<see cref="IAuthorizationMiddlewareResultHandler"/> の既定実装を
/// 置き換える形——マーカーの無いエンドポイントは既定ハンドラへ素通しするため、他の認可挙動は
/// 一切変わらない）。理由コードは circuit 側の拒否（<see cref="ForwarderMsiPlacementService"/>）と
/// 同じ <c>operation-auth-required</c> で統一する。
/// </para>
/// </remarks>
internal sealed class ForwarderMsiUploadAuthorizationAuditHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (!authorizeResult.Succeeded &&
            context.GetEndpoint()?.Metadata.GetMetadata<ForwarderMsiUploadOperationMetadata>() is { } operation)
        {
            // IAuditRecorder は Host 側の結線（テストハーネス等では未登録がありうる）——
            // 記録できない構成でも認可の拒否自体（既定ハンドラへの委譲）は変えない。
            var auditRecorder = context.RequestServices.GetService<IAuditRecorder>();
            if (auditRecorder is not null)
            {
                var timeProvider = context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
                var (scheme, principal) = AuditActorResolver.Resolve(context.User);
                var connection = AdminAuthenticationExtensions.IsLoopbackAdminConnection(context) ? "loopback" : "remote";

                await auditRecorder.RecordAsync(
                    new AuditEvent(
                        OccurredAt: timeProvider.GetUtcNow(),
                        Kind: AuditEventKind.ForwarderMsiUploadRejected,
                        RemoteAddress: context.Connection.RemoteIpAddress?.ToString(),
                        RemotePort: context.Connection.RemotePort,
                        ReachedListenerPort: context.Connection.LocalPort,
                        Detail: $"operation={operation.Operation} reason=operation-auth-required connection={connection}",
                        AuthenticationScheme: scheme,
                        AuthenticatedPrincipal: principal),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        await _defaultHandler.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);
    }
}
