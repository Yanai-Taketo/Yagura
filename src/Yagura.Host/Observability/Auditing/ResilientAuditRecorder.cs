using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yagura.Abstractions.Auditing;
using Yagura.Web.Diagnostics;

namespace Yagura.Host.Observability.Auditing;

/// <summary>
/// 監査チャネル障害中の事象保持・復旧後の書き戻し（SEC-10。security.md §4.2。Issue #269）。
/// <see cref="FileAuditRecorder"/> をラップし、アプリ記録ファイルへの書き込みが失敗した事象を
/// メモリ内に保持して、チャネル復旧後にファイルへ書き戻す。
/// </summary>
/// <remarks>
/// <para>
/// <b>責務分界（Issue #52・#269）</b>: <see cref="FileAuditRecorder"/> は「1 事象を 2 チャネルへ
/// 1 回書く」単責務に留め、失敗中の保持・書き戻しは本デコレータが担う（内側の
/// <see cref="FileAuditRecorder.TryRecord"/> が返す書き込み成否で「アプリ記録ファイルへ確実に
/// 残ったか」を判定する）。
/// </para>
/// <para>
/// <b>保持の判定</b>: アプリ記録ファイルの書き込みが失敗した事象を保持対象にする（イベントログへは
/// 内側が併記済みでも、<b>クエリ可能な正本はアプリ記録ファイル</b>であり、SEC-10 の目的は復旧後に
/// この正本を可能な限り完全にすることだから）。ファイル書き込みが成功していれば保持は不要
/// （既に正本に載っている）。
/// </para>
/// <para>
/// <b>縮退（<see cref="AuditResilienceDefaults.MaxBufferedEvents"/>）</b>: 無制限保持は OOM を招く
/// ため上限を設ける。上限到達後は<b>古い側を残し、到来した新しい事象を破棄</b>する——障害の起点と
/// 「監査記録が欠落し得た期間」の開始を保全するため。破棄件数は必ず計上する（復旧サマリ 3013 の
/// <c>Detail</c> + ライブ計器 <c>yagura.web.audit.buffer_dropped</c>——復旧前にプロセスが落ちても
/// 件数が観測に残るよう二重に残す。§4.2「縮退により捨てた件数は必ず計上する」）。
/// </para>
/// <para>
/// <b>復旧の検知と書き戻し</b>: (1) 新規事象のファイル書き込みが成功した時、または (2) 周期スキャン
/// （<see cref="AuditResilienceDefaults.RecoveryScanInterval"/>。障害中に新規事象が来なくても復旧を
/// 検知する）で、保持中の事象を発生日（UTC）のファイルへ古い順に書き戻す。書き戻した事象は
/// <b>遅延記録である旨を <c>Detail</c> に明示</b>する（§4.2「書き戻し時に遅延記録と分かる形にするか
/// 判断」——欠落期間の解析で「いつ発生し・いつ書かれたか」を取り違えないよう明示側を採る。発生日時
/// <see cref="AuditEvent.OccurredAt"/> は原事象のまま保つのでファイルの日付と内容の日付は一致する）。
/// 全件を書き戻し切ったら復旧サマリ（<see cref="AuditEventKind.AuditChannelRecovered"/>=3013）を
/// 1 件記録し、「欠落し得た期間・書き戻し件数・縮退破棄件数」を証跡に残す。
/// </para>
/// <para>
/// <b>限界の明示（§4.2）</b>: 保持はメモリ内のため、復旧前のプロセス終了（クラッシュ）で保持中の
/// 事象は失われる。この限界は縮退破棄のライブ計器と、障害開始が第 2・第 3 チャネル（イベントログ・
/// 計器）に残ることで下支えする（§4.2 のクラッシュ限界の流儀）。
/// </para>
/// <para>
/// <b>第 3 チャネル（状態画面での可視化）の要否</b>: 本 PR ではライブ計器
/// （<c>audit.write_failed</c>・<c>audit.buffer_dropped</c>）と復旧サマリ事象（3013）で観測性を確保し、
/// 専用の状態画面ウィジェットは追加しない（§4.2 が第 3 段に位置づける「ゲージ・状態画面」の枠に
/// 沿う。計器を出しておくことで将来の状態画面がそれを読める）。
/// </para>
/// </remarks>
public sealed class ResilientAuditRecorder : IAuditRecorder, IHostedService, IDisposable
{
    private const string DeferredMarker = "[deferred-writeback: 監査チャネル障害中に発生した事象を復旧後に書き戻し]";

    private readonly FileAuditRecorder _inner;
    private readonly WebGuardMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ResilientAuditRecorder> _logger;

    private readonly object _sync = new();
    private readonly Queue<AuditEvent> _buffer = new();
    private readonly int _maxBufferedEvents;
    private bool _failureActive;
    private long _droppedCount;
    private DateTimeOffset _gapStartUtc;

    private ITimer? _recoveryTimer;

    public ResilientAuditRecorder(
        FileAuditRecorder inner,
        WebGuardMetrics metrics,
        TimeProvider? timeProvider = null,
        ILogger<ResilientAuditRecorder>? logger = null,
        int? maxBufferedEvents = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(metrics);
        _inner = inner;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ResilientAuditRecorder>.Instance;
        _maxBufferedEvents = maxBufferedEvents ?? AuditResilienceDefaults.MaxBufferedEvents;
    }

