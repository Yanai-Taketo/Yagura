using System.Diagnostics.Metrics;
using Yagura.Ingestion.Diagnostics;

namespace Yagura.Ingestion.Tests.Diagnostics;

/// <summary>
/// OS 統計突合ゲージ（<c>yagura.os.udp.*</c>）が存在しないことの回帰テスト
/// （ADR-0016 決定 3。Issue #308）。
/// </summary>
/// <remarks>
/// 本ゲージは検証済み環境で受信・破棄を反映しないことが実測確定し（M7-2。
/// architecture.md §4.2「覆域の限界」）、製品コードから撤去された。再導入は
/// ADR-0016 再評価トリガ (d) 陽性時の amendment を要する——本テストは
/// amendment を経ない無自覚な復活（事実上の決定覆し）を機構で防ぐ。
/// </remarks>
public sealed class IngestionMetricsOsUdpAbsenceTests
{
    [Fact]
    public void YaguraMeter_PublishesNoOsUdpInstruments()
    {
        var published = new List<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == IngestionMetrics.MeterName)
                {
                    lock (published)
                    {
                        published.Add(instrument.Name);
                    }
                }
            },
        };
        listener.Start();

        using var metrics = new IngestionMetrics();

        lock (published)
        {
            Assert.NotEmpty(published);
            Assert.DoesNotContain(published, name =>
                name.StartsWith("yagura.os.udp.", StringComparison.Ordinal));
        }
    }
}
