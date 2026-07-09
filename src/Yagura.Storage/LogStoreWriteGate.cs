namespace Yagura.Storage;

/// <summary>
/// <see cref="ILogStore"/> の「書き込みは単一 writer が呼び出す」契約（<see cref="ILogStore"/>
/// 冒頭の doc コメント参照）を実配線で満たすための、プロセス内書き込みゲート（Issue #151）。
/// </summary>
/// <remarks>
/// <para>
/// <b>背景</b>: 実際には (a) 永続化段の <c>PersistenceWriter</c>（ライブ書き込み）・
/// (b) <c>SpoolDrainCoordinator</c>（drain）・(c) <c>RetentionScheduler</c>（保持期間削除 +
/// 実行記録のシステムイベント）の 3 経路が、独立したタスクから並行して <see cref="ILogStore"/>
/// の書き込み系メソッド（<see cref="ILogStore.WriteBatchAsync"/>・
/// <see cref="ILogStore.WriteSystemEventAsync"/>・<see cref="ILogStore.DeleteOlderThanAsync"/>）を
/// 呼び出し得る。SQLite は busy_timeout（既定 30 秒）による再試行と、削除のチャンクごとの
/// コネクション開閉が生むロック解放の組み合わせで「たまたま」並行呼び出しに耐えているに
/// 過ぎず、真に単一 writer を要求する provider（将来の PostgreSQL/MySQL 追加。database.md
/// §1.1）では例外・デッドロックとして表面化し得る。なお、ホスト起動処理の受信断記録
/// （<c>IngestionHostedService.StartAsync</c> の <see cref="ILogStore.WriteSystemEventAsync"/>
/// 直接呼び出し）は本ゲートを通らない第 4 の書き込み経路だが、上記 3 経路のいずれもまだ
/// 開始されていない時点で実行されるため、非同時実行は起動順序により保証される
/// （ゲートによる保証ではない。<see cref="ILogStore"/> の doc コメント参照）。
/// </para>
/// <para>
/// <b>設置場所の設計判断</b>: ホスト（<c>Yagura.Host.Program</c>）が単一のインスタンスを
/// 構築し、3 経路すべて（<c>IngestionPipeline</c> 経由の <c>PersistenceWriter</c>・
/// <c>SpoolDrainCoordinator</c>、および <c>RetentionScheduler</c>）へ同じインスタンスを渡す
/// ——Issue #151 が挙げるもう一方の代替案（<see cref="ILogStore"/> 実装内に排他を持たせる）は
/// 採らない。理由: 排他は provider の実装詳細ではなく「呼び出し側が直列化する」という
/// 既存契約（<see cref="ILogStore"/> の doc コメント）をそのまま体現でき、かつ provider ごとに
/// 同じ排他コードを重複させずに済むため。クラス自体は <see cref="ILogStore"/> にのみ依存し
/// Yagura.Host を参照しないため Yagura.Storage に置く（受信・ホストの双方が参照できる層
/// ——architecture.md §1.1「受信・UI → 永続化の抽象インターフェースのみ」参照）。
/// </para>
/// <para>
/// <b>ゲート待ちのタイムアウトを DB 操作のタイムアウトから分離する</b>: 呼び出し元
/// （<c>PersistenceWriter</c>・<c>SpoolDrainCoordinator</c>）は、ゲート取得の待ち時間と
/// 実際の DB 書き込みの待ち時間を同じ時間予算で縛らない。同じ予算にすると、保持期間削除
/// （<see cref="ILogStore.DeleteOlderThanAsync"/> 呼び出し全体を通じてゲートを保持する——
/// 理由は次項）が長時間ゲートを保持している間、ライブ書き込みの毎バッチが「ゲート待ちだけで」
/// 既存の書き込みタイムアウト（<c>PipelineConstants.WriteBatchTimeout</c>。既定 10 秒）を
/// 使い切り、DB 自体は健全なのに「速度不足」と誤認してスプール退避が連発する
/// （database.md §3「保持期間削除の実行中に発生したスプール退避は昇格案内の『速度不足』に
/// 数えない」が名指しするリスクそのもの）。本クラスは <see cref="AcquireAsync(TimeSpan, CancellationToken)"/>
/// でゲート専用の待ちタイムアウトを独立して受け取れるようにし、呼び出し元がゲート待ち予算
/// （<c>PipelineConstants.WriteGateAcquireTimeout</c>。既定は <c>WriteBatchTimeout</c> より
/// 大きい）と DB 操作予算（既存の <c>WriteBatchTimeout</c>。ゲート取得後にあらためて計測を
/// 始める）を別々に管理できるようにする。ゲート取得自体が失敗（タイムアウト）した場合、
/// 呼び出し元は既存のスプール退避経路（ライブ）・未消化のまま残す経路（drain）へ、
/// 「DB がタイムアウトした」場合と区別できるログを添えてそのまま合流させる——データを
/// 失わない設計（at-least-once。architecture.md §2.2）は変えない。
/// </para>
/// <para>
/// <b>DeleteOlderThanAsync はチャンク単位でゲートを解放しない</b>: <see cref="ILogStore.DeleteOlderThanAsync"/>
/// は内部で複数チャンクに分けて削除を繰り返す実装（分割実行。database.md §3）だが、その
/// チャンク境界は呼び出し元（<c>RetentionScheduler</c>）から見えないブラックボックスである。
/// チャンクごとにゲートを解放・再取得するには <see cref="ILogStore"/> の契約変更（チャンク単位の
/// コールバック等）が要り、本 Issue のスコープを超える——正しさ（3 経路の真の直列化）を
/// 優先する意図的なトレードオフとして、削除呼び出し全体を単一のゲート保持区間とする。
/// この間、ライブ・drain 書き込みはゲート待ちタイムアウト後にスプールへ退避（または
/// 未消化のまま残り）、削除完了後に通常どおり drain で回収される——データは失われない。
/// 分割実行の粒度（<c>RetentionConstants.DeleteBatchMaxSize</c>。既定 1,000 件）を小さく保つ
/// ことで、1 回の保持期間削除がゲートを保持する実時間は実務上有限に収まる想定だが、
/// 大量のキャッチアップ削除（Issue #150）ではこの保持時間が長くなり得ることを申し送る
/// （将来、チャンク単位のゲート解放が必要になった場合は <see cref="ILogStore"/> の契約拡張を
/// 検討する）。
/// </para>
/// </remarks>
public sealed class LogStoreWriteGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    /// ゲートを取得する。<paramref name="cancellationToken"/> のキャンセルのみで待ちを打ち切る
    /// （固定タイムアウトなし）。<c>RetentionScheduler</c> のように、緊急性より完遂を優先する
    /// 呼び出し元向け。
    /// </summary>
    /// <returns>解放するには <see cref="IDisposable.Dispose"/> を呼ぶこと（<c>using</c> 推奨）。</returns>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_semaphore);
    }

    /// <summary>
    /// ゲートを取得する。<paramref name="timeout"/> 以内に取得できなければ
    /// <see cref="LogStoreWriteGateTimeoutException"/> を送出する。ライブ書き込み・drain 等、
    /// 既存のタイムアウト・スプール退避経路へ合流させたい呼び出し元向け（本クラスの
    /// doc コメント「ゲート待ちのタイムアウトを DB 操作のタイムアウトから分離する」参照）。
    /// </summary>
    /// <returns>解放するには <see cref="IDisposable.Dispose"/> を呼ぶこと（<c>using</c> 推奨）。</returns>
    /// <exception cref="LogStoreWriteGateTimeoutException">
    /// <paramref name="timeout"/> 以内にゲートを取得できなかった場合。
    /// </exception>
    public async Task<IDisposable> AcquireAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await _semaphore.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new LogStoreWriteGateTimeoutException(timeout);
        }

        return new Lease(_semaphore);
    }

    public void Dispose() => _semaphore.Dispose();

    /// <summary>取得済みゲートの解放を 1 回だけに限定する薄いラッパ（二重 Dispose を安全にする）。</summary>
    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
