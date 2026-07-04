using System.Net;

namespace Yagura.Ingestion.FlowControl;

/// <summary>
/// v0.1 の既定実装。全データグラムを無条件で通過させる（architecture.md §3.3）。
/// </summary>
/// <remarks>
/// 判定・破棄ロジックは持たない。流量制御の実装（token bucket 等）は実装設計時に
/// 別クラスとして追加し、既定を opt-out（既定有効）にする方針は architecture.md §3.3 のとおり。
/// </remarks>
public sealed class NoopIngressGate : IIngressGate
{
    /// <inheritdoc />
    public bool ShouldAdmit(IPAddress sourceAddress, ReadOnlySpan<byte> payload) => true;
}
