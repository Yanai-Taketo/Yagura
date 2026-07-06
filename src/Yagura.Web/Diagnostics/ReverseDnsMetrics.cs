using System.Diagnostics.Metrics;

namespace Yagura.Web.Diagnostics;

/// <summary>
/// 逆引き（PTR）ホスト名解決の計測点（ADR-0007 決定 6。architecture.md §4.1.1 の命名規則に従う）。
/// </summary>
/// <remarks>
/// <para>
/// Meter 名は <see cref="WebGuardMetrics.MeterName"/> と同じ <c>"Yagura"</c>（単一の計測空間へ統合）。
/// 4 計器は「意味論の異なる別々の事象」であり、タグ付き単一カウンタにしない（§4.1.1 の判断と同じ）:
/// <list type="bullet">
/// <item><c>yagura.web.reverse_dns.resolved</c>: 解決成功（名前を取得できた。無害化で表示不可と
/// なった名前も解決としては成功に数える）。</item>
/// <item><c>yagura.web.reverse_dns.not_found</c>: PTR 未登録（正常系——PTR 未整備の LAN は
/// 珍しくない。ADR-0007 文脈 3）。</item>
/// <item><c>yagura.web.reverse_dns.failed</c>: 解決失敗（打ち切り・DNS 障害。増加が異常の
/// シグナル）。</item>
/// <item><c>yagura.web.reverse_dns.skipped</c>: キャッシュ件数上限による解決の見送り
/// （増加はキャッシュ運用の逼迫のシグナル）。</item>
/// </list>
/// </para>
/// <para>
/// カウンタとは別に <see cref="Interlocked"/> のプロセス内累積値を持ち、状態画面
/// （<c>SystemStatus</c>）がカウンタ一覧へ表示する読み出し口とする（<c>IngestionMetrics</c> の
/// スナップショットと同じ理由——<c>Counter&lt;T&gt;</c> は読み戻しの標準 API を持たない。
/// architecture.md §4.3.2）。本カウンタは表示補助の観測でありログの完全性と無関係のため、
/// メタデータ領域への永続化（再起動をまたぐ累積）は行わない。
/// </para>
/// </remarks>
public sealed class ReverseDnsMetrics : IDisposable
{
    /// <summary>計器名: 解決成功。</summary>
    public const string ResolvedInstrumentName = "yagura.web.reverse_dns.resolved";

    /// <summary>計器名: PTR 未登録。</summary>
    public const string NotFoundInstrumentName = "yagura.web.reverse_dns.not_found";

    /// <summary>計器名: 解決失敗（打ち切り・DNS 障害）。</summary>
    public const string FailedInstrumentName = "yagura.web.reverse_dns.failed";

    /// <summary>計器名: キャッシュ上限による見送り。</summary>
    public const string SkippedInstrumentName = "yagura.web.reverse_dns.skipped";

    private readonly Meter _meter;
    private readonly Counter<long> _resolved;
    private readonly Counter<long> _notFound;
    private readonly Counter<long> _failed;
    private readonly Counter<long> _skipped;

    private long _resolvedTotal;
    private long _notFoundTotal;
    private long _failedTotal;
    private long _skippedTotal;

    public ReverseDnsMetrics()
    {
        _meter = new Meter(WebGuardMetrics.MeterName);

        _resolved = _meter.CreateCounter<long>(
            ResolvedInstrumentName,
            unit: "{lookup}",
            description: "逆引きホスト名の解決成功件数（ADR-0007 決定 6）。");

        _notFound = _meter.CreateCounter<long>(
            NotFoundInstrumentName,
            unit: "{lookup}",
            description: "逆引きホスト名の PTR 未登録件数（正常系。ADR-0007 決定 6）。");

        _failed = _meter.CreateCounter<long>(
            FailedInstrumentName,
            unit: "{lookup}",
            description: "逆引きホスト名の解決失敗件数（打ち切り・DNS 障害。ADR-0007 決定 6）。");

        _skipped = _meter.CreateCounter<long>(
            SkippedInstrumentName,
            unit: "{lookup}",
            description: "キャッシュ件数上限による逆引き解決の見送り件数（ADR-0007 決定 3・6）。");
    }

    /// <summary>解決成功を 1 件計上する。</summary>
    public void RecordResolved()
    {
        _resolved.Add(1);
        Interlocked.Increment(ref _resolvedTotal);
    }

    /// <summary>PTR 未登録を 1 件計上する。</summary>
    public void RecordNotFound()
    {
        _notFound.Add(1);
        Interlocked.Increment(ref _notFoundTotal);
    }

    /// <summary>解決失敗（打ち切り・DNS 障害）を 1 件計上する。</summary>
    public void RecordFailed()
    {
        _failed.Add(1);
        Interlocked.Increment(ref _failedTotal);
    }

    /// <summary>キャッシュ上限による見送りを 1 件計上する。</summary>
    public void RecordSkipped()
    {
        _skipped.Add(1);
        Interlocked.Increment(ref _skippedTotal);
    }

    /// <summary>解決成功のプロセス内累積値（状態画面の表示用）。</summary>
    public long ResolvedTotal => Interlocked.Read(ref _resolvedTotal);

    /// <summary>PTR 未登録のプロセス内累積値（状態画面の表示用）。</summary>
    public long NotFoundTotal => Interlocked.Read(ref _notFoundTotal);

    /// <summary>解決失敗のプロセス内累積値（状態画面の表示用）。</summary>
    public long FailedTotal => Interlocked.Read(ref _failedTotal);

    /// <summary>見送りのプロセス内累積値（状態画面の表示用）。</summary>
    public long SkippedTotal => Interlocked.Read(ref _skippedTotal);

    public void Dispose() => _meter.Dispose();
}
