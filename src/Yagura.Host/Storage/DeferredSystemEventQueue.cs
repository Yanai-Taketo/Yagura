using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Yagura.Storage;

namespace Yagura.Host.Storage;

/// <summary>
/// 保存先のスキーマ初期化より前に発生し、書き込めなかったシステムイベントを保持し、
/// 初期化の完了後に書き込み直すための小さな待ち行列（Issue #501）。
/// </summary>
/// <remarks>
/// <para>
/// <b>なぜ必要か</b>: <see cref="IngestionHostedService"/> は受信断区間の確定を
/// <b>受信開始の直後</b>に行う（architecture.md §4.4）。一方、保存先のスキーマ初期化は
/// <see cref="StorageInitializationCoordinator"/> が起動経路の外で行う（ADR-0023 決定 1）。
/// この順序は意図的だが、<b>スキーマがまだ存在しない保存先</b>——SQLite から SQL Server へ
/// 昇格した直後の初回起動——では受信断の記録が必ず失敗する。しかもその 1 回は
/// 「昇格に伴う受信断」であり、運用者が最も記録を期待する受信断である。
/// </para>
/// <para>
/// <b>ここで解くこと・解かないこと</b>: 本クラスが引き受けるのは
/// <b>「初期化前だったから書けなかった」だけ</b>である。保存先が長時間落ちている場合の
/// 耐障害な書き込み経路（スプール等を通す）は M5 の契約完全化の管轄であり、本クラスは
/// その代替ではない——だから容量は小さく固定し、あふれた分は<b>黙って消さずに数える</b>。
/// </para>
/// <para>
/// <b>ゲートを通らない書き込み</b>: フラッシュは <see cref="ILogStore.WriteSystemEventAsync"/> を
/// 直接呼ぶ（<c>LogStoreWriteGate</c> を通らない第 4 の経路。<see cref="ILogStore"/> の
/// doc コメント参照）。呼び出し元は <see cref="StorageInitializationCoordinator"/> の
/// 単一実行ゲート（不変条件①）の内側であり、多重実行はそこで排除される。
/// </para>
/// </remarks>
public sealed class DeferredSystemEventQueue
{
    /// <summary>
    /// 保持する上限。受信断イベントは起動 1 回につき最大 1 件のため、通常は 1 件で足りる。
    /// 初期化が長く失敗し続ける環境で青天井に積まないための上限であり、
    /// 「ここに積めば必ず書ける」という保証を与えるための値ではない。
    /// </summary>
    internal const int Capacity = 8;

    private readonly ConcurrentQueue<SystemEvent> _pending = new();
    private int _droppedCount;

    /// <summary>書き込み待ちの件数。</summary>
    public int PendingCount => _pending.Count;

    /// <summary>容量超過で保持できなかった件数（観測用。0 でないことは設計想定の逸脱を示す）。</summary>
    public int DroppedCount => Volatile.Read(ref _droppedCount);

    /// <summary>
    /// 書き込みに失敗したシステムイベントを、初期化完了後の再書き込み対象として預かる。
    /// </summary>
    /// <returns>預かれた場合は <see langword="true"/>。容量超過で捨てた場合は <see langword="false"/>。</returns>
    public bool TryDefer(SystemEvent systemEvent)
    {
        ArgumentNullException.ThrowIfNull(systemEvent);

        // Count と Enqueue の間に競合の余地はあるが、上限は「青天井にしない」ための
        // 目安であり厳密な満杯判定は要らない（超過分は数えて捨てる）。
        if (_pending.Count >= Capacity)
        {
            Interlocked.Increment(ref _droppedCount);
            return false;
        }

        _pending.Enqueue(systemEvent);
        return true;
    }

    /// <summary>
    /// 預かっているイベントを書き込む。**1 件でも失敗したらそこで止め、残りは預かったまま**にする
    /// ——失敗する保存先へ残りを投げ続けても増幅するだけで、次の初期化完了契機で再試行できる。
    /// </summary>
    /// <returns>本呼び出しで書き込めた件数。</returns>
    public async Task<int> FlushAsync(
        ILogStore logStore,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logStore);

        var written = 0;

        while (_pending.TryDequeue(out var systemEvent))
        {
            try
            {
                await logStore.WriteSystemEventAsync(systemEvent, cancellationToken).ConfigureAwait(false);
                written++;
                logger?.LogInformation(
                    "[downtime-recorded-deferred] 保存先の初期化前に記録できなかったシステムイベントを書き込みました: " +
                    "{Kind} {StartAt:o} 〜 {EndAt:o}",
                    systemEvent.Kind,
                    systemEvent.StartAt,
                    systemEvent.EndAt);
            }
            catch (Exception ex)
            {
                // 取り出した 1 件を戻して打ち切る。順序は厳密には保たれないが、
                // システムイベントは時刻を自分で持つため表示・照合に影響しない。
                _pending.Enqueue(systemEvent);
                logger?.LogWarning(
                    ex,
                    "[downtime-record-deferred-retry-failed] 保存先の初期化後に再試行したシステムイベントの書き込みにも" +
                    "失敗しました（次の初期化契機で再試行します）: {Kind} {StartAt:o} 〜 {EndAt:o}",
                    systemEvent.Kind,
                    systemEvent.StartAt,
                    systemEvent.EndAt);
                break;
            }
        }

        return written;
    }
}
