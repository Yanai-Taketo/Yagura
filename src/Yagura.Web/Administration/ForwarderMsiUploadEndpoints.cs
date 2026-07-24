using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yagura.Abstractions.Auditing;
using Yagura.Web.ForwarderKit;

namespace Yagura.Web.Administration;

/// <summary>
/// フォワーダ MSI アップロードの HTTP エンドポイント（ADR-0020 決定 1・3。配置経路 (b)）。
/// </summary>
/// <remarks>
/// <para>
/// <b>登録は機能有効時のみ</b>（ADR-0020 決定 1「エンドポイントの構造的非存在」）:
/// <see cref="YaguraAdminExtensions.MapYaguraAdmin"/> が
/// <c>Admin:ForwarderKit:MsiUpload:Enabled</c> の実効値に応じて本クラスの登録自体を省略する
/// （<see cref="AdminAuthEndpoints.MapAdminAuthEndpoints"/> の Windows 認証経路と同じ先例）。
/// 条件不成立の構成では拒否応答すら返す口が存在しない（404）。
/// </para>
/// <para>
/// <b>HTTP なのはファイル転送（stage）のみ</b>: アップロード本文は SignalR circuit の RPC を
/// 経由してはならない（ADR-0020 決定 3——circuit のメッセージサイズ上限の緩和は禁止）ため、
/// ブラウザから <c>fetch</c> で直接 POST する専用エンドポイントで受ける。確認後の確定
/// （commit）・破棄・削除はファイル本文を運ばないため、Blazor circuit から
/// <see cref="IForwarderMsiStore"/> を直接呼ぶ（circuit 発の管理操作の監査は 2004 の先例）。
/// </para>
/// <para>
/// <b>CSRF 対策</b>: stage は認証セッション Cookie で保護された状態変更操作であり、
/// <see cref="IAntiforgery.ValidateRequestAsync"/> で明示検証する（AdminAuthEndpoints と同じ判断）。
/// 本文が <c>application/octet-stream</c> のためトークンはフォームではなくヘッダー
/// （<c>RequestVerificationToken</c>）で送る——トークンはトークン配布エンドポイント
/// （<c>GET …/antiforgery</c>。副作用なし・認可必須）から取得する。
/// </para>
/// </remarks>
internal static class ForwarderMsiUploadEndpoints
{
    /// <summary>アップロード関連エンドポイントを登録する（機能有効時のみ呼ばれる）。</summary>
    public static void MapForwarderMsiUploadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        MapAntiforgeryToken(endpoints);
        MapStage(endpoints);
    }

    private static void MapAntiforgeryToken(IEndpointRouteBuilder endpoints)
    {
        var endpoint = endpoints.MapGet("/admin/forwarder-kit/msi/antiforgery", (
            HttpContext context,
            IAntiforgery antiforgery) =>
        {
            // GetAndStoreTokens は antiforgery Cookie を応答へ設定し、対応する要求トークンを返す
            // （副作用はトークン Cookie の設定のみ。状態変更なし）。
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Json(new { token = tokens.RequestToken, headerName = tokens.HeaderName ?? "RequestVerificationToken" });
        });

        endpoint
            .WithMetadata(ListenerPortGuardEndpointMetadata.Admin)
            .RequireAuthorization(AdminAuthenticationExtensions.AdminPolicyName);
    }

    private static void MapStage(IEndpointRouteBuilder endpoints)
    {
        var endpoint = endpoints.MapPost("/admin/forwarder-kit/msi/stage", async (
            HttpContext context,
            string? architecture,
            IAntiforgery antiforgery,
            IForwarderMsiStore store,
            IAuditRecorder auditRecorder,
            TimeProvider timeProvider) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
            }
            catch (AntiforgeryValidationException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = "csrf" }).ConfigureAwait(false);
                return;
            }

            if (!TryParseArchitecture(architecture, out var msiArchitecture))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = "invalid-architecture" }).ConfigureAwait(false);
                return;
            }

            // --- Content-Length 申告の事前拒否（本文を読む前。ADR-0020 決定 3） ---
            var declaredLength = context.Request.ContentLength;
            if (declaredLength is > ForwarderMsiUploadConstraints.MaxUploadBytes)
            {
                await RecordRejectionAsync(
                    auditRecorder, timeProvider, context, msiArchitecture,
                    "declared-length-exceeds-limit", $"declaredLength={declaredLength}").ConfigureAwait(false);
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await context.Response.WriteAsJsonAsync(new { error = "declared-length-exceeds-limit" }).ConfigureAwait(false);
                return;
            }

            var result = await store.StageAsync(
                msiArchitecture, context.Request.Body, declaredLength, context.RequestAborted).ConfigureAwait(false);

            if (!result.Success)
            {
                var reason = FormatStageError(result.Error!.Value);
                // 中断（クライアント切断）も含め、失敗・拒否はすべて監査に残す（ADR-0004 決定 7・
                // ADR-0020 決定 4）。CancellationToken.None: 切断済みでも記録は打ち切らない。
                await RecordRejectionAsync(
                    auditRecorder, timeProvider, context, msiArchitecture, reason, detailSuffix: null)
                    .ConfigureAwait(false);

                if (result.Error == ForwarderMsiStageError.Cancelled)
                {
                    return; // クライアントは既にいない。応答は書かない。
                }

                context.Response.StatusCode = result.Error switch
                {
                    ForwarderMsiStageError.AnotherUploadInProgress => StatusCodes.Status409Conflict,
                    ForwarderMsiStageError.StreamExceedsLimit => StatusCodes.Status413PayloadTooLarge,
                    ForwarderMsiStageError.InsufficientDiskSpace => StatusCodes.Status507InsufficientStorage,
                    _ => StatusCodes.Status400BadRequest,
                };
                await context.Response.WriteAsJsonAsync(new { error = reason }).ConfigureAwait(false);
                return;
            }

            // stage 成功自体は監査しない——配置の確定（commit → 2026）で記録する。中止（discard）・
            // 確認不整合は circuit 側の操作として 3014 に記録される（ADR-0020 決定 4）。
            await context.Response.WriteAsJsonAsync(new
            {
                stagingToken = result.StagingToken,
                finalFileName = result.FinalFileName,
                productVersion = result.ProductVersion,
                sha256 = result.Sha256,
                length = result.Length,
                officialHashMatch = result.OfficialHashMatch.ToString(),
                versionMismatch = result.VersionMismatch,
                existingFileName = result.ExistingFileName,
                existingSha256 = result.ExistingSha256,
            }).ConfigureAwait(false);
        });

        endpoint
            .WithMetadata(ListenerPortGuardEndpointMetadata.Admin)
            .RequireAuthorization(AdminAuthenticationExtensions.AdminPolicyName);
    }

    private static async Task RecordRejectionAsync(
        IAuditRecorder auditRecorder,
        TimeProvider timeProvider,
        HttpContext context,
        ForwarderMsiArchitecture architecture,
        string reason,
        string? detailSuffix)
    {
        var (scheme, principal) = AuditActorResolver.Resolve(context.User);
        var connection = AdminAuthenticationExtensions.IsLoopbackAdminConnection(context) ? "loopback" : "remote";
        var detail = $"operation=stage architecture={FormatArchitecture(architecture)} reason={reason} connection={connection}";
        if (detailSuffix is not null)
        {
            detail += " " + detailSuffix;
        }

        await auditRecorder.RecordAsync(
            new AuditEvent(
                OccurredAt: timeProvider.GetUtcNow(),
                Kind: AuditEventKind.ForwarderMsiUploadRejected,
                RemoteAddress: context.Connection.RemoteIpAddress?.ToString(),
                RemotePort: context.Connection.RemotePort,
                ReachedListenerPort: context.Connection.LocalPort,
                Detail: detail,
                AuthenticationScheme: scheme,
                AuthenticatedPrincipal: principal),
            CancellationToken.None).ConfigureAwait(false);
    }

    internal static string FormatArchitecture(ForwarderMsiArchitecture architecture) =>
        architecture == ForwarderMsiArchitecture.WinArm64 ? "arm64" : "x64";

    internal static string FormatStageError(ForwarderMsiStageError error) => error switch
    {
        ForwarderMsiStageError.AnotherUploadInProgress => "another-upload-in-progress",
        ForwarderMsiStageError.DeclaredLengthExceedsLimit => "declared-length-exceeds-limit",
        ForwarderMsiStageError.StreamExceedsLimit => "stream-exceeds-limit",
        ForwarderMsiStageError.InsufficientDiskSpace => "insufficient-disk-space",
        ForwarderMsiStageError.WriteFailed => "write-failed",
        ForwarderMsiStageError.ProductVersionUnreadable => "product-version-unreadable",
        ForwarderMsiStageError.ProductVersionInvalid => "product-version-invalid",
        ForwarderMsiStageError.MultipleExistingFiles => "multiple-existing-files",
        ForwarderMsiStageError.Cancelled => "cancelled",
        _ => "unknown",
    };

    private static bool TryParseArchitecture(string? value, out ForwarderMsiArchitecture architecture)
    {
        architecture = ForwarderMsiArchitecture.Win64;
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "x64", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "arm64", StringComparison.OrdinalIgnoreCase))
        {
            architecture = ForwarderMsiArchitecture.WinArm64;
            return true;
        }

        return false;
    }
}
