using System.Diagnostics.Metrics;

namespace Yagura.Ingestion.Diagnostics;

/// <summary>
/// パイプラインの計測点（architecture.md §4.1 発生箇所別ドロップカウンタ。ADR-0002 決定 4）。
/// </summary>
/// <remarks>
/// <para>
/// M2 では architecture.md §4.1 の表にある 8 種のカウンタのうち「内部バッファ破棄
/// （Q1 UDP 溢れ）」のみを実装する。M4-1（TCP 受信）で「TCP 接続拒否」を追加した。
/// 残り（流量制御破棄・スプール退避・スプール書込失敗・スプール破棄・永続化失敗・
/// 解析失敗（保存済み）・TCP 接続断・TLS ハンドシェイク失敗・TCP 不完全メッセージ）は
/// スプール・流量制御判定・TLS 受信が実装される後続マイルストーンで追加する
/// （TCP 不完全メッセージは ParseStatus.Incomplete としてログレコード側に計上されるため、
/// カウンタとしての追加は §4.6 のゲージ整備と合わせて後続で検討する）。
/// </para>
/// <para>
/// Meter 名・計器名は M2 時点の暫定であり、後続（スプール・流量制御追加時）に
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
    private readonly Counter<long> _tcpConnectionRejected;

    public IngestionMetrics()
    {
        _meter = new Meter(MeterName);

        // architecture.md §4.1「内部バッファ破棄」: Q1（UDP 由来）が満杯で解析段へ渡せず
        // 破棄した件数。名称は暫定（後続でスプール・流量制御破棄等を追加する際に見直す）。
        _internalBufferDropped = _meter.CreateCounter<long>(
            "yagura.ingestion.internal_buffer.dropped",
            unit: "{datagram}",
            description: "Q1 (UDP 由来) が満杯のため解析段へ渡せず破棄したデータグラム数。");

        // architecture.md §4.1「TCP 接続拒否」: 同時接続数上限（§3.1・M-14）到達により
        // 新規接続を拒否した件数。既存接続を守るための有限化が働いていることの確認に使う。
        _tcpConnectionRejected = _meter.CreateCounter<long>(
            "yagura.ingestion.tcp_connection.rejected",
            unit: "{connection}",
            description: "TCP 同時接続数上限到達により拒否した新規接続数。");
    }

    /// <summary>
    /// Q1（UDP 由来）の溢れによる破棄を 1 件計上する。
    /// </summary>
    public void RecordInternalBufferDropped() => _internalBufferDropped.Add(1);

    /// <summary>
    /// TCP 同時接続数上限到達による新規接続拒否を 1 件計上する。
    /// </summary>
    public void RecordTcpConnectionRejected() => _tcpConnectionRejected.Add(1);

    /// <summary>
    /// 内部バッファ破棄カウンタの計器そのもの。テストで
    /// <c>Microsoft.Extensions.Diagnostics.Metrics.Testing.MetricCollector&lt;long&gt;</c>
    /// に直接束縛するために公開する（Meter 名・計器名の文字列一致より頑健なため）。
    /// </summary>
    public Counter<long> InternalBufferDroppedCounter => _internalBufferDropped;

    /// <summary>
    /// TCP 接続拒否カウンタの計器そのもの（テスト用。<see cref="InternalBufferDroppedCounter"/> と同じ理由）。
    /// </summary>
    public Counter<long> TcpConnectionRejectedCounter => _tcpConnectionRejected;

    public void Dispose() => _meter.Dispose();
}
