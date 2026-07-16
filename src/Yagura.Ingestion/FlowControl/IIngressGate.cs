using System.Net;

namespace Yagura.Ingestion.FlowControl;

/// <summary>
/// 流量制御の挿入点（architecture.md §3.3）。受信段の直後・Q1 投入前に置く
/// （送信元の判別に解析は不要なため、解析より前に置いて解析コストを浪費させない）。
/// </summary>
/// <remarks>
/// 実装は 2 つ: 送信元単位の token bucket による判定・破棄を行う
/// <see cref="TokenBucketIngressGate"/>（既定有効。ADR-0002 決定 2。Issue #260）と、
/// 無条件で通す <see cref="NoopIngressGate"/>（<c>Ingestion:FlowControl:Enabled = false</c> の
/// opt-out 構成用）。どちらを結線するかはホスト（<c>Yagura.Host.Program</c>）が設定から選ぶ。
/// </remarks>
public interface IIngressGate
{
    /// <summary>
    /// データグラムを Q1 へ投入してよいか判定する。
    /// </summary>
    /// <param name="sourceAddress">送信元アドレス。</param>
    /// <param name="payload">受信したバイト列。</param>
    /// <returns><c>true</c> なら Q1 への投入を続行する。<c>false</c> なら破棄する（破棄の計上は呼び出し元の責務——「発火は必ず計測される」architecture.md §3.3）。</returns>
    bool ShouldAdmit(IPAddress sourceAddress, ReadOnlySpan<byte> payload);
}
