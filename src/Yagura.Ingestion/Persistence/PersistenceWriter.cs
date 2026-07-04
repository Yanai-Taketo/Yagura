using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yagura.Storage;

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
/// M2 時点では、Q2 溢れ時のスプール退避（§3.2）・書き込み失敗時のリトライは実装しない
/// （スプールは M4）。タイムアウトで打ち切った・例外で失敗したバッチは当該バッチのみ破棄し、
/// 消費ループは継続する（失敗の分類・リトライ・スプール退避・「永続化失敗」カウンタの計上は
/// M4 で追加する。M2 時点は例外を握りつぶさず警告ログへ残すのみ）。
/// </para>
/// <para>
/// なお、持続的な書き込み失敗でこのループが生き続ける場合でも、Q2 → Q1 の順に詰まって
/// 内部バッファ破棄カウンタ（architecture.md §4.1）に現れるため、完全に沈黙することはない。
/// </para>
/// </remarks>
public sealed class PersistenceWriter
{
    private readonly ChannelReader<LogRecord> _q2Reader;
    private readonly ILogStore _logStore;
    private readonly ILogger<PersistenceWriter> _logger;

    public PersistenceWriter(
        ChannelReader<LogRecord> q2Reader,
        ILogStore logStore,
        ILogger<PersistenceWriter>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(q2Reader);
        ArgumentNullException.ThrowIfNull(logStore);

        _q2Reader = q2Reader;
        _logStore = logStore;
        _logger = logger ?? NullLogger<PersistenceWriter>.Instance;
    }

    /// <summary>
    /// Q2 の消費ループ。<paramref name="stoppingToken"/> がキャンセルされ、かつ Q2 が
    /// 空になるまで実行する（停止時のベストエフォート drain。architecture.md §1.3 手順 2。
    /// 完全な停止順序の保証は M4）。
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
                // stoppingToken 自体がキャンセルされた——溜まっている分は書き込んでから終了する。
                if (batch.Count == 0)
                {
                    return;
                }
            }

            if (batch.Count > 0)
            {
                await WriteBatchWithTimeoutAsync(batch).ConfigureAwait(false);
            }

            if (stoppingToken.IsCancellationRequested && !_q2Reader.TryPeek(out _))
            {
                return;
            }
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

    private async Task WriteBatchWithTimeoutAsync(List<LogRecord> batch)
    {
        using var timeoutCts = new CancellationTokenSource(PipelineConstants.WriteBatchTimeout);

        try
        {
            await _logStore.WriteBatchAsync(batch, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // 応答のないハングを打ち切る（architecture.md §2.1・§3.2.1）。M2 時点はスプールが
            // 無いため、打ち切ったバッチは破棄する（M4 でスプール退避に置き換える）。
            _logger.LogWarning(
                "バッチ書き込みがタイムアウト時間 {Timeout} を超過したため打ち切り、{Count} 件を破棄した（スプール退避は M4 で実装予定）。",
                PipelineConstants.WriteBatchTimeout,
                batch.Count);
        }
        catch (Exception ex)
        {
            // 書き込み例外（一時的なディスクエラー・ロック等）で消費ループを恒久停止させない——
            // ループが止まるとリスナは受信を続けたまま Q2 → Q1 と詰まり、以降の全受信が
            // 内部バッファ破棄になる「黙った縮退」（architecture.md §1.2 が禁じる状態）に陥る。
            // 当該バッチのみ破棄してループを継続する。失敗の分類（一時/恒久）・リトライ・
            // スプール退避・「永続化失敗」カウンタの計上は M4 で実装する。
            _logger.LogWarning(
                ex,
                "バッチ書き込みが失敗したため {Count} 件を破棄し、消費ループを継続する（リトライ・スプール退避は M4 で実装予定）。",
                batch.Count);
        }
    }
}
