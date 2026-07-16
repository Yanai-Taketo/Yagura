using System.Net;

namespace Yagura.Ingestion.FlowControl;

/// <summary>
/// 全データグラムを無条件で通過させる実装（architecture.md §3.3）。
/// </summary>
/// <remarks>
/// 流量制御を opt-out（<c>Ingestion:FlowControl:Enabled = false</c>）した構成、および
/// 流量制御を伴わないテスト・ベンチの既定として使う。既定有効の本実装は
/// <see cref="TokenBucketIngressGate"/>（Issue #260）。
/// </remarks>
public sealed class NoopIngressGate : IIngressGate
{
    /// <inheritdoc />
    public bool ShouldAdmit(IPAddress sourceAddress, ReadOnlySpan<byte> payload) => true;
}
