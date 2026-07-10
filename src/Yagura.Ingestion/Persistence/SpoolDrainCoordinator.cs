using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yagura.Ingestion.Diagnostics;
using Yagura.Storage;
using Yagura.Storage.Spool;

namespace Yagura.Ingestion.Persistence;

/// <summary>
/// ディスクスプールの drain オーケストレーション（architecture.md §3.2.2）。
/// <see cref="DiskSpool"/>（ストレージ層の一次操作）と Q2・<see cref="ILogStore"/>
/// （Yagura.Ingestion 側が保持）の両方を仲介する。
/// </summary>
/// <remarks>
/// <para>
/// <b>ライブ優先（ヒステリシス）</b>: Q2 使用率が <see cref="SpoolConstants.DrainLowWatermarkRatio"/>
/// を下回っている間だけ drain を進め、<see cref="SpoolConstants.DrainHighWatermarkRatio"/>
/// を上回ったら停止する。低水位・再開水位を分けることで水位付近の振動を防ぐ。
/// </para>
/// <para>
/// <b>速度上限</b>: 1 回の drain バッチは <see cref="SpoolConstants.DrainBatchMaxSize"/> 件まで。
/// </para>
/// <para>
/// <b>保存先障害中は進めない</b>: drain バッチの書き込みが失敗・タイムアウトした場合、
/// バックオフ（<see cref="SpoolConstants.DrainBackoffDelay"/>）してから再試行する。
/// <b>drain 由来のバッチの失敗時にスプールへ再追記しない</b>——元セグメントを未消化のまま
/// 残し、次の周期で同じセグメントから再度読み直す（§3.2.2 の「退避 → drain → 再タイムアウト
/// → 再退避」ループを閉じるための設計）。
/// </para>
/// <para>
/// <b>at-least-once</b>: セグメントは「DB への書き込みが確定してから」削除する
/// （§2.2・§3.2.1）。書き込み成功 → 削除の間でクラッシュした場合、次回起動時に
/// 同じセグメントが再度 drain され重複が発生し得るが、これは仕様として許容する。
/// </para>
/// <para>
/// <b>定期自己検証の照合（§3.2.5。Issue #152）</b>: <see cref="SpoolRecordKind.SelfTest"/> の
/// 合成レコードを DB 書き込み直前で破棄するのは drain の常時動作だが、破棄する前に
/// <see cref="Yagura.Storage.Spool.SpoolSelfTestTracker"/>（渡されていれば）へマーカーを通知する。
/// 投入側（<c>Yagura.Host.Observability.ActiveNotification.ActiveNotificationMonitor</c>）と
/// 同一インスタンスを共有することで、「投入 → drain の実機構に読ませる → 合流判定」の照合が
/// 成立する。トラッカー未指定（<c>null</c>）でも drain 自体の識別・破棄動作は変わらない
/// （検証の有無は drain の正しさに影響しない）。
/// </para>
/// </remarks>
public sealed class SpoolDrainCoordinator
{
    private readonly DiskSpool _spool;
    private readonly ChannelReader<LogRecord> _q2Reader;
    private readonly ILogStore _logStore;
    private readonly IngestionMetrics _metrics;
    private readonly ILogger<SpoolDrainCoordinator> _logger;
    private readonly ICapacityExhaustionHandler? _capacityExhaustionHandler;
    private readonly LogStoreWriteGate? _writeGate;
    private readonly SpoolSelfTestTracker? _selfTestTracker;

    public SpoolDrainCoordinator(
        DiskSpool spool,
        ChannelReader<LogRecord> q2Reader,
        ILogStore logStore,
        IngestionMetrics metrics,
        ILogger<SpoolDrainCoordinator>? logger = null,
        ICapacityExhaustionHandler? capacityExhaustionHandler = null,
        LogStoreWriteGate? writeGate = null,
        SpoolSelfTestTracker? selfTestTracker = null)
    {
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(q2Reader);
        ArgumentNullException.ThrowIfNull(logStore);
        ArgumentNullException.ThrowIfNull(metrics);

        _spool = spool;
        _q2Reader = q2Reader;
        _logStore = logStore;
        _metrics = metrics;
        _logger = logger ?? NullLogger<SpoolDrainCoordinator>.Instance;
        _capacityExhaustionHandler = capacityExhaustionHandler;
        _writeGate = writeGate;
        _selfTestTracker = selfTestTracker;
    }

