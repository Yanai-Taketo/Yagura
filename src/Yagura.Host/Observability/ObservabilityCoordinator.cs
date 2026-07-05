using Microsoft.Extensions.Logging;
using Yagura.Ingestion.Diagnostics;

namespace Yagura.Host.Observability;

/// <summary>
/// メタデータ領域（§4.3）の起動時読込・定期永続化・停止手順への結線を担う
/// （architecture.md §1.2 起動手順・§1.3 停止手順・§4.3・§4.4）。
/// </summary>
/// <remarks>
/// <para>
/// 独立の <c>IHostedService</c> にはしない——停止手順（§1.3）は「①受信ソケットを閉じてから
/// カウンタを書く」「②drain」「③最終値を書いて正常停止イベントを記録する」という
/// <see cref="Yagura.Ingestion.IngestionPipeline"/> の停止処理と厳密に順序付けられた一連の
/// 手順であり、Generic Host の複数 <c>IHostedService</c> 間の停止順序（登録の逆順）に
/// 依存すると、結合の意図が「登録順」という間接的な事実に埋没する。本クラスは
/// <see cref="IngestionHostedService"/> が直接所有し、その <c>StartAsync</c>/<c>StopAsync</c>
/// の中で明示的に呼び出す設計とした。
/// </para>
/// <para>
/// <b>定期永続化の実装</b>: <see cref="System.Threading.PeriodicTimer"/> を使う
/// （.NET 標準・キャンセル対応・タイマー精度の調整が要らない単純な定期実行に適する）。
/// 間隔は <see cref="ObservabilityConstants.MetadataPersistInterval"/>（M-11 実測確定待ちの
/// 暫定値 10 秒）。
/// </para>
/// </remarks>
public sealed class ObservabilityCoordinator : IAsyncDisposable
{
    private readonly string _dataRoot;
    private readonly IngestionMetrics _metrics;
    private readonly ILogger _logger;

    private PeriodicTimer? _timer;
    private Task? _persistLoopTask;
    private CancellationTokenSource? _stoppingCts;

    public ObservabilityCoordinator(string dataRoot, IngestionMetrics metrics, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _dataRoot = dataRoot;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// 起動時に読み込んだ前回までのメタデータ領域の状態（受信断区間の判定・カウンタの
    /// 引き継ぎに使う）。<see cref="LoadAndSeedAsync"/> 呼び出し後に有効。
    /// </summary>
    public MetadataState PreviousState { get; private set; } = MetadataState.Initial;

    /// <summary>
    /// メタデータ領域を読み込み、カウンタ累積値を <see cref="IngestionMetrics"/> へ引き継ぐ
    /// （architecture.md §4.3「起動時に復元して継続する」）。パイプラインの受信開始
    /// （<see cref="IngestionHostedService.StartAsync"/> 手順 2）より前に呼ぶ想定——
    /// カウンタの加算が始まる前に前回値を種として設定する必要があるため。
    /// </summary>
    public void LoadAndSeed()
    {
        PreviousState = MetadataStore.Read(_dataRoot, _logger);
        _metrics.SeedCumulativeCounters(PreviousState.Counters);
    }

    /// <summary>
    /// 定期永続化ループを開始する（§4.3「一定間隔でメタデータ領域に永続化」・
    /// §4.4「稼働中は一定間隔で生存時刻をメタデータ領域に更新する」）。
    /// </summary>
    public void StartPeriodicPersistence()
    {
        if (_timer is not null)
        {
            throw new InvalidOperationException("定期永続化ループは既に開始されている。");
        }

        _stoppingCts = new CancellationTokenSource();
        _timer = new PeriodicTimer(ObservabilityConstants.MetadataPersistInterval);
        _persistLoopTask = Task.Run(() => PersistLoopAsync(_stoppingCts.Token));
    }

    private async Task PersistLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _timer!.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                PersistCurrentCountersAndLiveness();
            }
        }
        catch (OperationCanceledException)
        {
            // 停止要求による正常終了（PeriodicTimer.Dispose/トークンキャンセルのいずれか）。
        }
    }

    /// <summary>
    /// 現在のカウンタ累積値・生存時刻を書く（定期永続化ループ本体。停止イベントは含めない
    /// ——正常停止時の停止イベント記録は <see cref="WriteStopStep1Async"/>／
    /// <see cref="WriteStopStep3Async"/> が専用に行う）。
    /// </summary>
    private void PersistCurrentCountersAndLiveness()
    {
        var snapshot = _metrics.SnapshotCumulativeCounters();
        var state = new MetadataState(snapshot, LastStopEvent: null, LastLivenessAt: DateTimeOffset.UtcNow);
        MetadataStore.Save(_dataRoot, state);
    }

    /// <summary>
    /// 停止手順 1（architecture.md §1.3）: 受信ソケットを閉じた直後に呼ぶ。その時点の
    /// カウンタをメタデータ領域へ書く（正常停止イベントはまだ記録しない——手順 3 で記録する。
    /// 手順 1 の時点ではまだ drain（手順 2）が完了しておらず「正常停止した」とは言えないため）。
    /// </summary>
    /// <param name="receiveSocketClosedAt">受信ソケットを閉じた時刻（§4.4 の受信断区間の開始点）。</param>
    public void WriteStopStep1(DateTimeOffset receiveSocketClosedAt)
    {
        var snapshot = _metrics.SnapshotCumulativeCounters();
        var state = new MetadataState(snapshot, LastStopEvent: null, LastLivenessAt: receiveSocketClosedAt);
        MetadataStore.Save(_dataRoot, state);

        _receiveSocketClosedAt = receiveSocketClosedAt;
    }

    private DateTimeOffset? _receiveSocketClosedAt;

    /// <summary>
    /// 停止手順 3（architecture.md §1.3）: drain（手順 2）完了後、カウンタを最終値で永続化し、
    /// 正常停止イベントを記録する。<see cref="WriteStopStep1"/> を先に呼んでいない場合
    /// （防御的フォールバック）は <paramref name="fallbackReceiveSocketClosedAt"/> を
    /// 区間開始として使う。
    /// </summary>
    public void WriteStopStep3(DateTimeOffset stoppedAt, DateTimeOffset fallbackReceiveSocketClosedAt)
    {
        var receiveSocketClosedAt = _receiveSocketClosedAt ?? fallbackReceiveSocketClosedAt;
        var snapshot = _metrics.SnapshotCumulativeCounters();
        var stopEvent = new StopEventRecord(receiveSocketClosedAt, stoppedAt);
        var state = new MetadataState(snapshot, stopEvent, LastLivenessAt: stoppedAt);
        MetadataStore.Save(_dataRoot, state);
    }

    /// <summary>
    /// 定期永続化ループを停止する（Dispose 前の明示停止。<see cref="StopAsync"/> と同義だが、
    /// 停止手順の一部として明示的に呼べるよう独立メソッドにしている）。
    /// </summary>
    public async Task StopAsync()
    {
        if (_stoppingCts is null)
        {
            return;
        }

        _stoppingCts.Cancel();
        _timer?.Dispose();

        if (_persistLoopTask is not null)
        {
            await _persistLoopTask.ConfigureAwait(false);
        }

        _stoppingCts.Dispose();
        _stoppingCts = null;
        _timer = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
