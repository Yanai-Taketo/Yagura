using System.Threading.Channels;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.Parsing;
using Yagura.Ingestion.Udp;
using Yagura.Storage;

namespace Yagura.Ingestion.Tests.Parsing;

/// <summary>
/// 解析段が解析失敗（保存済み）・TCP 不完全メッセージの 2 種を計器へ計上すること
/// （architecture.md §4.1「破棄・異常は必ず計上する」。Issue #270）。
/// </summary>
/// <remarks>
/// どちらも生データのまま保存され損失ではないが、発生の計測が原則の穴になっていた分を埋める。
/// 計上は解析直後の単一点（<c>ParsingStage.ParseAndCount</c>）で行われ、後段の投入/退避経路に
/// よらないため、Q2 へ正常投入される経路（読み手あり）でも確実に計上されることを確認する。
/// </remarks>
public sealed class ParsingStageParseCounterTests
{
    private static RawDatagram Datagram(string payload, bool incomplete = false) =>
        new(
            ReceivedAt: DateTimeOffset.UtcNow,
            SourceAddress: "10.0.0.1",
            SourcePort: 514,
            Protocol: incomplete ? Protocol.Tcp : Protocol.Udp,
            Payload: System.Text.Encoding.UTF8.GetBytes(payload),
            Incomplete: incomplete);

    private static async Task<(long ParseFailed, long Incomplete)> RunAndCountAsync(
        IReadOnlyList<RawDatagram> inputs)
    {
        var q1 = Channel.CreateBounded<RawDatagram>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = true,
        });
        // Q2 は容量に余裕を持たせ、読み手を置いて詰まらせない（正常投入経路を通す）。
        var q2 = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = true,
        });

        using var metrics = new IngestionMetrics();
        using var parseFailed = new MetricCollector<long>(metrics.ParseFailedSavedCounter);
        using var incomplete = new MetricCollector<long>(metrics.TcpIncompleteMessageCounter);

        var stage = new ParsingStage(q1.Reader, q2.Writer, spool: null, metrics);
        using var cts = new CancellationTokenSource();
        var run = Task.Run(() => stage.RunAsync(cts.Token));

        // Q2 の読み手（詰まり防止のため捨てるだけ）。
        var drain = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in q2.Reader.ReadAllAsync(cts.Token))
                {
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        foreach (var d in inputs)
        {
            await q1.Writer.WriteAsync(d);
        }

        // 全件が解析されるまで待つ（計上は解析直後）。
        var expected = inputs.Count;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var pf = parseFailed.GetMeasurementSnapshot().Sum(m => m.Value);
            var inc = incomplete.GetMeasurementSnapshot().Sum(m => m.Value);
            if (pf + inc >= inputs.Count(i => i.Incomplete || !HasValidPri(i)))
            {
                break;
            }

            await Task.Delay(25);
        }

        cts.Cancel();
        await run;
        await drain;

        return (
            parseFailed.GetMeasurementSnapshot().Sum(m => m.Value),
            incomplete.GetMeasurementSnapshot().Sum(m => m.Value));
    }

    private static bool HasValidPri(RawDatagram d)
    {
        var s = System.Text.Encoding.UTF8.GetString(d.Payload);
        return s.StartsWith("<", StringComparison.Ordinal) && s.Contains('>');
    }

    [Fact]
    public async Task ParseFailedRecords_AreCounted()
    {
        var inputs = new[]
        {
            Datagram("no-pri-at-all"),      // PRI 不在 → ParseFailed
            Datagram("also invalid"),        // PRI 不在 → ParseFailed
            Datagram("<34>Oct 11 valid"),    // 有効 PRI → 計上しない
        };

        var (parseFailed, incomplete) = await RunAndCountAsync(inputs);

        Assert.Equal(2, parseFailed);
        Assert.Equal(0, incomplete);
    }

    [Fact]
    public async Task IncompleteMessages_AreCounted()
    {
        var inputs = new[]
        {
            Datagram("<34>partial", incomplete: true), // Incomplete フラグ優先 → Incomplete
            Datagram("<34>whole message"),             // 正常 → 計上しない
        };

        var (parseFailed, incomplete) = await RunAndCountAsync(inputs);

        Assert.Equal(0, parseFailed);
        Assert.Equal(1, incomplete);
    }

    [Fact]
    public async Task ValidRecords_AreNotCounted()
    {
        var inputs = new[]
        {
            Datagram("<34>Oct 11 22:14:15 host app: ok"),
            Datagram("<13>another valid one"),
        };

        var (parseFailed, incomplete) = await RunAndCountAsync(inputs);

        Assert.Equal(0, parseFailed);
        Assert.Equal(0, incomplete);
    }
}
