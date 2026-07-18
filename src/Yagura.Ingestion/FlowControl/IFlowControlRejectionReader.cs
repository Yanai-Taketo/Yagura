using System.Net;

namespace Yagura.Ingestion.FlowControl;

/// <summary>
/// 流量制御の送信元別の拒否状況の読み取り口（Issue #288。2026-07-18 オーナー裁定——案 (a)
/// カード型）。「どの送信元が制限に達しているか」をダッシュボードへ公開するための
/// 読み取り専用契約。
/// </summary>
/// <remarks>
/// <para>
/// 拒否カウントはゲート内部の送信元別バケット（#284 で有界管理——スイープ・追跡上限——を
/// 設計済み）に併せて保持する。既存の有界化設計に相乗りすることで、可視化のために新たな
/// 無限成長点を作らない（裁定の論点 1 の解）。帰結として<b>カウントの生存期間はバケットと
/// 同じ</b>——制限なく受信できる状態が続いた送信元はスイープでバケットごと消え、
/// 設定変更によるゲート差し替え（<see cref="SwappableIngressGate.Swap"/>）でも消える。
/// サービス起動からの破棄総数は従来どおり計器
/// <c>yagura.ingestion.flow_control.dropped</c>（architecture.md §4.1）が持つ。
/// </para>
/// </remarks>
public interface IFlowControlRejectionReader
{
    /// <summary>
    /// 拒否が発生している送信元を拒否数の多い順に返す（最大 <paramref name="maxCount"/> 件。
    /// 1 未満は空）。判定のホットパスを止めない軽量なスナップショット読み取りであること。
    /// </summary>
    IReadOnlyList<FlowControlRejectedSource> SnapshotRejectedSources(int maxCount);
}

/// <summary>拒否が発生している送信元 1 つぶんの読み取り値。</summary>
/// <param name="SourceAddress">送信元アドレス。</param>
/// <param name="RejectedCount">
/// この送信元の拒否（破棄）件数。バケット生成からの累計であり、サービス起動からの累計ではない
/// （<see cref="IFlowControlRejectionReader"/> remarks 参照）。
/// </param>
public sealed record FlowControlRejectedSource(IPAddress SourceAddress, long RejectedCount);
