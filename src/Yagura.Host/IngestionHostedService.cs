using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yagura.Ingestion;
using Yagura.Storage;

namespace Yagura.Host;

/// <summary>
/// Generic Host のライフサイクルに <see cref="IngestionPipeline"/> を結線する
/// （architecture.md §1.2 起動順序・§1.3 停止順序）。
/// </summary>
/// <remarks>
/// <para>
/// <b>起動順序は受信先行</b>（§1.2）: <see cref="StartAsync"/> はまずリスナの listen を
/// 開始し、その後 DB の <see cref="ILogStore.InitializeAsync"/> を待ってから消費ループ
/// （解析段・永続化段）を開始する。DB 初期化の完了までの間に受信したデータグラムは
/// Q1・Q2 が緩衝する。
/// </para>
/// <para>
/// Windows サービス統合（<c>UseWindowsService()</c> 等）は M3 で行う。M2 時点は
/// コンソールプロセスとして Generic Host 常駐にする。
/// </para>
/// </remarks>
public sealed class IngestionHostedService : IHostedService
{
    private readonly IngestionPipeline _pipeline;
    private readonly ILogStore _logStore;
    private readonly ILogger<IngestionHostedService> _logger;

    public IngestionHostedService(IngestionPipeline pipeline, ILogStore logStore, ILogger<IngestionHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(logStore);
        ArgumentNullException.ThrowIfNull(logger);

        _pipeline = pipeline;
        _logStore = logStore;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 手順 2（§1.2）: 受信ソケットを開き、受信を開始する。DB 初期化より先に行う。
        // UDP・TCP は同時に開始する（M4-1 依頼「起動順序: UDP と同時（受信先行の一部）」）。
        await _pipeline.StartListenerAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("UDP syslog listener started on port {Port}.", _pipeline.BoundPort);
        _logger.LogInformation("TCP syslog listener started on port {Port}.", _pipeline.TcpBoundPort);

        // 手順 3（§1.2）: DB provider を初期化する。完了までの間は Q1・Q2 が緩衝になる
        // （スプールへの退避は M4。M2 時点は Q2 の容量とバックプレッシャで持ちこたえる）。
        await _logStore.InitializeAsync(cancellationToken).ConfigureAwait(false);

        _pipeline.StartConsumers();
        _logger.LogInformation("Ingestion pipeline consumers (parsing/persistence) started.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // architecture.md §1.3: リスナ停止 → Q1/Q2 を drain して書き切れる分は書く
        // （ベストエフォート。完全な停止順序保証は M4）。
        await _pipeline.StopAsync().ConfigureAwait(false);
        _logger.LogInformation("Ingestion pipeline stopped.");
    }
}
