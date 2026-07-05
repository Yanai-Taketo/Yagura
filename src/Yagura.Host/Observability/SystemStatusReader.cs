using Yagura.Abstractions.Observability;
using Yagura.Ingestion.Diagnostics;
using Yagura.Storage.Spool;

namespace Yagura.Host.Observability;

/// <summary>
/// <see cref="IYaguraSystemStatusReader"/> のホスト実装（M8-3。Issue #70）:
/// 閲覧画面（ダッシュボード・システム状態）へ、カウンタ累積値・スプール状態・健康状態の
/// 判定結果を読み取り専用で公開する。
/// </summary>
/// <remarks>
/// <para>
/// <b>健康状態の判定（ui.md §5.1）</b>: 「判定は累積カウンタの現在値ではなく、直近の観測窓内の
/// 増分・現在のゲージ状態で行う」を、読み出しのたびに取るカウンタスナップショットの履歴
/// （観測窓 = <see cref="ObservationWindow"/>。UI-6 の仮値 5 分）との差分で実装する。
/// 判定規則（ui.md §5.1 の表に対応）:
/// 異常あり = 観測窓内の取りこぼし（いずれかの破棄カウンタの増分）/
/// 警告あり = 観測窓内のスプール退避の継続・スプール使用量の上限接近
/// （<see cref="SpoolNearLimitRatio"/> 以上）・スプールなし縮退運転。
/// </para>
/// <para>
/// <b>観測窓の基準スナップショットの保持</b>: 読み出し時に (時刻, スナップショット) を履歴へ
/// 追加し、観測窓より古い履歴は「窓の外側の直近 1 件」だけを残して間引く（増分の基準が
/// 窓幅ぶんを確実に覆うようにするため——全部消すと基準が窓より若くなり増分を取り逃がす）。
/// プロセス起動直後・最初の読み出しでは基準が無く増分を判定できないため、正常側に倒す
/// （過去の破棄はカウンタ一覧・履歴側で確認できる——「状態帯は『今』、履歴は『起きたこと』」
/// の分担。ui.md §5.1）。
/// </para>
/// <para>
/// <b>受信リスナが開けていない場合の異常判定（ui.md §5.1 の判定入力の 1 つ）は本実装では
/// 未対応</b>——リスナの開閉状態を公開する観測点が現行のパイプラインに無いため。リスナを
/// 開けない失敗はホスト起動自体の失敗として現れる（architecture.md §1.2）ことを暫定の
/// 根拠とし、部分 bind 失敗等の観測点は UI-6 の確定と合わせて追加する。
/// </para>
/// </remarks>
public sealed class SystemStatusReader : IYaguraSystemStatusReader
{
    /// <summary>状態帯判定の観測窓（ui.md §12 UI-6 の仮値。実利用で確定するまでの暫定値）。</summary>
    public static readonly TimeSpan ObservationWindow = TimeSpan.FromMinutes(5);

    /// <summary>スプール使用量の「上限接近」警告のしきい値（比率。M-12 と同様に実測確定までの暫定値）。</summary>
    public const double SpoolNearLimitRatio = 0.8;

    private readonly IngestionMetrics _metrics;
    private readonly DiskSpool? _spool;
    private readonly long _spoolQuotaBytes;
    private readonly bool _spoolDegraded;
    private readonly int? _retentionDays;
    private readonly IReadOnlyList<YaguraListenerEndpoint> _listeners;
    private readonly TimeProvider _timeProvider;

    private readonly object _historyLock = new();
    private readonly List<(DateTimeOffset TakenAt, IngestionCounterSnapshot Snapshot)> _history = [];

