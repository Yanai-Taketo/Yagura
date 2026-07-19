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
/// <para>
/// <b>末尾破損分の計上（Issue #201）</b>: <see cref="DiskSpool.ReadSegmentRecords"/> が
/// <c>corruptTailDetected</c> を返した場合、読み捨てた末尾のバイト数を
/// <see cref="IngestionMetrics.RecordSpoolCorruptTailDiscarded"/> で計上する
/// （architecture.md §3.1「カウンタに計上されない喪失は重大」）。計上は
/// <see cref="DiskSpool.DeleteSegment"/> の直前（= このセグメントの drain 完了が確定する
/// タイミング）でのみ行う——書き込み失敗により同じセグメントを次周期で再読み込みする間、
/// 同じ破損バイト数を毎回計上してしまう二重計上を避けるため。
/// </para>
/// <para>
/// <b>再起動を跨ぐ理論上の二重計上（既知・許容。PR #213 レビュー指摘）</b>: 計上と
/// <see cref="DiskSpool.DeleteSegment"/> の間には同期を取っていない微小な time window があり、
/// この間にメタデータ領域の定期永続化（既定 10 秒間隔）が「計上済みだが未削除」の状態を
/// 書き出した直後・削除完了前にプロセスが非グレースフルにクラッシュした場合、再起動後の
/// 再 drain で同じバイト数が再度加算され、永続化累積値が恒久的に過大になり得る。通常レコードの
/// at-least-once 重複（DB 側の重複行として現れる）とは異なり「累積カウンタの値そのもの」が
/// 膨らむ点で性質が異なるが、(i) window は加算〜削除呼び出しのごく短い区間に永続化タイマーの
/// 発火とクラッシュの両方が重なる必要があり発生確率が極めて低い、(ii) 発生しても
/// <c>SystemStatusReader</c> の異常判定は増分 &gt; 0 のみを見るため判定結果は変わらず、影響は
/// 表示上のバイト数の誤差にとどまる、の 2 点から exactly-once 化（計上と削除の
/// アトミック化）のコストに見合わないと判断し許容する。
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

            // 封止 + 列挙は環境要因で例外を投げる（ディスク満杯時の Dispose 失敗・ディレクトリ列挙の
            // 失敗）。ここで捕捉しないと drain ループごと fault して恒久停止する——スプールが最も
            // 必要とされている状況（ディスク満杯）で drain が止まるという最悪の組み合わせになる
            // （Issue #360）。ReadSegmentRecords と同じ「この周期は見送って次の周期で再試行」に揃える。
            IReadOnlyList<string> segments;
            try
            {
                segments = _spool.SealActiveSegmentAndListDrainable();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    ex,
                    "スプールの封止またはセグメント列挙に失敗したため、この周期の drain を見送る" +
                    "（次の周期で再試行する。ディスク満杯・I/O 障害が疑われる）。");
                await DelayAsync(SpoolConstants.DrainPollInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }

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
        long corruptTailBytes;

        try
        {
            records = _spool.ReadSegmentRecords(segmentPath, out corruptTailDetected, out corruptTailBytes);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "スプールセグメント {SegmentPath} の読み取りに失敗したため、この周期の drain を見送る。", segmentPath);
            return false;
        }

        if (corruptTailDetected)
        {
            _logger.LogWarning(
                "スプールセグメント {SegmentPath} の末尾に破損を検出したため、{CorruptTailBytes} バイトを読み捨てた" +
                "（回収できた分は drain する）。",
                segmentPath,
                corruptTailBytes);
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
            //
            // 末尾破損の計上（Issue #201）はここ・末尾の DeleteSegment 直前の 2 箇所でのみ行う
            // ——セグメント削除（= このセグメントの drain 完了）が確定するタイミングに限ることで、
            // 書き込み失敗による再試行のたびに同じ破損バイト数を重複計上することを避ける
            // （セグメントが未消化のまま残る限り、次周期の DrainSegmentAsync は同じ末尾破損を
            // 再度検出するため）。
            if (corruptTailDetected)
            {
                _metrics.RecordSpoolCorruptTailDiscarded(corruptTailBytes);
            }

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
        // 末尾破損の計上（Issue #201）はセグメント削除の確定直前で行う（上の早期リターン経路と
        // 同じ理由。クラス内コメント参照）。
        if (corruptTailDetected)
        {
            _metrics.RecordSpoolCorruptTailDiscarded(corruptTailBytes);
        }

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
