using System.Diagnostics.Metrics;

namespace Yagura.Ingestion.Diagnostics;

/// <summary>
/// パイプラインの計測点（architecture.md §4.1 発生箇所別ドロップカウンタ。ADR-0002 決定 4）。
/// </summary>
/// <remarks>
/// <para>
/// M2 では architecture.md §4.1 の表にある 8 種のカウンタのうち「内部バッファ破棄
/// （Q1 UDP 溢れ）」のみを実装した。M4-1（TCP 受信）で「TCP 接続拒否」を追加。
/// M4-3（ディスクスプール）で「スプール退避」「スプール書込失敗」「スプール破棄」
/// 「永続化失敗」を追加した。残り（流量制御破棄・解析失敗（保存済み）・TCP 接続断・
/// TLS ハンドシェイク失敗・TCP 不完全メッセージ）は流量制御判定・TLS 受信が実装される
/// 後続マイルストーンで追加する（TCP 不完全メッセージは ParseStatus.Incomplete として
/// ログレコード側に計上されるため、カウンタとしての追加は §4.6 のゲージ整備と合わせて
/// 後続で検討する）。
/// </para>
/// <para>
/// Meter 名・計器名は暫定であり、後続（流量制御追加時）に命名規則ごと見直す前提とする。
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
    private readonly Counter<long> _spoolEvacuated;
    private readonly Counter<long> _spoolWriteFailed;
    private readonly Counter<long> _spoolDiscarded;
    private readonly Counter<long> _persistenceFailed;

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

        // architecture.md §4.1「スプール退避」: Q2 溢れ・書き込み失敗/タイムアウトにより
        // スプールへ退避した件数（損失ではない。飽和の予兆シグナル。§3.1）。
        _spoolEvacuated = _meter.CreateCounter<long>(
            "yagura.ingestion.spool.evacuated",
            unit: "{record}",
            description: "Q2 溢れ・書き込み失敗/タイムアウトによりスプールへ退避した件数。");

        // architecture.md §4.1「スプール書込失敗」: スプール追記をリトライしても書き込めず
        // 破棄した件数（ディスク障害等）。
        _spoolWriteFailed = _meter.CreateCounter<long>(
            "yagura.ingestion.spool.write_failed",
            unit: "{record}",
            description: "スプール追記がリトライ後も失敗し破棄した件数。");

        // architecture.md §4.1「スプール破棄」: スプール上限到達により新規到着分を破棄した件数
        // （§3.2.3）。
        _spoolDiscarded = _meter.CreateCounter<long>(
            "yagura.ingestion.spool.discarded",
            unit: "{record}",
            description: "スプール上限到達により新規破棄した件数。");

        // architecture.md §4.1「永続化失敗」: リトライとスプール退避でも救えず失われた件数
        // （スプールなし縮退中の喪失を含む。§1.2）。
        _persistenceFailed = _meter.CreateCounter<long>(
            "yagura.ingestion.persistence.failed",
            unit: "{record}",
            description: "リトライ・スプール退避でも救えず失われた件数（スプールなし縮退中の喪失を含む）。");
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
    /// スプールへの退避を 1 件計上する（損失ではない。§4.1）。
    /// </summary>
    public void RecordSpoolEvacuated() => _spoolEvacuated.Add(1);

    /// <summary>
    /// スプール追記の失敗（リトライ後破棄）を 1 件計上する。
    /// </summary>
    public void RecordSpoolWriteFailed() => _spoolWriteFailed.Add(1);

    /// <summary>
    /// スプール上限到達による破棄を 1 件計上する（§3.2.3）。
    /// </summary>
    public void RecordSpoolDiscarded() => _spoolDiscarded.Add(1);

    /// <summary>
    /// リトライ・スプール退避でも救えなかった永続化失敗を 1 件計上する
    /// （スプールなし縮退中の喪失を含む。§1.2・§4.1）。
    /// </summary>
    public void RecordPersistenceFailed() => _persistenceFailed.Add(1);

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

    /// <summary>スプール退避カウンタの計器そのもの（テスト用）。</summary>
    public Counter<long> SpoolEvacuatedCounter => _spoolEvacuated;

    /// <summary>スプール書込失敗カウンタの計器そのもの（テスト用）。</summary>
    public Counter<long> SpoolWriteFailedCounter => _spoolWriteFailed;

    /// <summary>スプール破棄カウンタの計器そのもの（テスト用）。</summary>
    public Counter<long> SpoolDiscardedCounter => _spoolDiscarded;

    /// <summary>永続化失敗カウンタの計器そのもの（テスト用）。</summary>
    public Counter<long> PersistenceFailedCounter => _persistenceFailed;

    public void Dispose() => _meter.Dispose();
}
