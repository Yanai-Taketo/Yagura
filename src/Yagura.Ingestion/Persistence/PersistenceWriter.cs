using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yagura.Ingestion.Diagnostics;
using Yagura.Storage;
using Yagura.Storage.Spool;

namespace Yagura.Ingestion.Persistence;

/// <summary>
/// 永続化段（architecture.md §2.1）。Q2 から取り出したレコードを「バッチ上限 N 件 or
/// 待機時間 T」でまとめ、<see cref="ILogStore.WriteBatchAsync"/> へ渡す。
/// </summary>
/// <remarks>
/// <para>
/// <b>書き込みは単一タスクで直列に行う</b>（<see cref="ILogStore"/> の契約——書き込み
/// 呼び出し元が直列化する責務を持つ。database.md §4）。本クラスは 1 つの消費ループの
/// みが <see cref="ILogStore.WriteBatchAsync"/> を呼び出すことでこれを満たす。
/// </para>
/// <para>
/// N・T は <see cref="PipelineConstants.WriteBatchMaxSize"/> /
/// <see cref="PipelineConstants.WriteBatchMaxWait"/>（実測確定待ち。M-1）。
/// バッチ書き込みには <see cref="PipelineConstants.WriteBatchTimeout"/> の時間上限を設け、
/// 応答のないハングを打ち切る（§2.1・§3.2.1。M-13）。
/// </para>
/// <para>
/// <b>M4-3 でのスプール退避</b>: タイムアウトで打ち切った・例外で失敗したバッチは、
/// スプールへ退避する（architecture.md §3.2.1「時間: バッチ書き込みが失敗した、
/// またはタイムアウトで打ち切られた」）。<see cref="DiskSpool"/> が <c>null</c>
/// （スプールなし縮退運転。§1.2）の場合、または退避自体が失敗した場合のみ破棄し、
/// 「永続化失敗」カウンタへ計上する。
/// </para>
/// <para>
/// なお、持続的な書き込み失敗でこのループが生き続ける場合でも、Q2 → Q1 の順に詰まって
/// 内部バッファ破棄カウンタ（architecture.md §4.1）に現れるため、完全に沈黙することはない。
/// </para>
/// <para>
/// <b>停止要求は書き込みタイムアウトより優先される</b>: <see cref="WriteBatchWithTimeoutAsync"/>
/// は <c>stoppingToken</c> とタイムアウト用トークンを連結し、停止要求が来たら
/// <see cref="PipelineConstants.WriteBatchTimeout"/>（既定 10 秒）の満了を待たずに
/// 即座に打ち切ってスプールへ退避する（§1.3 手順 2「DB へ書き切るのを待たず」の実現。
/// 連結しない実装だと、停止直前に開始した書き込みがハングしている場合、停止処理が
/// 最大でタイムアウト分だけ遅延してしまう）。
/// </para>
/// </remarks>
public sealed class PersistenceWriter
{
    private readonly ChannelReader<LogRecord> _q2Reader;
    private readonly ILogStore _logStore;
    private readonly DiskSpool? _spool;
    private readonly IngestionMetrics _metrics;
    private readonly ILogger<PersistenceWriter> _logger;

    public PersistenceWriter(
        ChannelReader<LogRecord> q2Reader,
        ILogStore logStore,
        DiskSpool? spool,
        IngestionMetrics metrics,
        ILogger<PersistenceWriter>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(q2Reader);
        ArgumentNullException.ThrowIfNull(logStore);
        ArgumentNullException.ThrowIfNull(metrics);

        _q2Reader = q2Reader;
        _logStore = logStore;
        _spool = spool;
        _metrics = metrics;
        _logger = logger ?? NullLogger<PersistenceWriter>.Instance;
    }

    /// <summary>
    /// Q2 の消費ループ。<paramref name="stoppingToken"/> がキャンセルされたら、以降は
    /// DB への書き込みを試みず、Q2 に残る分をスプールへ退避してから終了する
    /// （architecture.md §1.3 手順 2「メモリ上の未永続化ログを DB へ書き切るのを待たず、
    /// スプールへ追記して退避する」）。停止要求前は通常どおり N 件 or T 経過でバッチ書き込みする。
    /// </summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var batch = new List<LogRecord>(PipelineConstants.WriteBatchMaxSize);

