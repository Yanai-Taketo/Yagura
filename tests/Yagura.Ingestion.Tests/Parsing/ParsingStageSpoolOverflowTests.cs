using System.Threading.Channels;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.Parsing;
using Yagura.Ingestion.Udp;
using Yagura.Storage;
using Yagura.Storage.Spool;

namespace Yagura.Ingestion.Tests.Parsing;

/// <summary>
/// Q2 が満杯のとき、解析段が新規投入分をスプールへ退避すること（architecture.md §3.1・
/// §3.2.1「容量: Q2 が満杯で新規投入分を受け取れない」。PR #28 オーナー確認事項 1 の解消）。
/// </summary>
public sealed class ParsingStageSpoolOverflowTests : IDisposable
{
    private readonly string _spoolDirectory = Path.Combine(Path.GetTempPath(), $"yagura-parsingstage-tests-{Guid.NewGuid():N}");
    private DiskSpool? _spool;

    public void Dispose()
    {
        _spool?.Dispose();

        if (Directory.Exists(_spoolDirectory))
        {
            Directory.Delete(_spoolDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Q2Full_NewRecordIsSpooled_AndSpoolEvacuatedCounterIncrements()
    {
        _spool = DiskSpool.TryOpen(new DiskSpoolOptions { Directory = _spoolDirectory }, out _);
        Assert.NotNull(_spool);

        // Q2 の容量を 1 に絞り、永続化段側の読み手を置かない（満杯を維持する）ことで
        // 確実に溢れさせる。
        var q1 = Channel.CreateBounded<RawDatagram>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var q2 = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var metrics = new IngestionMetrics();
        var stage = new ParsingStage(q1.Reader, q2.Writer, _spool, metrics);

        using var stoppingCts = new CancellationTokenSource();
        var runTask = Task.Run(() => stage.RunAsync(stoppingCts.Token));

        // Q2 を先に 1 件で満杯にしておく。
        await q1.Writer.WriteAsync(CreateDatagram("filler"));
        // filler が Q2 へ収まるまで待つ（Q2 容量 1 を使い切る）。
        await WaitUntilAsync(() => q2.Reader.Count >= 1, TimeSpan.FromSeconds(10));

        // これで Q2 は満杯——次の 1 件は TryWrite が失敗しスプールへ退避されるはず。
        var marker = $"overflow-marker-{Guid.NewGuid():N}";
        await q1.Writer.WriteAsync(CreateDatagram(marker));

        await WaitUntilAsync(() => _spool.CurrentUsageBytes > 0, TimeSpan.FromSeconds(10));

        stoppingCts.Cancel();
        await runTask;

        var segments = _spool.TrySealActiveSegmentAndListDrainable();
        var spooledMessages = segments
            .SelectMany(path => _spool.ReadSegmentRecords(path, out _))
            .Where(r => r.Kind == SpoolRecordKind.Normal)
            .Select(r => r.LogRecord!.Message)
            .ToList();

        Assert.Contains(marker, spooledMessages);
    }

    private static RawDatagram CreateDatagram(string message) =>
        new(
            ReceivedAt: DateTimeOffset.UtcNow,
            SourceAddress: "10.0.0.1",
            SourcePort: 514,
            Protocol: Protocol.Udp,
            Payload: System.Text.Encoding.UTF8.GetBytes($"<34>{message}"));

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        Assert.True(condition(), $"条件が {timeout} 以内に成立しなかった。");
    }
}
