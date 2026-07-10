using Yagura.Abstractions.Auditing;
using Yagura.Web.Administration;
using Yagura.Web.Circuits;

namespace Yagura.Web.Tests.Circuits;

/// <summary>
/// <see cref="CircuitManagementService"/> の単体テスト（M8-4。Issue #71。security.md §2.2——
/// 一覧の内容と、個別切断が管理操作として監査記録（2000 番台 ID 2004）されること）。
/// </summary>
public sealed class CircuitManagementServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 6, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public void ListCircuits_ReturnsRemoteEstablishedAndLastActivity()
    {
        var registry = new CircuitRegistry();
        var record = new CircuitRecord("c1", "127.0.0.1", T0, new YaguraCircuitContext { IsAdminListener = true });
        registry.Register(record);
        registry.RecordActivity("c1", T0 + TimeSpan.FromMinutes(5));

        var service = new CircuitManagementService(registry, new RecordingAuditRecorder());

        var circuit = Assert.Single(service.ListCircuits());
        Assert.Equal("c1", circuit.CircuitId);
        Assert.Equal("127.0.0.1", circuit.RemoteAddress);
        Assert.True(circuit.IsAdminListener);
        Assert.Equal(T0, circuit.OpenedAt);
        Assert.Equal(T0 + TimeSpan.FromMinutes(5), circuit.LastActivityAt);
    }

    [Fact]
    public async Task Disconnect_Accepted_RecordsAuditEvent()
    {
        var registry = new CircuitRegistry();
        var record = new CircuitRecord("c1", "10.0.0.5", T0, new YaguraCircuitContext { IsAdminListener = false });
        registry.Register(record);

        string? deliveredReason = null;
        record.Context.TerminationRequested += reason => { deliveredReason = reason; return Task.CompletedTask; };

        var audit = new RecordingAuditRecorder();
        var service = new CircuitManagementService(registry, audit);

        var accepted = await service.DisconnectAsync("c1", operatorAddress: "127.0.0.1");

        Assert.True(accepted);
        Assert.Equal(CircuitTerminationReasons.DisconnectedByAdministrator, deliveredReason);

        var recorded = Assert.Single(audit.RecordedEvents);
        Assert.Equal(AuditEventKind.CircuitDisconnected, recorded.Kind);
        Assert.Equal("127.0.0.1", recorded.RemoteAddress);
        Assert.Contains("c1", recorded.Detail);
    }

    [Fact]
    public async Task Disconnect_NotAccepted_DoesNotRecordAudit()
    {
        // 実行されなかった操作は監査記録しない（対象が既に終了している等）。
        var registry = new CircuitRegistry();
        var audit = new RecordingAuditRecorder();
        var service = new CircuitManagementService(registry, audit);

        var accepted = await service.DisconnectAsync("missing");

        Assert.False(accepted);
        Assert.Empty(audit.RecordedEvents);
    }

    private sealed class RecordingAuditRecorder : IAuditRecorder
    {
        public List<AuditEvent> RecordedEvents { get; } = [];

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            RecordedEvents.Add(auditEvent);
            return Task.CompletedTask;
        }
    }
}
