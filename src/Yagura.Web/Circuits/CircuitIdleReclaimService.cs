using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yagura.Web.Diagnostics;

namespace Yagura.Web.Circuits;

/// <summary>
/// 無操作 circuit の定期回収（SEC-8。security.md §2.2「一定時間操作のない circuit を切断して
/// 枠を解放する」。M8-4。Issue #71）。
/// </summary>
/// <remarks>
/// タイムアウト値・「操作」の定義は仮値（<see cref="CircuitGovernanceDefaults"/> の remarks）。
/// 回収の判定と切断要求は <see cref="CircuitRegistry.ReclaimIdleAsync"/> に集約してあり
/// （単体テストの対象）、本サービスは定期実行の器のみを担う。回収は管理「操作」ではなく
/// 統治機構の自動動作のため監査記録（2000 番台）の対象にせず、カウンタで観測可能にする。
/// </remarks>
public sealed class CircuitIdleReclaimService : BackgroundService
{
    private readonly CircuitRegistry _registry;
    private readonly WebGuardMetrics _metrics;
    private readonly ILogger<CircuitIdleReclaimService> _logger;
    private readonly TimeProvider _timeProvider;

    public CircuitIdleReclaimService(
        CircuitRegistry registry,
        WebGuardMetrics metrics,
        ILogger<CircuitIdleReclaimService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _metrics = metrics;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CircuitGovernanceDefaults.IdleScanInterval, _timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var reclaimed = await _registry.ReclaimIdleAsync(_timeProvider.GetUtcNow()).ConfigureAwait(false);

                foreach (var record in reclaimed)
                {
                    _metrics.RecordCircuitIdleReclaimed();
                    _logger.LogInformation(
                        "無操作 circuit を回収しました（SEC-8 仮値）: {CircuitId} 最終活動 {LastActivityAt:O}",
                        record.CircuitId,
                        record.LastActivityAt);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
    }
}