        while (true)
        {
            batch.Clear();

            var batchDeadline = DateTimeOffset.UtcNow + PipelineConstants.WriteBatchMaxWait;

            // 1 件目は「停止要求 かつ 空」まで待つ。以降は N 件 or T 経過まで取り貯める。
            try
            {
                if (!await TryReadFirstAsync(batch, stoppingToken).ConfigureAwait(false))
                {
                    // 停止要求済みかつ Q2 が空——drain 完了。
                    return;
                }

                while (batch.Count < PipelineConstants.WriteBatchMaxSize)
                {
                    var remaining = batchDeadline - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    if (!_q2Reader.TryRead(out var record))
                    {
                        // 即座に追加できるものが無ければ、残り時間だけ次の到着を待つ。
                        using var waitCts = new CancellationTokenSource(remaining);
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, waitCts.Token);
                        try
                        {
                            if (!await _q2Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
                            {
                                break;
                            }
                        }
                        catch (OperationCanceledException) when (waitCts.IsCancellationRequested)
                        {
                            break;
                        }

                        continue;
                    }

                    batch.Add(record);
                }
            }
            catch (OperationCanceledException)
            {
                // stoppingToken 自体がキャンセルされた——溜まっている分（+ Q2 の残り全て）を
                // DB を待たずスプールへ退避してから終了する（§1.3 手順 2）。
                await DrainRemainderToSpoolAsync(batch).ConfigureAwait(false);
                return;
            }

            if (batch.Count > 0)
            {
                await WriteBatchWithTimeoutAsync(batch, stoppingToken).ConfigureAwait(false);
            }

            if (stoppingToken.IsCancellationRequested)
            {
                // このバッチの書き込み中に停止要求が来た場合も、以降は DB を待たず
                // 残りをスプールへ退避する（§1.3 手順 2）。
                await DrainRemainderToSpoolAsync([]).ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>
    /// 停止時、<paramref name="pending"/>（読み取り済みだが未書き込みの分）と Q2 に
    /// 残っている全レコードを、DB を経由せず直接スプールへ退避する
    /// （architecture.md §1.3 手順 2）。退避中に発生した破棄は逐次カウンタへ反映する
    /// （§1.3「強制終了された場合でも退避中の破棄が計上されないまま消えないように」——
    /// 本メソッドは 1 件ごとに <see cref="EvacuateSingleRecordAsync"/> を呼び、
    /// カウンタ計上をレコード単位で完結させることでこれを満たす）。
    /// </summary>
    private async Task DrainRemainderToSpoolAsync(List<LogRecord> pending)
    {
        foreach (var record in pending)
        {
            await EvacuateSingleRecordAsync(record).ConfigureAwait(false);
        }

        while (_q2Reader.TryRead(out var record))
        {
            await EvacuateSingleRecordAsync(record).ConfigureAwait(false);
        }
    }

    private async Task<bool> TryReadFirstAsync(List<LogRecord> batch, CancellationToken stoppingToken)
    {
        while (true)
        {
            if (_q2Reader.TryRead(out var record))
            {
                batch.Add(record);
                return true;
            }

            if (stoppingToken.IsCancellationRequested)
            {
                return false;
            }

            if (!await _q2Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                // Channel が完了（Writer 側が Complete 済み）かつ空。
                return false;
            }
        }
    }

    /// <summary>
    /// バッチを DB へ書き込む。<paramref name="stoppingToken"/> と
    /// <see cref="PipelineConstants.WriteBatchTimeout"/> のどちらか早い方で打ち切る——
    /// 停止要求時に通常のタイムアウト（既定 10 秒）を律義に待つと、§1.3「DB へ書き切るのを
    /// 待たず」に反して停止処理が遅延するため、停止要求はタイムアウトより優先して
    /// 即座に打ち切りへつなげる。
    /// </summary>
    private async Task WriteBatchWithTimeoutAsync(List<LogRecord> batch, CancellationToken stoppingToken)
    {
        using var timeoutCts = new CancellationTokenSource(PipelineConstants.WriteBatchTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);

        try
        {
            await _logStore.WriteBatchAsync(batch, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                // 停止要求による打ち切り（§1.3 手順 2「DB へ書き切るのを待たず」）。
                _logger.LogWarning(
                    "停止要求により DB 書き込みを打ち切り、{Count} 件をスプールへ退避する。",
                    batch.Count);
            }
            else
            {
                // 応答のないハングを打ち切る（architecture.md §2.1・§3.2.1）。打ち切ったバッチは
                // スプールへ退避する（タイムアウト後に DB 側で書き込みが成立していた場合の重複は
                // at-least-once の範囲内。§3.2.1）。
                _logger.LogWarning(
                    "バッチ書き込みがタイムアウト時間 {Timeout} を超過したため打ち切り、{Count} 件をスプールへ退避する。",
                    PipelineConstants.WriteBatchTimeout,
                    batch.Count);
            }

            await EvacuateToSpoolAsync(batch).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 書き込み例外（一時的なディスクエラー・ロック等）で消費ループを恒久停止させない——
            // ループが止まるとリスナは受信を続けたまま Q2 → Q1 と詰まり、以降の全受信が
            // 内部バッファ破棄になる「黙った縮退」（architecture.md §1.2 が禁じる状態）に陥る。
            // 当該バッチはスプールへ退避し、ループは継続する。
            _logger.LogWarning(
                ex,
                "バッチ書き込みが失敗したため {Count} 件をスプールへ退避し、消費ループを継続する。",
                batch.Count);

            await EvacuateToSpoolAsync(batch).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 書き込みに失敗・タイムアウトしたバッチをスプールへ退避する（architecture.md §3.2.1）。
    /// </summary>
    private async Task EvacuateToSpoolAsync(List<LogRecord> batch)
    {
        foreach (var record in batch)
        {
            await EvacuateSingleRecordAsync(record).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 1 件をスプールへ退避する。スプールが無い（縮退運転。§1.2）場合、または退避自体が
    /// 失敗した場合は破棄し「永続化失敗」カウンタへ計上する。カウンタ計上をレコード単位で
    /// 完結させることで、退避処理の途中で強制終了しても、それまでに処理済みの分の
    /// カウンタ計上は失われない（§1.3「退避中に発生した破棄は逐次カウンタへ反映する」）。
    /// </summary>
    private async Task EvacuateSingleRecordAsync(LogRecord record)
    {
        if (_spool is null)
        {
            // スプールなし縮退運転（§1.2）: 退避先が無いため破棄する。
            _metrics.RecordPersistenceFailed();
            return;
        }

        var result = await _spool.TryAppendAsync(SpoolRecord.ForLog(record)).ConfigureAwait(false);

        switch (result)
        {
            case SpoolAppendResult.Appended:
                _metrics.RecordSpoolEvacuated();
                break;
            case SpoolAppendResult.QuotaExceeded:
                _metrics.RecordSpoolDiscarded();
                _metrics.RecordPersistenceFailed();
                break;
            case SpoolAppendResult.WriteFailed:
                _metrics.RecordSpoolWriteFailed();
                _metrics.RecordPersistenceFailed();
                break;
        }
    }
}
