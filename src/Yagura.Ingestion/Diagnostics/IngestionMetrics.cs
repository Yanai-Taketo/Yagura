using System.Diagnostics.Metrics;

namespace Yagura.Ingestion.Diagnostics;

/// <summary>
/// パイプラインの計測点（architecture.md §4.1 発生箇所別ドロップカウンタ。ADR-0002 決定 4）。
/// </summary>
/// <remarks>
/// <para>
/// M2 では architecture.md §4.1 の表にある 8 種のカウンタのうち「内部バッファ破棄
/// （Q1 UDP 溢れ）」のみを実装する。残り 7 種（流量制御破棄・スプール退避・
/// スプール書込失敗・スプール破棄・永続化失敗・解析失敗（保存済み）・TCP 系）は
/// スプール・流量制御判定・TCP 受信が実装される M4 以降で追加する。
/// </para>
/// <para>
/// Meter 名・計器名は M2 時点の暫定であり、後続（M4 でのスプール・流量制御追加時）に
/// 命名規則ごと見直す前提とする。
/// </para>
/// </remarks>
public sealed class IngestionMetrics : IDisposable
{
    /// <summary>
    /// このコンポーネントが使用する <see cref="Meter"/> の名前。
    /// </summary>
    public const string MeterName = "Yagura.Ingestion";

    private readonly Meter _meter;
    private readonly Counter<long> _internalBufferDropped;

    public IngestionMetrics()
    {
        _meter = new Meter(MeterName);

        // architecture.md §4.1「内部バッファ破棄」: Q1（UDP 由来）が満杯で解析段へ渡せず
        // 破棄した件数。名称は暫定（M4 で流量制御破棄等を追加する際に見直す）。
        _internalBufferDropped = _meter.CreateCounter<long>(
            "yagura.ingestion.internal_buffer.dropped",
            unit: "{datagram}",
            description: "Q1 (UDP 由来) が満杯のため解析段へ渡せず破棄したデータグラム数。");
    }

    /// <summary>
    /// Q1（UDP 由来）の溢れによる破棄を 1 件計上する。
    /// </summary>
    public void RecordInternalBufferDropped() => _internalBufferDropped.Add(1);

    /// <summary>
    /// 内部バッファ破棄カウンタの計器そのもの。テストで
    /// <c>Microsoft.Extensions.Diagnostics.Metrics.Testing.MetricCollector&lt;long&gt;</c>
    /// に直接束縛するために公開する（Meter 名・計器名の文字列一致より頑健なため）。
    /// </summary>
    public Counter<long> InternalBufferDroppedCounter => _internalBufferDropped;

    public void Dispose() => _meter.Dispose();
}
