using Yagura.Abstractions.Observability;

namespace Yagura.TestSupport.Fakes;

/// <summary>
/// <see cref="IYaguraSystemStatusReader"/>のフェイク。既定値は「カウンタ・スプール・受信ポートが
/// 一通り揃った通常運用中」の状態(閲覧画面の描画テストが個々に空・異常系だけを上書きしやすいよう、
/// 既定を「何もない」ではなく「典型状態」にする)。空・null を既定にしたいテストは対応プロパティを
/// 明示的に上書きする。
/// </summary>
public sealed class FakeStatusReader : IYaguraSystemStatusReader
{
    public List<YaguraCounterReading> Counters { get; init; } =
    [
        new("yagura.ingestion.internal_buffer.dropped", 0, IsLoss: true),
        new("yagura.ingestion.spool.evacuated", 0, IsLoss: false),
    ];

    public YaguraSpoolReading? Spool { get; init; } = new(CurrentUsageBytes: 1024, QuotaBytes: 1024 * 1024);

    public bool SpoolDegraded { get; init; }

    public YaguraHealthReading Health { get; init; } = YaguraHealthReading.Ok;

    public int? RetentionDays { get; init; } = 30;

    public List<YaguraListenerEndpoint> Listeners { get; init; } =
        [new YaguraListenerEndpoint("UDP", 514)];

    public YaguraSystemStatusSnapshot ReadCurrent() => new(
        TakenAt: DateTimeOffset.UtcNow,
        Counters: Counters,
        Spool: Spool,
        SpoolDegraded: SpoolDegraded,
        Health: Health,
        RetentionDays: RetentionDays,
        Listeners: Listeners);

    public List<YaguraFlowControlRejectionReading> FlowControlRejections { get; init; } = [];

    public IReadOnlyList<YaguraFlowControlRejectionReading> ReadFlowControlRejections(int maxCount) =>
        FlowControlRejections.Take(maxCount).ToList();

    public List<YaguraSourceSilenceReading> SourceSilenceEntries { get; init; } = [];

    public IReadOnlyList<YaguraSourceSilenceReading> ReadSourceSilenceEntries() => SourceSilenceEntries;
}
