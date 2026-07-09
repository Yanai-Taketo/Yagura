using Yagura.Storage;
using Yagura.Storage.SqlServer;

namespace Yagura.Host.Observability.ActiveNotification;

/// <summary>
/// SQL Server Express の DB 容量判定に必要な生データ（database.md §5.3）。
/// </summary>
/// <param name="DatabaseSizeBytes">現在の DB サイズ（バイト。<see cref="LogStoreStatistics.DatabaseSizeBytes"/>）。</param>
/// <param name="MaxDatabaseSizeBytes">Express の上限（バイト。<see cref="SqlServerLogStore.ExpressMaxDatabaseSizeBytes"/>）。</param>
public sealed record ExpressCapacityReading(long DatabaseSizeBytes, long MaxDatabaseSizeBytes)
{
    /// <summary>使用率（0〜1 超もあり得る）。上限 0 以下なら 0。</summary>
    public double UsageRatio => MaxDatabaseSizeBytes <= 0 ? 0 : (double)DatabaseSizeBytes / MaxDatabaseSizeBytes;
}

/// <summary>
/// SQL Server Express の DB 容量接近を判定するための読み取り口（テスト用の差し替え口。
/// 実装は <see cref="LogStoreExpressCapacityChecker"/>）。
/// </summary>
public interface IExpressCapacityChecker
{
    /// <summary>
    /// 現在の provider が SQL Server Express でない場合、または判定自体が失敗した場合は
    /// <c>null</c> を返す（判定対象外・取得不能はどちらも「この周期は警告を出さない」で扱う。
    /// 本 Issue の実装判断——ゲージ・カウンタは独立チャネルとして残るため、本監視の沈黙が
    /// 唯一の観測経路にはならない）。
    /// </summary>
    Task<ExpressCapacityReading?> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IExpressCapacityChecker"/> の既定実装。<see cref="ILogStore"/> の実体が
/// <see cref="SqlServerLogStore"/> の場合のみ実データを返す。
/// </summary>
/// <remarks>
/// <see cref="ILogStore"/> の契約自体には Express 判定を含めない（database.md §1.2 の provider
/// 契約は provider 非依存であり、Express 判定は SQL Server 固有の事情——database.md §5.3。
/// architecture.md §7「本文書からの要求」にも Express 判定は含まれない）。本クラスは
/// Yagura.Host（合成ルート。Program.cs で <see cref="SqlServerLogStore"/> の具体型を直接
/// 扱っている）の側で、具体型への型検査により provider 非依存の契約を汚さずに実現する。
/// </remarks>
public sealed class LogStoreExpressCapacityChecker : IExpressCapacityChecker
{
    private readonly ILogStore _logStore;

    public LogStoreExpressCapacityChecker(ILogStore logStore)
    {
        ArgumentNullException.ThrowIfNull(logStore);
        _logStore = logStore;
    }

    /// <inheritdoc />
    public async Task<ExpressCapacityReading?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_logStore is not SqlServerLogStore sqlServerLogStore)
        {
            return null;
        }

        try
        {
            var isExpress = await sqlServerLogStore.IsExpressEditionAsync(cancellationToken).ConfigureAwait(false);
            if (!isExpress)
            {
                return null;
            }

            var statistics = await sqlServerLogStore.GetStatisticsAsync(cancellationToken).ConfigureAwait(false);
            if (statistics.DatabaseSizeBytes is not { } databaseSizeBytes)
            {
                return null;
            }

            return new ExpressCapacityReading(databaseSizeBytes, SqlServerLogStore.ExpressMaxDatabaseSizeBytes);
        }
        catch (LogStoreWriteException)
        {
            // 判定自体の失敗（DB 未接続・一時障害等）は「取得不能」として扱い、この周期は
            // 判定を見送る（インターフェースの remarks 参照）。
            return null;
        }
    }
}
