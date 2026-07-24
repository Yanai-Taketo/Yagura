using Yagura.Abstractions.Auditing;
using Yagura.Web.ForwarderKit;

namespace Yagura.Web.Administration;

/// <summary>
/// フォワーダ MSI 配置の確定・破棄・削除と監査記録をまとめる circuit 向けサービス
/// （ADR-0020 決定 3・4。<see cref="ICircuitManagementService"/>——2004——と同じ
/// 「操作の実体 + 監査記録」の役割分担。ファイル本文を運ぶ stage だけが HTTP エンドポイント
/// ——<see cref="ForwarderMsiUploadEndpoints"/>——で、確認後の操作は circuit から本サービスを呼ぶ）。
/// </summary>
public interface IForwarderMsiPlacementService
{
    /// <summary>確認済みステージングの配置確定（成功 = 監査 2026 / 失敗 = 監査 3014）。</summary>
    Task<ForwarderMsiCommitResult> CommitAsync(
        string stagingToken,
        bool versionMismatchAcknowledged,
        bool replaceAcknowledged,
        ForwarderMsiArchitecture architecture,
        ForwarderMsiOperatorContext operatorContext);

    /// <summary>ステージングの破棄（利用者の中止。監査 3014——中止も記録する。ADR-0020 決定 4）。</summary>
    Task<ForwarderMsiDiscardResult> DiscardAsync(
        string stagingToken,
        ForwarderMsiArchitecture architecture,
        ForwarderMsiOperatorContext operatorContext);

    /// <summary>配置済み MSI の削除（成功 = 監査 2027 / 失敗 = 監査 3014）。</summary>
    Task<ForwarderMsiDeleteResult> DeleteAsync(
        ForwarderMsiArchitecture architecture,
        string expectedSha256,
        ForwarderMsiOperatorContext operatorContext);
}

/// <summary>
/// circuit 上の操作者情報（画面が <c>YaguraCircuitContext</c> と
/// <c>YaguraCircuitAuthenticationStateProvider</c> から解決して渡す——<c>AdminRemoteAccessScreen</c> と
/// 同じパターン）。本機能の前提条件（ADR-0020 決定 1——全経路認証必須）により
/// <paramref name="Scheme"/>/<paramref name="Principal"/> には実運用で必ず値が入る。
/// </summary>
/// <param name="RemoteAddress">circuit 確立時の接続元アドレス。</param>
/// <param name="IsLoopback">loopback 束縛ポート経由か（<see langword="null"/> = 帰属不明）。</param>
/// <param name="Scheme">認証方式（<c>windows</c>/<c>app</c>）。</param>
/// <param name="Principal">認証済み利用者名。</param>
public sealed record ForwarderMsiOperatorContext(
    string? RemoteAddress,
    bool? IsLoopback,
    string? Scheme,
    string? Principal);

/// <inheritdoc cref="IForwarderMsiPlacementService"/>
internal sealed class ForwarderMsiPlacementService : IForwarderMsiPlacementService
{
    private readonly IForwarderMsiStore _store;
    private readonly IAuditRecorder _auditRecorder;
    private readonly TimeProvider _timeProvider;

    public ForwarderMsiPlacementService(IForwarderMsiStore store, IAuditRecorder auditRecorder, TimeProvider timeProvider)
    {
        _store = store;
        _auditRecorder = auditRecorder;
        _timeProvider = timeProvider;
    }

    public async Task<ForwarderMsiCommitResult> CommitAsync(
        string stagingToken,
        bool versionMismatchAcknowledged,
        bool replaceAcknowledged,
        ForwarderMsiArchitecture architecture,
        ForwarderMsiOperatorContext operatorContext)
    {
        var result = _store.Commit(stagingToken, versionMismatchAcknowledged, replaceAcknowledged);
        if (result.Success)
        {
            // 記録内容は ADR-0020 決定 4 のとおり: アーキ・格納ファイル名・版・SHA256・サイズ・
            // officialHashMatch・versionMismatchAcknowledged・置換時の旧 SHA256・loopback/リモートの別。
            var detail =
                $"operation=place architecture={ForwarderMsiUploadEndpoints.FormatArchitecture(architecture)}" +
                $" fileName={result.FinalFileName} productVersion={result.ProductVersion}" +
                $" sha256={result.Sha256} length={result.Length}" +
                $" officialHashMatch={result.OfficialHashMatch}" +
                $" versionMismatch={FormatBool(result.VersionMismatch)}" +
                $" versionMismatchAcknowledged={FormatBool(result.VersionMismatchAcknowledged)}" +
                (result.ReplacedSha256 is not null ? $" replacedSha256={result.ReplacedSha256}" : string.Empty) +
                $" connection={FormatConnection(operatorContext)}";
            await RecordAsync(AuditEventKind.ForwarderMsiPlaced, detail, operatorContext).ConfigureAwait(false);
        }
        else
        {
            await RecordRejectionAsync(
                "commit", architecture, FormatCommitError(result.Error!.Value), operatorContext).ConfigureAwait(false);
        }

        return result;
    }