    /// <summary>
    /// drain ループ。<paramref name="stoppingToken"/> がキャンセルされるまで実行し続ける。
    /// </summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // ライブ優先: Q2 使用率が再開水位を上回っていれば drain を進めない
            // （§3.2.2「ライブ流入が増えて Q2 が埋まれば drain は自動的に停止する」）。
            if (GetQ2UsageRatio() > SpoolConstants.DrainHighWatermarkRatio)
            {
                await DelayAsync(SpoolConstants.DrainPollInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }

            var segments = _spool.TrySealActiveSegmentAndListDrainable();
            if (segments.Count == 0)
            {
                await DelayAsync(SpoolConstants.DrainPollInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }

            var madeProgress = false;

            foreach (var segmentPath in segments)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                // 低水位を上回ったら（まだ高水位未満でも）このセグメントの drain 完了後に
                // 一旦様子を見る——Q2 使用率が上向きに転じている途中の過剰投入を避ける。
                if (GetQ2UsageRatio() > SpoolConstants.DrainLowWatermarkRatio && madeProgress)
                {
                    break;
                }

                var drained = await DrainSegmentAsync(segmentPath, stoppingToken).ConfigureAwait(false);
                if (!drained)
                {
                    // 保存先障害中——バックオフしてから外側のループで再試行する。
                    await DelayAsync(SpoolConstants.DrainBackoffDelay, stoppingToken).ConfigureAwait(false);
                    break;
                }

                madeProgress = true;
            }

            if (!madeProgress)
            {
                await DelayAsync(SpoolConstants.DrainPollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 1 セグメントを drain する。DB への書き込みが確定してからセグメントを削除する
    /// （先に消すとクラッシュで喪失し at-least-once に反する）。
    /// </summary>
    /// <returns>drain に成功した場合 <c>true</c>。書き込み失敗・タイムアウトの場合 <c>false</c>
    /// （セグメントは未消化のまま残す。§3.2.2「drain 由来のバッチはスプールへ再追記しない」）。</returns>
    private async Task<bool> DrainSegmentAsync(string segmentPath, CancellationToken stoppingToken)
    {
        IReadOnlyList<SpoolRecord> records;
        bool corruptTailDetected;

        try
        {
            records = _spool.ReadSegmentRecords(segmentPath, out corruptTailDetected);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "スプールセグメント {SegmentPath} の読み取りに失敗したため、この周期の drain を見送る。", segmentPath);
            return false;
        }

        if (corruptTailDetected)
        {
            _logger.LogWarning(
                "スプールセグメント {SegmentPath} の末尾に破損を検出したため、それ以降を読み捨てた（回収できた分は drain する）。",
                segmentPath);
        }

        // 自己検証用の合成レコード（§3.2.5）は DB 書き込みの直前で破棄する——
        // この識別・破棄は定期検証時だけでなく drain の常時動作である。破棄する前に、
        // 定期自己検証（Issue #152）の照合対象としてトラッカーへ通知する——「drain の
        // 実機構に読ませて照合する」ことの実体はこの通知である（トラッカーが無い、または
        // 投入していないマーカーであれば黙って無視される。§3.2.5）。
        foreach (var selfTestMarker in records
            .Where(r => r.Kind == SpoolRecordKind.SelfTest)
            .Select(r => r.SelfTestMarker!))
        {
            _selfTestTracker?.OnSelfTestRecordDrained(selfTestMarker);
        }

        var logRecords = records
            .Where(r => r.Kind == SpoolRecordKind.Normal)
            .Select(r => r.LogRecord!)
            .ToList();

        if (logRecords.Count == 0)
        {
            // 通常ログが 0 件（自己検証レコードのみ、または空/全破損セグメント）でも
            // セグメント自体は消化済みとして削除してよい——再 drain しても得るものがない。
            _spool.DeleteSegment(segmentPath);
            return true;
        }

        for (var offset = 0; offset < logRecords.Count; offset += SpoolConstants.DrainBatchMaxSize)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                // 停止要求が来たら、このセグメントは未消化のまま残し次回 drain に委ねる。
                return false;
            }

            var batch = logRecords.Skip(offset).Take(SpoolConstants.DrainBatchMaxSize).ToList();

            try
            {
                // 書き込みゲート（Issue #151）: ライブ・保持期間削除と同じゲートを通す。
                // ゲート待ちのタイムアウトは DB 操作のタイムアウトと独立させる
                // （PersistenceWriter.WriteBatchWithTimeoutAsync のコメント・LogStoreWriteGate の
                // doc コメント参照）。取得できなければこのセグメントは未消化のまま残し、
                // 次周期の drain に委ねる——既存の「drain 由来のバッチはスプールへ再追記しない」
                // 方針（§3.2.2）と同じ扱いにする。
                IDisposable? gateLease = _writeGate is null
                    ? null
                    : await _writeGate.AcquireAsync(PipelineConstants.WriteGateAcquireTimeout, stoppingToken).ConfigureAwait(false);
                try
                {
                    using var timeoutCts = new CancellationTokenSource(PipelineConstants.WriteBatchTimeout);
                    await _logStore.WriteBatchAsync(batch, timeoutCts.Token).ConfigureAwait(false);
                }
                finally
                {
                    gateLease?.Dispose();
                }
            }
            catch (LogStoreWriteGateTimeoutException ex)
            {
                _logger.LogWarning(
                    ex,
                    "[write-gate-timeout] 書き込みゲートの取得がタイムアウトしたため（他の書き込み経路が実行中の可能性）、" +
                    "セグメント {SegmentPath} を未消化のまま残す（次周期で再試行。§3.2.2 と同じ扱い）。",
                    segmentPath);
                return false;
            }
            catch (LogStoreWriteException ex)
            {
                if (ex.FailureKind == LogStoreFailureKind.CapacityExhausted)
                {
                    // 容量枯渇: 保持期間削除の前倒し実行で自走復旧を試みる（database.md §3・§4・§5.3）。
                    // drain 側で検知した場合も同じハンドラへ通知する——退避元がライブ書き込み
                    // （PersistenceWriter）か drain かにかかわらず、自走復旧の契機は同一に扱う。
                    _logger.LogWarning(
                        ex,
                        "[capacity-exhausted] 容量枯渇により drain 由来のバッチ書き込みが失敗したため、" +
                        "セグメント {SegmentPath} を未消化のまま残し、保持期間削除の前倒し実行を試みる（再追記はしない。§3.2.2）。",
                        segmentPath);
                    _capacityExhaustionHandler?.OnCapacityExhausted();
                }
                else
                {
                    _logger.LogWarning(
                        ex,
                        "drain 由来のバッチ書き込みが失敗したため、セグメント {SegmentPath} を未消化のまま残す（再追記はしない。§3.2.2）。",
                        segmentPath);
                }

                return false;
            }
            catch (Exception ex)
            {
                // provider が LogStoreWriteException を経由せず素の例外を投げた場合の保険的な受け皿。
                _logger.LogWarning(
                    ex,
                    "drain 由来のバッチ書き込みが失敗したため、セグメント {SegmentPath} を未消化のまま残す（再追記はしない。§3.2.2）。",
                    segmentPath);
                return false;
            }
        }

        // 全バッチの書き込みが確定してから削除する（at-least-once。§3.2.1）。
        _spool.DeleteSegment(segmentPath);
        return true;
    }

    private double GetQ2UsageRatio() => (double)_q2Reader.Count / PipelineConstants.Q2Capacity;

    private static async Task DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停止要求による遅延の中断は正常系（呼び出し元のループ条件で終了する）。
        }
    }
}