    /// <param name="metrics">パイプラインの計測点（カウンタ累積値の読み出し元）。</param>
    /// <param name="spool">スプール（無効・縮退時は <c>null</c>）。</param>
    /// <param name="spoolQuotaBytes">スプールのディスク使用量上限（設定の適用値。configuration.md §2）。</param>
    /// <param name="spoolDegraded">スプールなし縮退運転中か（明示無効化は縮退に数えない）。</param>
    /// <param name="retentionDays">保持期間の適用値（不正値フォールバック時は <c>null</c>）。</param>
    /// <param name="listeners">受信リスナの構成。</param>
    /// <param name="timeProvider">時刻源（テスト用の差し替え口。既定はシステム時刻）。</param>
    public SystemStatusReader(
        IngestionMetrics metrics,
        DiskSpool? spool,
        long spoolQuotaBytes,
        bool spoolDegraded,
        int? retentionDays,
        IReadOnlyList<YaguraListenerEndpoint> listeners,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(listeners);

        _metrics = metrics;
        _spool = spool;
        _spoolQuotaBytes = spoolQuotaBytes;
        _spoolDegraded = spoolDegraded;
        _retentionDays = retentionDays;
        _listeners = listeners;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public YaguraSystemStatusSnapshot ReadCurrent()
    {
        var now = _timeProvider.GetUtcNow();
        var snapshot = _metrics.SnapshotCumulativeCounters();
        var baseline = RecordAndGetBaseline(now, snapshot);

        var spoolReading = _spool is null
            ? null
            : new YaguraSpoolReading(_spool.CurrentUsageBytes, QuotaBytes: _spoolQuotaBytes);

        var health = Assess(snapshot, baseline, spoolReading);

        return new YaguraSystemStatusSnapshot(
            TakenAt: now,
            Counters: BuildCounterReadings(snapshot),
            Spool: spoolReading,
            SpoolDegraded: _spoolDegraded,
            Health: health,
            RetentionDays: _retentionDays,
            Listeners: _listeners);
    }

    private IngestionCounterSnapshot? RecordAndGetBaseline(DateTimeOffset now, IngestionCounterSnapshot snapshot)
    {
        lock (_historyLock)
        {
            _history.Add((now, snapshot));

            // 観測窓より古い履歴は「窓の外側の直近 1 件」だけ残して間引く（クラス remarks 参照）。
            var windowStart = now - ObservationWindow;
            var lastOutsideIndex = -1;
            for (var i = 0; i < _history.Count; i++)
            {
                if (_history[i].TakenAt < windowStart)
                {
                    lastOutsideIndex = i;
                }
            }

            if (lastOutsideIndex > 0)
            {
                _history.RemoveRange(0, lastOutsideIndex);
            }

            // 基準 = 最も古い残存履歴（今回追加分しか無い場合は基準なし = 初回読み出し）。
            return _history.Count > 1 ? _history[0].Snapshot : null;
        }
    }

    private YaguraHealthReading Assess(
        IngestionCounterSnapshot current,
        IngestionCounterSnapshot? baseline,
        YaguraSpoolReading? spoolReading)
    {
        var reasons = new List<YaguraHealthReason>();

        if (baseline is not null)
        {
            var lossIncrement =
                (current.InternalBufferDropped - baseline.InternalBufferDropped) +
                (current.SpoolWriteFailed - baseline.SpoolWriteFailed) +
                (current.SpoolDiscarded - baseline.SpoolDiscarded) +
                (current.PersistenceFailed - baseline.PersistenceFailed) +
                (current.FlowControlDropped - baseline.FlowControlDropped);

            if (lossIncrement > 0)
            {
                reasons.Add(YaguraHealthReason.LossObserved);
            }

            if (current.SpoolEvacuated - baseline.SpoolEvacuated > 0)
            {
                reasons.Add(YaguraHealthReason.SpoolEvacuationObserved);
            }
        }

        if (spoolReading is not null && spoolReading.UsageRatio >= SpoolNearLimitRatio)
        {
            reasons.Add(YaguraHealthReason.SpoolUsageNearLimit);
        }

        if (_spoolDegraded)
        {
            reasons.Add(YaguraHealthReason.SpoolDegraded);
        }

        if (reasons.Count == 0)
        {
            return YaguraHealthReading.Ok;
        }

        var kind = reasons.Contains(YaguraHealthReason.LossObserved)
            ? YaguraHealthKind.Error
            : YaguraHealthKind.Warning;

        return new YaguraHealthReading(kind, reasons);
    }

    private static IReadOnlyList<YaguraCounterReading> BuildCounterReadings(IngestionCounterSnapshot snapshot) =>
        // 計器名は architecture.md §4.1.1 の一覧（IngestionMetrics の CreateCounter 呼び出し）と
        // 1 対 1 対応させる。IsLoss の区分は同 §4.1 の表の「意味」列に従う（スプール退避・
        // TCP 接続拒否は損失ではない）。
        [
            new("yagura.ingestion.internal_buffer.dropped", snapshot.InternalBufferDropped, IsLoss: true),
            new("yagura.ingestion.tcp_connection.rejected", snapshot.TcpConnectionRejected, IsLoss: false),
            new("yagura.ingestion.spool.evacuated", snapshot.SpoolEvacuated, IsLoss: false),
            new("yagura.ingestion.spool.write_failed", snapshot.SpoolWriteFailed, IsLoss: true),
            new("yagura.ingestion.spool.discarded", snapshot.SpoolDiscarded, IsLoss: true),
            new("yagura.ingestion.persistence.failed", snapshot.PersistenceFailed, IsLoss: true),
            new("yagura.ingestion.flow_control.dropped", snapshot.FlowControlDropped, IsLoss: true),
        ];
}