    public async Task<ForwarderMsiDiscardResult> DiscardAsync(
        string stagingToken,
        ForwarderMsiArchitecture architecture,
        ForwarderMsiOperatorContext operatorContext)
    {
        var result = _store.Discard(stagingToken);
        if (result.Found)
        {
            // 二段階確認の拒否（利用者の中止）も監査に残す（ADR-0020 決定 4。「言った・言わない」を
            // 作らない——確認画面まで進んで中止した事実が事後追跡できる）。
            await RecordRejectionAsync(
                "discard", architecture, "cancelled-by-user",
                operatorContext,
                $" sha256={result.Sha256} productVersion={result.ProductVersion}").ConfigureAwait(false);
        }

        return result;
    }

    public async Task<ForwarderMsiDeleteResult> DeleteAsync(
        ForwarderMsiArchitecture architecture,
        string expectedSha256,
        ForwarderMsiOperatorContext operatorContext)
    {
        var result = _store.Delete(architecture, expectedSha256);
        if (result.Success)
        {
            var detail =
                $"operation=delete architecture={ForwarderMsiUploadEndpoints.FormatArchitecture(architecture)}" +
                $" fileName={result.DeletedFileName} deletedSha256={result.DeletedSha256}" +
                $" connection={FormatConnection(operatorContext)}";
            await RecordAsync(AuditEventKind.ForwarderMsiDeleted, detail, operatorContext).ConfigureAwait(false);
        }
        else
        {
            await RecordRejectionAsync(
                "delete", architecture, FormatDeleteError(result.Error!.Value), operatorContext).ConfigureAwait(false);
        }

        return result;
    }

    private Task RecordRejectionAsync(
        string operation,
        ForwarderMsiArchitecture architecture,
        string reason,
        ForwarderMsiOperatorContext operatorContext,
        string detailSuffix = "")
    {
        var detail =
            $"operation={operation} architecture={ForwarderMsiUploadEndpoints.FormatArchitecture(architecture)}" +
            $" reason={reason} connection={FormatConnection(operatorContext)}{detailSuffix}";
        return RecordAsync(AuditEventKind.ForwarderMsiUploadRejected, detail, operatorContext);
    }

    private Task RecordAsync(AuditEventKind kind, string detail, ForwarderMsiOperatorContext operatorContext) =>
        _auditRecorder.RecordAsync(
            new AuditEvent(
                OccurredAt: _timeProvider.GetUtcNow(),
                Kind: kind,
                RemoteAddress: operatorContext.RemoteAddress,
                RemotePort: null,
                Detail: detail,
                AuthenticationScheme: operatorContext.Scheme,
                AuthenticatedPrincipal: operatorContext.Principal),
            CancellationToken.None);

    private static string FormatConnection(ForwarderMsiOperatorContext operatorContext) =>
        operatorContext.IsLoopback switch
        {
            true => "loopback",
            false => "remote",
            null => "unknown",
        };

    private static string FormatBool(bool value) => value ? "true" : "false";

    private static string FormatCommitError(ForwarderMsiCommitError error) => error switch
    {
        ForwarderMsiCommitError.UnknownStagingToken => "unknown-staging-token",
        ForwarderMsiCommitError.VersionMismatchNotAcknowledged => "version-mismatch-not-acknowledged",
        ForwarderMsiCommitError.ReplaceNotAcknowledged => "replace-not-acknowledged",
        ForwarderMsiCommitError.FolderStateChanged => "folder-state-changed",
        ForwarderMsiCommitError.WriteFailed => "write-failed",
        _ => "unknown",
    };

    private static string FormatDeleteError(ForwarderMsiDeleteError error) => error switch
    {
        ForwarderMsiDeleteError.NotFound => "not-found",
        ForwarderMsiDeleteError.MultipleExistingFiles => "multiple-existing-files",
        ForwarderMsiDeleteError.Sha256Mismatch => "sha256-mismatch",
        ForwarderMsiDeleteError.WriteFailed => "write-failed",
        _ => "unknown",
    };
}
