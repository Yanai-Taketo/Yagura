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

    /// <summary>拒否カウンタの計器そのもの（テスト用）。</summary>
    public Counter<long> ListenerGuardRejectedCounter => _listenerGuardRejected;

    /// <summary>監査記録書き込み失敗カウンタの計器そのもの（テスト用）。</summary>
    public Counter<long> AuditWriteFailedCounter => _auditWriteFailed;

    public void Dispose() => _meter.Dispose();
}
