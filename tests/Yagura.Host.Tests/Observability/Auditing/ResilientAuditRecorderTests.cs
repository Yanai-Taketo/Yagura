using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Time.Testing;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Observability.Auditing;
using Yagura.Web.Diagnostics;

namespace Yagura.Host.Tests.Observability.Auditing;

/// <summary>
/// <see cref="ResilientAuditRecorder"/> の単体テスト（SEC-10。security.md §4.2。Issue #269）。
/// </summary>
/// <remarks>
/// 検証観点: (1) 正常時は保持しない、(2) ファイル書き込み失敗時にメモリ内保持、(3) 復旧後（新規事象
/// 契機・周期スキャン契機の両方）に発生日ファイルへ書き戻す、(4) 書き戻し事象に遅延記録の印を付ける、
/// (5) 全件書き戻し後に復旧サマリ（3013）を「欠落し得た期間・書き戻し件数・縮退破棄件数」つきで残す、
/// (6) 保持上限超過で縮退破棄し、件数を計器に計上する。
/// </remarks>
/// <remarks>
/// 実チャネル障害は <see cref="FileAuditRecorder"/> の出力先（<c>&lt;dataRoot&gt;/audit</c>）と同名の
/// <b>ファイル</b>を置いて <c>Directory.CreateDirectory</c> を失敗させることで再現する（＝アプリ記録
/// ファイル書き込み失敗）。イベントログ併記はテストロガーで成功するため、内側の <c>TryRecord</c> は
/// 「ファイル失敗・イベントログ成功」を返す——SEC-10 の保持条件（正本に残らなかった）を満たす。
/// </remarks>
public sealed class ResilientAuditRecorderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), $"yagura-resilient-audit-{Guid.NewGuid():N}");
    private readonly string _auditDir;
    private readonly WebGuardMetrics _metrics = new();

    public ResilientAuditRecorderTests()
    {
        Directory.CreateDirectory(_dataRoot);
        _auditDir = Path.Combine(_dataRoot, FileAuditRecorder.DirectoryName);
    }

    public void Dispose()
    {
        _metrics.Dispose();
        try
        {
            if (File.Exists(_auditDir))
            {
                File.Delete(_auditDir);
            }
            if (Directory.Exists(_dataRoot))
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // ベストエフォート。
        }
    }

    private FileAuditRecorder CreateInner() =>
        new(_dataRoot, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, _metrics);

    private ResilientAuditRecorder CreateSut(FakeTimeProvider time, int? maxBuffered = null) =>
        new(CreateInner(), _metrics, time, logger: null, maxBufferedEvents: maxBuffered);

    /// <summary>アプリ記録ファイル書き込みを失敗させる（audit ディレクトリ位置に同名ファイルを置く）。</summary>
    private void BlockChannel() => File.WriteAllText(_auditDir, "block");

    /// <summary>チャネルを復旧させる（阻害ファイルを除去）。</summary>
    private void UnblockChannel() => File.Delete(_auditDir);

    /// <summary>
    /// 監査ファイルの各行を <c>(Kind, Detail)</c> に解して返す。ファイル上の JSON は非 ASCII を
    /// <c>\uXXXX</c> でエスケープするため、生文字列一致ではなくデシリアライズして値を取り出す。
    /// </summary>
    private IReadOnlyList<(string Kind, string Detail)> ReadAuditLines()
    {
        if (!Directory.Exists(_auditDir))
        {
            return Array.Empty<(string, string)>();
        }

        return Directory.GetFiles(_auditDir, FileAuditRecorder.AuditFileSearchPattern)
            .SelectMany(File.ReadAllLines)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l =>
            {
                using var doc = System.Text.Json.JsonDocument.Parse(l);
                var root = doc.RootElement;
                var kind = root.TryGetProperty("Kind", out var k) ? k.GetString() ?? string.Empty : string.Empty;
                var detail = root.TryGetProperty("Detail", out var d) ? d.GetString() ?? string.Empty : string.Empty;
                return (kind, detail);
            })
            .ToList();
    }

    private static AuditEvent Sample(string detail) => new(
        OccurredAt: Now,
        Kind: AuditEventKind.ConfigurationSaved,
        RemoteAddress: "203.0.113.9",
        RemotePort: 443,
        Detail: detail);

    [Fact]
    public async Task ChannelHealthy_DoesNotBuffer()
    {
        var time = new FakeTimeProvider(Now);
        var sut = CreateSut(time);

        await sut.RecordAsync(Sample("ok"));

        Assert.Equal(0, sut.BufferedCount);
        Assert.Single(ReadAuditLines());
    }

    [Fact]
    public async Task FileWriteFails_EventIsBuffered_NotInFile()
    {
        var time = new FakeTimeProvider(Now);
        var sut = CreateSut(time);
        BlockChannel();

        await sut.RecordAsync(Sample("during-outage"));

        Assert.Equal(1, sut.BufferedCount);
        UnblockChannel();
        Assert.Empty(ReadAuditLines()); // まだ書かれていない
    }

    [Fact]
    public async Task Recovery_ViaNewEvent_WritesBackWithDeferredMarker_AndSummary()
    {
        var time = new FakeTimeProvider(Now);
        var sut = CreateSut(time);

        BlockChannel();
        await sut.RecordAsync(Sample("outage-1"));
        await sut.RecordAsync(Sample("outage-2"));
        Assert.Equal(2, sut.BufferedCount);

        // 復旧。次の新規事象の書き込み成功が書き戻しの契機になる。
        UnblockChannel();
        await sut.RecordAsync(Sample("after-recovery"));

        Assert.Equal(0, sut.BufferedCount);
        var lines = ReadAuditLines();

        // 保持していた 2 件（遅延印つき）+ 復旧を跨いだ新規 1 件 + 復旧サマリ 1 件 = 4 行。
        Assert.Equal(4, lines.Count);
        Assert.Equal(2, lines.Count(l => l.Detail.Contains("deferred-writeback")));
        Assert.Contains(lines, l => l.Detail.Contains("outage-1") && l.Detail.Contains("deferred-writeback"));
        Assert.Contains(lines, l => l.Detail.Contains("after-recovery") && !l.Detail.Contains("deferred-writeback"));

        var summary = Assert.Single(lines, l => l.Kind == "AuditChannelRecovered");
        Assert.Contains("書き戻し=2件", summary.Detail);
        Assert.Contains("縮退破棄=0件", summary.Detail);
        Assert.Contains("欠落し得た期間=", summary.Detail);
    }

    [Fact]
    public async Task Recovery_ViaTimer_WritesBackWithoutNewEvent()
    {
        var time = new FakeTimeProvider(Now);
        var sut = CreateSut(time);
        await sut.StartAsync(CancellationToken.None);

        BlockChannel();
        await sut.RecordAsync(Sample("outage"));
        Assert.Equal(1, sut.BufferedCount);

        // 新規事象なしでも周期スキャンが復旧を検知する。
        UnblockChannel();
        time.Advance(AuditResilienceDefaults.RecoveryScanInterval + TimeSpan.FromSeconds(1));

        Assert.Equal(0, sut.BufferedCount);
        var lines = ReadAuditLines();
        Assert.Contains(lines, l => l.Detail.Contains("outage") && l.Detail.Contains("deferred-writeback"));
        Assert.Contains(lines, l => l.Kind == "AuditChannelRecovered");

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task BufferCapacityExceeded_DropsNewest_CountsInSummaryAndMetric()
    {
        var time = new FakeTimeProvider(Now);
        var sut = CreateSut(time, maxBuffered: 3);
        using var dropped = new MetricCollector<long>(_metrics.AuditBufferDroppedCounter);

        BlockChannel();
        for (var i = 0; i < 8; i++)
        {
            await sut.RecordAsync(Sample($"outage-{i}"));
        }

        // 上限 3 件のみ保持、残り 5 件は縮退破棄——破棄件数はライブ計器に計上される
        // （復旧サマリが書けないままプロセスが落ちても観測に残るため。§4.2）。
        Assert.Equal(3, sut.BufferedCount);
        Assert.Equal(5, dropped.GetMeasurementSnapshot().Sum(m => m.Value));
    }

    [Fact]
    public async Task BufferCapacityExceeded_RetainsOldest_SummaryReportsDrops()
    {
        var time = new FakeTimeProvider(Now);
        var sut = CreateSut(time, maxBuffered: 3);

        BlockChannel();
        for (var i = 0; i < 8; i++)
        {
            await sut.RecordAsync(Sample($"outage-{i}"));
        }

        UnblockChannel();
        await sut.RecordAsync(Sample("recover"));

        var lines = ReadAuditLines();
        // 古い側を残す縮退——outage-0..2 が書き戻され、outage-3..7 は破棄される。
        Assert.Contains(lines, l => l.Detail.Contains("outage-0") && l.Detail.Contains("deferred-writeback"));
        Assert.DoesNotContain(lines, l => l.Detail.Contains("outage-7"));

        var summary = Assert.Single(lines, l => l.Kind == "AuditChannelRecovered");
        Assert.Contains("書き戻し=3件", summary.Detail);
        Assert.Contains("縮退破棄=5件", summary.Detail);
    }
}
