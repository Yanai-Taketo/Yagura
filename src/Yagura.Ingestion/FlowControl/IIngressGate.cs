using System.Net;

namespace Yagura.Ingestion.FlowControl;

/// <summary>
/// 流量制御の挿入点（architecture.md §3.3）。受信段の直後・Q1 投入前に置く
/// （送信元の判別に解析は不要なため、解析より前に置いて解析コストを浪費させない）。
/// </summary>
/// <remarks>
/// v0.1 は挿入点とカウンタの枠のみを設ける段階であり、判定・破棄は実装しない
/// （<see cref="NoopIngressGate"/> が唯一の実装）。送信元単位の token bucket 等の
/// 実装は実装設計時に確定する（architecture.md M-4）。
/// </remarks>
public interface IIngressGate
{
    /// <summary>
    /// データグラムを Q1 へ投入してよいか判定する。
    /// </summary>
    /// <param name="sourceAddress">送信元アドレス。</param>
    /// <param name="payload">受信したバイト列。</param>
    /// <returns><c>true</c> なら Q1 への投入を続行する。<c>false</c> なら破棄する（v0.1 では常に <c>true</c>）。</returns>
    bool ShouldAdmit(IPAddress sourceAddress, ReadOnlySpan<byte> payload);
}
