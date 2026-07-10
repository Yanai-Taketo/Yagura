using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Web.Circuits;

namespace Yagura.Web.Administration;

/// <summary>
/// circuit 管理（一覧・個別切断）の実装（security.md §2.2。M8-4。Issue #71）。
/// </summary>
/// <remarks>
/// <para>
/// 書き込み系サービス（<see cref="IYaguraWriteService"/> 実装 = <see cref="ICircuitManagementService"/>
/// 経由）であり、閲覧リスナ側のコンポーネント（<c>Yagura.Web.Components</c> 名前空間）から
/// 参照してはならない（security.md §1 L-5 の参照分離検査
/// <c>ViewerComponentReferenceIsolationTests</c> が機械検証する）。利用は管理画面
/// （<c>Yagura.Web.Administration.Screens</c>）に限る。
/// </para>
/// <para>
/// 個別切断は管理操作として監査記録（2000 番台 = ID 2004）の対象（security.md §2.2・§4.1）。
/// 切断の実行自体は circuit 側の協調動作（<see cref="YaguraCircuitContext"/> の remarks 参照）。
/// </para>
/// </remarks>
public sealed class CircuitManagementService : ICircuitManagementService
{
    private readonly CircuitRegistry _registry;
    private readonly IAuditRecorder _auditRecorder;
    private readonly TimeProvider _timeProvider;

    public CircuitManagementService(
        CircuitRegistry registry,
        IAuditRecorder auditRecorder,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(auditRecorder);

        _registry = registry;
        _auditRecorder = auditRecorder;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public IReadOnlyList<CircuitInfo> ListCircuits() =>
        _registry.Snapshot()
            .OrderBy(record => record.OpenedAt)
            .Select(record => new CircuitInfo(
                record.CircuitId,
                record.RemoteAddress,
                record.IsAdminListener,
                record.OpenedAt,
                record.LastActivityAt))
            .ToList();

    /// <inheritdoc/>
    public async Task<bool> DisconnectAsync(
        string circuitId,
        string? operatorAddress = null,
        string? operatorScheme = null,
        string? operatorPrincipal = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(circuitId);

        var accepted = await _registry.RequestDisconnectAsync(
            circuitId,
            CircuitTerminationReasons.DisconnectedByAdministrator).ConfigureAwait(false);

        if (!accepted)
        {
            // 対象が既に存在しない・切断要求を受理できなかった場合は「実行された操作」ではない
            // ため監査記録しない（呼び出し画面が結果を利用者へ表示する）。
            return false;
        }

        await _auditRecorder.RecordAsync(
            new AuditEvent(
                OccurredAt: _timeProvider.GetUtcNow(),
                Kind: AuditEventKind.CircuitDisconnected,
                RemoteAddress: operatorAddress,
                RemotePort: null,
                Detail: $"circuitId={circuitId}",
                AuthenticationScheme: operatorScheme,
                AuthenticatedPrincipal: operatorPrincipal),
            CancellationToken.None).ConfigureAwait(false);

        return true;
    }
}