    /// <summary>現在メモリ内に保持している事象数（テスト・可観測性用）。</summary>
    internal int BufferedCount
    {
        get { lock (_sync) { return _buffer.Count; } }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _recoveryTimer = _timeProvider.CreateTimer(
            _ => TryRecoverFromTimer(),
            state: null,
            AuditResilienceDefaults.RecoveryScanInterval,
            AuditResilienceDefaults.RecoveryScanInterval);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _recoveryTimer?.Dispose();
        _recoveryTimer = null;

        // 停止時にも一度復旧を試みる——チャネルが既に戻っていれば保持中の事象を書き戻して
        // クラッシュ以外の通常停止での取りこぼしを減らす（ベストエフォート）。
        lock (_sync)
        {
            if (_failureActive)
            {
                AttemptWriteBackLocked();
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _recoveryTimer?.Dispose();
        _recoveryTimer = null;
    }

    /// <inheritdoc />
    public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        lock (_sync)
        {
            var outcome = _inner.TryRecord(auditEvent);

            if (outcome.FileSucceeded)
            {
                // アプリ記録の正本に載った。障害中だったなら、これを機に保持分を書き戻す
                // （チャネルが戻った証拠——新規事象が成功したのだから）。
                if (_failureActive)
                {
                    AttemptWriteBackLocked();
                }
            }
            else
            {
                // 正本への書き込みが失敗——保持対象。イベントログには内側が併記済みかもしれないが、
                // 復旧後に正本へ書き戻すため保持する。
                BufferLocked(auditEvent);
            }
        }

        return Task.CompletedTask;
    }

    private void BufferLocked(AuditEvent auditEvent)
    {
        if (!_failureActive)
        {
            _failureActive = true;
            _gapStartUtc = auditEvent.OccurredAt.ToUniversalTime();
        }

        if (_buffer.Count >= _maxBufferedEvents)
        {
            // 縮退: 到来した新しい事象を破棄し、件数を計上する（古い側 = 障害起点を保全）。
            _droppedCount++;
            _metrics.RecordAuditBufferDropped();
            return;
        }

        _buffer.Enqueue(auditEvent);
    }

    private void TryRecoverFromTimer()
    {
        try
        {
            lock (_sync)
            {
                if (_failureActive)
                {
                    AttemptWriteBackLocked();
                }
            }
        }
        catch (Exception ex)
        {
            // 周期スキャンは監査経路を妨げない——失敗は次回の周期で再試行する。
            _logger.LogWarning(ex, "監査チャネルの復旧スキャンに失敗しました（次回の周期で再試行されます）。");
        }
    }

    /// <summary>
    /// 保持中の事象を古い順にファイルへ書き戻す（<see cref="_sync"/> を保持した状態で呼ぶ）。
    /// 途中でファイル書き込みが再び失敗したら、そこで打ち切り残りは保持したまま返る（チャネルが
    /// まだ戻っていない）。全件書き戻せたら復旧サマリ（3013）を記録して障害状態を解除する。
    /// </summary>
    private void AttemptWriteBackLocked()
    {
        var writtenBack = 0;

        while (_buffer.Count > 0)
        {
            var pending = _buffer.Peek();
            var outcome = _inner.TryRecord(MarkDeferred(pending));

            if (!outcome.FileSucceeded)
            {
                // まだ書けない——ここまでの書き戻し分だけ確定し、残りは保持のまま。
                return;
            }

            _buffer.Dequeue();
            writtenBack++;
        }

        // 全件書き戻し完了——復旧サマリを出して障害状態を閉じる。
        var gapStart = _gapStartUtc;
        var dropped = _droppedCount;
        _failureActive = false;
        _droppedCount = 0;

        EmitRecoverySummaryLocked(gapStart, writtenBack, dropped);
    }

    private void EmitRecoverySummaryLocked(DateTimeOffset gapStart, int writtenBack, long dropped)
    {
        var now = _timeProvider.GetUtcNow();
        var detail =
            $"監査チャネル復旧。欠落し得た期間={gapStart:O}〜{now:O} " +
            $"書き戻し={writtenBack}件 縮退破棄={dropped}件";

        var summary = new AuditEvent(
            OccurredAt: now,
            Kind: AuditEventKind.AuditChannelRecovered,
            RemoteAddress: null,
            RemotePort: null,
            Detail: detail);

        var outcome = _inner.TryRecord(summary);
        if (!outcome.FileSucceeded)
        {
            // 復旧サマリ自体が書けなかった（チャネルが再び落ちた）。障害状態へ戻し、サマリ事象を
            // 保持し直す——次の復旧機会で改めてサマリを出す（欠落期間の記録を失わない）。
            _logger.LogWarning("監査チャネル復旧サマリの書き込みに失敗しました。障害状態を継続します。");
            _failureActive = true;
            _droppedCount = dropped;
            _gapStartUtc = gapStart;
            if (_buffer.Count < AuditResilienceDefaults.MaxBufferedEvents)
            {
                _buffer.Enqueue(summary);
            }
            else
            {
                _droppedCount++;
                _metrics.RecordAuditBufferDropped();
            }
        }
    }

    private static AuditEvent MarkDeferred(AuditEvent original)
    {
        var detail = string.IsNullOrEmpty(original.Detail)
            ? DeferredMarker
            : $"{original.Detail} {DeferredMarker}";
        return original with { Detail = detail };
    }
}
