using Yagura.Storage;

namespace Yagura.TestSupport.Fakes;

/// <summary>
/// <see cref="ILogStore"/>のテスト用二重体基底。既定は全メンバー<see cref="NotSupportedException"/>で、
/// 各テストは実際に使う操作だけ override する——Fake の定義そのものが「このテストが何を使うか」を
/// 表す(未 override のメンバーが呼ばれれば、そのテストの想定漏れとして直ちに失敗する)。
/// </summary>
public abstract class LogStoreTestDouble : ILogStore
{
    public virtual Task InitializeAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual Task<IReadOnlyList<LogRecordSummary>> QueryLatestAsync(
        int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual Task<IReadOnlyList<LogRecordSummary>> QueryAsync(
        LogQuery query, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual Task WriteSystemEventAsync(SystemEvent systemEvent, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual Task<DeleteOlderThanResult> DeleteOlderThanAsync(
        DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual Task<LogStoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual Task<LogRecord?> FindByIdAsync(
        long id, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual Task<IReadOnlyList<SystemEvent>> QuerySystemEventsAsync(
        DateTimeOffset? from, DateTimeOffset? to, int limit, TimeSpan timeout, string? kind = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual Task<IReadOnlyList<SourceActivity>> QuerySourceActivityAsync(
        int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual Task<IReadOnlyList<SeverityCount>> QuerySeverityDistributionAsync(
        DateTimeOffset from, DateTimeOffset to, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual Task<IReadOnlyList<SourceActivity>> QueryTopTalkersAsync(
        DateTimeOffset from, DateTimeOffset to, int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
