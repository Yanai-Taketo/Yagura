using System.Security.Claims;
using Yagura.Abstractions.Auditing;
using Yagura.Web.Administration;
using Yagura.Web.ForwarderKit;

namespace Yagura.Web.Tests.Administration;

/// <summary>
/// アップロード系操作の専用認可（ADR-0021 決定 1）の circuit 側最終防衛線
/// （<see cref="ForwarderMsiPlacementService"/>）と判定関数の単体テスト（委任 1 の回帰テスト①②）。
/// </summary>
public sealed class ForwarderMsiPlacementServiceAuthorizationTests
{
    // ---- 判定関数（HTTP 側ポリシー・circuit 側で共有する単一実装）の真理値表 ----

    [Fact]
    public void IsForwarderMsiUploadOperationAllowed_AdminSession_ReturnsTrue()
    {
        var admin = BuildPrincipal(authenticated: true, AdminAuthenticationExtensions.AdminSessionClaimType);

        Assert.True(AdminAuthenticationExtensions.IsForwarderMsiUploadOperationAllowed(admin));
    }

    [Fact]
    public void IsForwarderMsiUploadOperationAllowed_UnauthenticatedImplicitLoopbackPrincipal_ReturnsFalse()
    {
        // 無認証 loopback の暗黙プリンシパル（IsAuthenticated = false）は通さない——
        // AdminPolicy と異なり loopback バイパス条項が存在しない（ADR-0021 決定 1）。
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.False(AdminAuthenticationExtensions.IsForwarderMsiUploadOperationAllowed(anonymous));
    }

    [Fact]
    public void IsForwarderMsiUploadOperationAllowed_ViewerSession_ReturnsFalse()
    {
        // 閲覧セッションは通さない——書き込み口につながる操作は管理役割のみ
        // （AuditActorResolver は閲覧セッションでも操作者名を解決するため、
        // Principal 非 null での代用は誤り。ForwarderMsiOperatorContext の doc 参照）。
        var viewer = BuildPrincipal(authenticated: true, AdminAuthenticationExtensions.ViewerSessionClaimType);

        Assert.False(AdminAuthenticationExtensions.IsForwarderMsiUploadOperationAllowed(viewer));
    }

    [Fact]
    public void IsForwarderMsiUploadOperationAllowed_AuthenticatedWithoutSessionClaim_ReturnsFalse()
    {
        // 標識クレーム欠落は fail-closed（IsAdminSessionAuthenticated と同じ）。
        var authenticatedNoClaim = BuildPrincipal(authenticated: true, claimType: null);

        Assert.False(AdminAuthenticationExtensions.IsForwarderMsiUploadOperationAllowed(authenticatedNoClaim));
    }

    // ---- circuit 側の最終防衛線（未認証の操作は実行せず拒否 + 監査 3014） ----

    [Fact]
    public async Task CommitAsync_UnauthenticatedOperator_RejectsWithoutTouchingStoreAndAudits()
    {
        var store = new ThrowingForwarderMsiStore();
        var audit = new RecordingAuditRecorder();
        var service = new ForwarderMsiPlacementService(store, audit, TimeProvider.System);

        var result = await service.CommitAsync(
            "token", versionMismatchAcknowledged: true, replaceAcknowledged: true,
            ForwarderMsiArchitecture.Win64, UnauthenticatedContext());

        Assert.False(result.Success);
        Assert.Equal(ForwarderMsiCommitError.OperationNotAuthenticated, result.Error);
        var recorded = Assert.Single(audit.Recorded);
        Assert.Equal(AuditEventKind.ForwarderMsiUploadRejected, recorded.Kind);
        Assert.Contains("operation=commit", recorded.Detail);
        Assert.Contains("reason=operation-auth-required", recorded.Detail);
    }

    [Fact]
    public async Task DiscardAsync_UnauthenticatedOperator_RejectsWithoutTouchingStoreAndAudits()
    {
        var store = new ThrowingForwarderMsiStore();
        var audit = new RecordingAuditRecorder();
        var service = new ForwarderMsiPlacementService(store, audit, TimeProvider.System);

        var result = await service.DiscardAsync("token", ForwarderMsiArchitecture.Win64, UnauthenticatedContext());

        Assert.False(result.Found);
        var recorded = Assert.Single(audit.Recorded);
        Assert.Contains("operation=discard", recorded.Detail);
        Assert.Contains("reason=operation-auth-required", recorded.Detail);
    }

    [Fact]
    public async Task DeleteAsync_UnauthenticatedOperator_RejectsWithoutTouchingStoreAndAudits()
    {
        var store = new ThrowingForwarderMsiStore();
        var audit = new RecordingAuditRecorder();
        var service = new ForwarderMsiPlacementService(store, audit, TimeProvider.System);

        var result = await service.DeleteAsync(
            ForwarderMsiArchitecture.Win64, "0123abcd", UnauthenticatedContext());

        Assert.False(result.Success);
        Assert.Equal(ForwarderMsiDeleteError.OperationNotAuthenticated, result.Error);
        var recorded = Assert.Single(audit.Recorded);
        Assert.Contains("operation=delete", recorded.Detail);
        Assert.Contains("reason=operation-auth-required", recorded.Detail);
    }

    [Fact]
    public async Task DeleteAsync_AuthenticatedOperator_ReachesStore()
    {
        // OperationAuthenticated = true なら従来どおりストアへ到達する（拒否の判定が
        // 認証済み操作へ波及しないことの対照テスト）。
        var store = new ThrowingForwarderMsiStore();
        var audit = new RecordingAuditRecorder();
        var service = new ForwarderMsiPlacementService(store, audit, TimeProvider.System);

        await Assert.ThrowsAsync<NotSupportedException>(() => service.DeleteAsync(
            ForwarderMsiArchitecture.Win64, "0123abcd", AuthenticatedContext()));
    }

    private static ForwarderMsiOperatorContext UnauthenticatedContext() =>
        new(RemoteAddress: "127.0.0.1", IsLoopback: true, Scheme: null, Principal: null,
            OperationAuthenticated: false);

    private static ForwarderMsiOperatorContext AuthenticatedContext() =>
        new(RemoteAddress: "127.0.0.1", IsLoopback: true, Scheme: "app", Principal: "app:admin1",
            OperationAuthenticated: true);

    private static ClaimsPrincipal BuildPrincipal(bool authenticated, string? claimType)
    {
        var identity = authenticated
            ? new ClaimsIdentity(
                claimType is null ? [] : [new Claim(claimType, "1")],
                authenticationType: "Cookies")
            : new ClaimsIdentity();
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// 触られたら失敗するストア——未認証の拒否がストア到達前に成立することを構造的に検証する。
    /// </summary>
    private sealed class ThrowingForwarderMsiStore : IForwarderMsiStore
    {
        public string FolderPath => @"C:\unused";

        public ForwarderMsiWriteAccess CheckWriteAccess() => throw new NotSupportedException();

        public int CleanupStagingFiles() => throw new NotSupportedException();

        public Task<ForwarderMsiStageResult> StageAsync(
            ForwarderMsiArchitecture architecture, Stream content, long? declaredLength, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ForwarderMsiCommitResult Commit(string stagingToken, bool versionMismatchAcknowledged, bool replaceAcknowledged)
            => throw new NotSupportedException();

        public ForwarderMsiDiscardResult Discard(string stagingToken) => throw new NotSupportedException();

        public ForwarderMsiDeleteResult Delete(ForwarderMsiArchitecture architecture, string expectedSha256)
            => throw new NotSupportedException();
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
