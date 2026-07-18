using System.Diagnostics.Metrics;

namespace Yagura.Web.Diagnostics;

/// <summary>
/// Yagura.Web（閲覧・管理リスナ）の計測点（architecture.md §4.1 発生箇所別ドロップカウンタ・
/// §4.1.1 計器の命名規則。M6-2。Issue #52）。
/// </summary>
/// <remarks>
/// <para>
/// <b>単一 Meter への統合</b>: architecture.md §4.1.1 が確定した「単一 Meter <c>Yagura</c> +
/// 計器名 <c>yagura.&lt;領域&gt;.&lt;事象&gt;</c>」の命名規則にそのまま従う。
/// <c>Yagura.Ingestion.Diagnostics.IngestionMetrics</c> と同じ Meter 名 <c>"Yagura"</c> の
/// 別インスタンスを本クラスが構築する——<c>Meter</c> は同名であれば .NET の計測 API 上は
/// 同一の計測空間として扱われる（<c>MeterListener</c>・OpenTelemetry エクスポータ等の購読側は
/// Meter 名で購読するため、複数の <c>Meter</c> インスタンスが同名であっても集約先は 1 つになる）。
/// <c>Yagura.Web</c> は <c>Yagura.Ingestion</c> を参照しない設計（Web は Storage 抽象のみを
/// 参照する。architecture.md の参照構造）のため、<see cref="IngestionMetrics"/> のインスタンスを
/// 共有する経路は採らず、本クラスとして独立に持つ。
/// </para>
/// <para>
/// <b>本クラスが持つ計器</b>:
/// <list type="bullet">
/// <item><c>yagura.web.listener_guard.rejected</c>: 閲覧リスナに到達した管理系要求の拒否件数
/// （security.md §1 L-3b。<see cref="ListenerPortGuardMiddleware"/> が発火）。</item>
/// <item><c>yagura.web.audit.write_failed</c>: 監査記録の書き込み失敗件数（アプリ記録ファイル・
/// イベントログの両方が失敗した場合。security.md §4.2 の多段の最終段——「それも失敗したら
/// 黙って握りつぶさずカウンタで観測可能にする」）。</item>
/// <item><c>yagura.web.audit.buffer_dropped</c>: 監査チャネル障害中のメモリ内保持が上限に達し、
/// 縮退で破棄した事象の件数（SEC-10。security.md §4.2。Issue #269）。復旧サマリ（3013）が
/// 書けないままプロセスが落ちても件数が観測に残るよう、ライブの計器としても計上する。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class WebGuardMetrics : IDisposable
{
    /// <summary>
    /// このコンポーネントが使用する <see cref="Meter"/> の名前。
    /// <c>Yagura.Ingestion.Diagnostics.IngestionMetrics.MeterName</c> と同じ文字列
    /// <c>"Yagura"</c> を独立に持つ（<c>Yagura.Web</c> は <c>Yagura.Ingestion</c> を
    /// 参照しないため定数を共有する経路がない。値の一致を崩さないことが重要——変更する場合は
    /// 両方を同時に変更すること）。
    /// </summary>
    public const string MeterName = "Yagura";

    private readonly Meter _meter;
    private readonly Counter<long> _listenerGuardRejected;
    private readonly Counter<long> _auditWriteFailed;
    private readonly Counter<long> _auditBufferDropped;
    private readonly Counter<long> _circuitOriginRejected;
    private readonly Counter<long> _circuitLimitRejected;
    private readonly Counter<long> _circuitIdleReclaimed;

    public WebGuardMetrics()
    {
        _meter = new Meter(MeterName);

        _listenerGuardRejected = _meter.CreateCounter<long>(
            "yagura.web.listener_guard.rejected",
            unit: "{request}",
            description: "閲覧リスナに到達した管理系要求の拒否件数（security.md §1 L-3b）。");

        _auditWriteFailed = _meter.CreateCounter<long>(
            "yagura.web.audit.write_failed",
            unit: "{event}",
            description: "監査記録の書き込み失敗件数（アプリ記録ファイル・イベントログの両方が失敗。security.md §4.2）。");

        _auditBufferDropped = _meter.CreateCounter<long>(
            "yagura.web.audit.buffer_dropped",
            unit: "{event}",
            description: "監査チャネル障害中の保持上限超過で縮退破棄した事象の件数（SEC-10。security.md §4.2）。");

        _circuitOriginRejected = _meter.CreateCounter<long>(
            "yagura.web.circuit.origin_rejected",
            unit: "{request}",
            description: "同一サイト以外からの circuit 確立試行の拒否件数（origin 検証。security.md §2.1）。");

        _circuitLimitRejected = _meter.CreateCounter<long>(
            "yagura.web.circuit.limit_rejected",
            unit: "{request}",
            description: "circuit 数上限による新規接続の拒否件数（security.md §2.2。SEC-1 仮値）。");

        _circuitIdleReclaimed = _meter.CreateCounter<long>(
            "yagura.web.circuit.idle_reclaimed",
            unit: "{circuit}",
            description: "無操作 circuit の回収件数（security.md §2.2。SEC-8 仮値）。");
    }

    /// <summary>
    /// 閲覧リスナに到達した管理系要求の拒否を 1 件計上する（security.md §1 L-3b）。
    /// </summary>
    public void RecordListenerGuardRejected() => _listenerGuardRejected.Add(1);

    /// <summary>
    /// 監査記録の書き込み失敗（アプリ記録ファイル・イベントログの両方）を 1 件計上する
    /// （security.md §4.2 の多段の最終段）。
    /// </summary>
    public void RecordAuditWriteFailed() => _auditWriteFailed.Add(1);

    /// <summary>
    /// 監査チャネル障害中の保持上限超過で縮退破棄した事象を計上する（SEC-10。security.md §4.2）。
    /// </summary>
    public void RecordAuditBufferDropped(long count = 1)
    {
        if (count > 0)
        {
            _auditBufferDropped.Add(count);
        }
    }

    /// <summary>
    /// 同一サイト以外からの circuit 確立試行の拒否を 1 件計上する（security.md §2.1
    /// 「拒否は計測する」）。
    /// </summary>
    public void RecordCircuitOriginRejected() => _circuitOriginRejected.Add(1);

    /// <summary>
    /// circuit 数上限による新規接続の拒否を 1 件計上する（security.md §2.2「拒否はカウンタに
    /// 計上する」）。
    /// </summary>
    public void RecordCircuitLimitRejected() => _circuitLimitRejected.Add(1);

    /// <summary>無操作 circuit の回収を 1 件計上する（SEC-8）。</summary>
    public void RecordCircuitIdleReclaimed() => _circuitIdleReclaimed.Add(1);

    /// <summary>拒否カウンタの計器そのもの（テスト用）。</summary>
    public Counter<long> ListenerGuardRejectedCounter => _listenerGuardRejected;

    /// <summary>origin 拒否カウンタの計器そのもの（テスト用）。</summary>
    public Counter<long> CircuitOriginRejectedCounter => _circuitOriginRejected;

    /// <summary>circuit 上限拒否カウンタの計器そのもの（テスト用）。</summary>
    public Counter<long> CircuitLimitRejectedCounter => _circuitLimitRejected;

    /// <summary>監査記録書き込み失敗カウンタの計器そのもの（テスト用）。</summary>
    public Counter<long> AuditWriteFailedCounter => _auditWriteFailed;

    /// <summary>監査チャネル障害中の縮退破棄カウンタの計器そのもの（テスト用）。</summary>
    public Counter<long> AuditBufferDroppedCounter => _auditBufferDropped;

    public void Dispose() => _meter.Dispose();
}
