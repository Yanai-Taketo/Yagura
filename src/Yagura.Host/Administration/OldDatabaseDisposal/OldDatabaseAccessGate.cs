namespace Yagura.Host.Administration.OldDatabaseDisposal;

/// <summary>旧・組み込み DB ファイルの利用権を持っている主体。</summary>
public enum OldDatabaseAccessHolder
{
    /// <summary>誰も持っていない。</summary>
    None,

    /// <summary>移行画面の状態取得・処分の確認材料の読み取り（短時間）。</summary>
    Inspection,

    /// <summary>蓄積ログの移行（database.md §6.2）。</summary>
    Migration,

    /// <summary>旧ファイルの処分（database.md §6.3）。</summary>
    Disposal,
}

/// <summary>
/// 旧・組み込み DB ファイルの利用権の排他（database.md §6.3 前提条件 2）。移行・処分・旧ファイルを
/// 開く状態取得が同じ 1 本を取り合う——処分の実行中は移行を開始できず、移行の実行中は処分を開始できない。
/// </summary>
/// <remarks>
/// 保持者の種類（<see cref="Holder"/>）は状態表の判定（移行の実行中 / 処分の実行中）にも使うため、
/// 取得と同時に記録し、解放と同時に消す。スレッド安全。
/// </remarks>
public sealed class OldDatabaseAccessGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private int _holder;

    /// <summary>現在の保持者。</summary>
    public OldDatabaseAccessHolder Holder => (OldDatabaseAccessHolder)Volatile.Read(ref _holder);

    /// <summary>
    /// 利用権の取得を試みる。<paramref name="wait"/> の間に取れなければ <see langword="null"/>。
    /// 戻り値の破棄で解放する。
    /// </summary>
    public async Task<IDisposable?> TryAcquireAsync(
        OldDatabaseAccessHolder holder,
        TimeSpan wait,
        CancellationToken cancellationToken = default)
    {
        if (holder == OldDatabaseAccessHolder.None)
        {
            throw new ArgumentOutOfRangeException(nameof(holder));
        }

        if (!await _semaphore.WaitAsync(wait, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        Volatile.Write(ref _holder, (int)holder);
        return new Lease(this);
    }

    private void Release()
    {
        Volatile.Write(ref _holder, (int)OldDatabaseAccessHolder.None);
        _semaphore.Release();
    }

    private sealed class Lease(OldDatabaseAccessGate owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}
