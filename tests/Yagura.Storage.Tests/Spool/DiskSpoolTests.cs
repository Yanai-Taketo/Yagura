using Yagura.Storage.Spool;

namespace Yagura.Storage.Tests.Spool;

/// <summary>
/// <see cref="DiskSpool"/> の結合的な振る舞い（architecture.md §3.2）。
/// ファイル形式の往復・破損末尾の検出とスキップ回収・空ファイル/ゼロバイトファイルの扱い・
/// 上限到達時の破棄・削除保証を、実際のファイル I/O を通して確認する。
/// </summary>
public sealed class DiskSpoolTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"yagura-diskspool-tests-{Guid.NewGuid():N}");
    private readonly List<DiskSpool> _openedSpools = [];

    public void Dispose()
    {
        // アクティブセグメントの FileStream を閉じてからでないと、Windows ではディレクトリ
        // 削除が「別プロセスが使用中」で失敗する。
        foreach (var spool in _openedSpools)
        {
            spool.Dispose();
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>
    /// <see cref="DiskSpool.TryOpen"/> を呼び、テスト終了時に確実に <see cref="DiskSpool.Dispose"/>
    /// されるよう追跡する。
    /// </summary>
    private DiskSpool OpenSpool(DiskSpoolOptions options)
    {
        var spool = DiskSpool.TryOpen(options, out _);
        Assert.NotNull(spool);
        _openedSpools.Add(spool);
        return spool;
    }

    [Fact]
    public void TryOpen_NewDirectory_Succeeds()
    {
        var spool = OpenSpool(new DiskSpoolOptions { Directory = _directory });

        Assert.NotNull(spool);
        Assert.True(Directory.Exists(_directory));
    }

    [Fact]
    public async Task TryAppendAsync_ThenReadSegmentRecords_RoundTripsAllColumns()
    {
        var spool = OpenSpool(new DiskSpoolOptions { Directory = _directory });

        var baseline = DateTimeOffset.UtcNow;
        var record = new LogRecord(
            ReceivedAt: baseline,
            SourceAddress: "10.1.2.3",
            SourcePort: 5140,
            Protocol: Protocol.Udp,
            ParseStatus: ParseStatus.Parsed,
            DeviceTimestamp: baseline.AddSeconds(-1),
            Facility: 16,
            Severity: 6,
            Hostname: "myhost",
            AppName: "myapp",
            ProcId: "42",
            MsgId: "MSG1",
            StructuredData: "[a@1 b=\"c\"]",
            Message: "test message");

        var result = await spool.TryAppendAsync(SpoolRecord.ForLog(record));
        Assert.Equal(SpoolAppendResult.Appended, result);

        var segments = spool.TrySealActiveSegmentAndListDrainable();
        Assert.Single(segments);

        var records = spool.ReadSegmentRecords(segments[0], out var corruptTailDetected);
        Assert.False(corruptTailDetected);
        Assert.Single(records);

        var restored = records[0].LogRecord!;
        Assert.Equal(record.SourceAddress, restored.SourceAddress);
        Assert.Equal(record.SourcePort, restored.SourcePort);
        Assert.Equal(record.Message, restored.Message);
        Assert.Equal(record.Hostname, restored.Hostname);
        Assert.Equal(record.StructuredData, restored.StructuredData);
    }

    [Fact]
    public async Task ReadSegmentRecords_TruncatedTailAfterNRecords_RecoversAllNAndDetectsCorruption()
    {
        var spool = OpenSpool(new DiskSpoolOptions { Directory = _directory });

        const int recordCount = 5;
        var baseline = DateTimeOffset.UtcNow;

        for (var i = 0; i < recordCount; i++)
        {
            var record = new LogRecord(
                ReceivedAt: baseline.AddMilliseconds(i),
                SourceAddress: "10.0.0.1",
                SourcePort: 514,
                Protocol: Protocol.Udp,
                ParseStatus: ParseStatus.Parsed,
                Message: $"message-{i}");

            var result = await spool.TryAppendAsync(SpoolRecord.ForLog(record));
            Assert.Equal(SpoolAppendResult.Appended, result);
        }

        var segments = spool.TrySealActiveSegmentAndListDrainable();
        Assert.Single(segments);
        var segmentPath = segments[0];

        // 正常な N 件の直後に、中途半端な末尾バイト（不完全なフレーム）を追記する
        // ——クラッシュ・強制終了で末尾が中途半端に切れた状況を模擬する。
        using (var stream = new FileStream(segmentPath, FileMode.Append, FileAccess.Write))
        {
            // 長さプレフィックスだけを書き、payload を書かずに終える（torn write の模擬）。
            var partialLengthPrefix = new byte[] { 0x10, 0x00, 0x00, 0x00 }; // payloadLength = 16 のつもりだが本体が続かない
            stream.Write(partialLengthPrefix);
        }

        var records = spool.ReadSegmentRecords(segmentPath, out var corruptTailDetected);

        Assert.True(corruptTailDetected);
        Assert.Equal(recordCount, records.Count);
        for (var i = 0; i < recordCount; i++)
        {
            Assert.Equal($"message-{i}", records[i].LogRecord!.Message);
        }
    }

    [Fact]
    public async Task ReadSegmentRecords_HugeLengthPrefixInCorruptTail_RecoversPriorRecordsWithoutHugeAllocation()
    {
        var spool = OpenSpool(new DiskSpoolOptions { Directory = _directory });

        var baseline = DateTimeOffset.UtcNow;
        var record = new LogRecord(
            ReceivedAt: baseline,
            SourceAddress: "10.0.0.1",
            SourcePort: 514,
            Protocol: Protocol.Udp,
            ParseStatus: ParseStatus.Parsed,
            Message: "good");
        Assert.Equal(SpoolAppendResult.Appended, await spool.TryAppendAsync(SpoolRecord.ForLog(record)));

        var segments = spool.TrySealActiveSegmentAndListDrainable();
        var segmentPath = Assert.Single(segments);

        // torn write の途中バイトは任意の値を取り得る——長さプレフィックスが巨大値
        // (int.MaxValue)として読める破損末尾でも、検証が割り当てより先に行われ、
        // 巨大確保・負値配列例外を起こさずに正常レコードを回収できることを固定化する。
        using (var stream = new FileStream(segmentPath, FileMode.Append, FileAccess.Write))
        {
            stream.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F }); // payloadLength = int.MaxValue
            stream.Write(new byte[] { 0x01, 0x02, 0x03 });        // 続く本体は 3 バイトしかない
        }

        var records = spool.ReadSegmentRecords(segmentPath, out var corruptTailDetected);

        Assert.True(corruptTailDetected);
        var recovered = Assert.Single(records);
        Assert.Equal("good", recovered.LogRecord!.Message);
    }

    [Fact]
    public async Task ReadSegmentRecords_CorruptedCrcOnLastRecord_RecoversPriorRecordsAndDetectsCorruption()
    {
        var spool = OpenSpool(new DiskSpoolOptions { Directory = _directory });

        var baseline = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++)
        {
            var record = new LogRecord(
                ReceivedAt: baseline.AddMilliseconds(i),
                SourceAddress: "10.0.0.1",
                SourcePort: 514,
                Protocol: Protocol.Udp,
                ParseStatus: ParseStatus.Parsed,
                Message: $"good-{i}");

            await spool.TryAppendAsync(SpoolRecord.ForLog(record));
        }

        var segments = spool.TrySealActiveSegmentAndListDrainable();
        var segmentPath = segments[0];

        // 最後の 1 バイトを破壊する（CRC の一部を破壊し、torn write によるビット化けを模擬）。
        var bytes = File.ReadAllBytes(segmentPath);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(segmentPath, bytes);

        var records = spool.ReadSegmentRecords(segmentPath, out var corruptTailDetected);

        Assert.True(corruptTailDetected);
        // 3 件目の CRC が壊れているため、1〜2 件目は回収できるが 3 件目は読めない。
        Assert.Equal(2, records.Count);
        Assert.Equal("good-0", records[0].LogRecord!.Message);
        Assert.Equal("good-1", records[1].LogRecord!.Message);
    }

    [Fact]
    public void ReadSegmentRecords_EmptyFile_ReturnsNoRecordsAndNoCorruption()
    {
        Directory.CreateDirectory(_directory);
        var emptyFilePath = Path.Combine(_directory, "20260705120000000000000-0000-00000000.seg");
        File.WriteAllBytes(emptyFilePath, []);

        var spool = OpenSpool(new DiskSpoolOptions { Directory = _directory });

        var records = spool.ReadSegmentRecords(emptyFilePath, out var corruptTailDetected);

        Assert.Empty(records);
        Assert.False(corruptTailDetected);
    }

    [Fact]
    public async Task TryAppendAsync_QuotaExceeded_ReturnsQuotaExceededAndDoesNotGrowUsage()
    {
        // 上限をごく小さくし、1 件目で使い切ったうえで 2 件目が破棄されることを確認する。
        var record = new LogRecord(
            ReceivedAt: DateTimeOffset.UtcNow,
            SourceAddress: "10.0.0.1",
            SourcePort: 514,
            Protocol: Protocol.Udp,
            ParseStatus: ParseStatus.Parsed,
            Message: "quota-test-message-with-some-length-to-consume-bytes");

        // 1 件分のフレームサイズを実測してから、それより小さい上限を設定する。
        var probeFrame = SpoolRecordSerializerProbe.SerializeFrameLength(record);

        var spool = OpenSpool(new DiskSpoolOptions { Directory = _directory, QuotaBytes = probeFrame - 1 });

        var result = await spool.TryAppendAsync(SpoolRecord.ForLog(record));

        Assert.Equal(SpoolAppendResult.QuotaExceeded, result);
        Assert.Equal(0, spool.CurrentUsageBytes);
    }

    [Fact]
    public async Task TryAppendAsync_SecondRecordExceedsQuota_FirstSucceedsSecondDiscarded()
    {
        var record1 = new LogRecord(
            ReceivedAt: DateTimeOffset.UtcNow,
            SourceAddress: "10.0.0.1",
            SourcePort: 514,
            Protocol: Protocol.Udp,
            ParseStatus: ParseStatus.Parsed,
            Message: "first-message");

        var frameLength = SpoolRecordSerializerProbe.SerializeFrameLength(record1);

        // 上限は 1 件分ちょうど（2 件目は必ず溢れる）。
        var spool = OpenSpool(new DiskSpoolOptions { Directory = _directory, QuotaBytes = frameLength });

        var firstResult = await spool.TryAppendAsync(SpoolRecord.ForLog(record1));
        Assert.Equal(SpoolAppendResult.Appended, firstResult);

        var record2 = record1 with { Message = "second-message-overflow" };
        var secondResult = await spool.TryAppendAsync(SpoolRecord.ForLog(record2));

        Assert.Equal(SpoolAppendResult.QuotaExceeded, secondResult);
        Assert.Equal(frameLength, spool.CurrentUsageBytes);
    }

    [Fact]
    public async Task DeleteSegment_RemovesFileAndReducesUsage()
    {
        var spool = OpenSpool(new DiskSpoolOptions { Directory = _directory });

        var record = new LogRecord(
            ReceivedAt: DateTimeOffset.UtcNow,
            SourceAddress: "10.0.0.1",
            SourcePort: 514,
            Protocol: Protocol.Udp,
            ParseStatus: ParseStatus.Parsed,
            Message: "to-be-deleted");

        await spool.TryAppendAsync(SpoolRecord.ForLog(record));
        var segments = spool.TrySealActiveSegmentAndListDrainable();
        Assert.Single(segments);
        Assert.True(spool.CurrentUsageBytes > 0);

        spool.DeleteSegment(segments[0]);

        Assert.False(File.Exists(segments[0]));
        Assert.Equal(0, spool.CurrentUsageBytes);
    }

    [Fact]
    public async Task TryOpen_ExistingSegmentsFromPreviousRun_AreDiscoveredAndUsageReflected()
    {
        // 1 回目のプロセスを模擬: 書き込んでセグメントを残す。
        var firstOpen = OpenSpool(new DiskSpoolOptions { Directory = _directory });
        var record = new LogRecord(
            ReceivedAt: DateTimeOffset.UtcNow,
            SourceAddress: "10.0.0.1",
            SourcePort: 514,
            Protocol: Protocol.Udp,
            ParseStatus: ParseStatus.Parsed,
            Message: "left-over-from-previous-run");

        await firstOpen.TryAppendAsync(SpoolRecord.ForLog(record));
        firstOpen.TrySealActiveSegmentAndListDrainable();

        // 2 回目の起動（前回退避分の存在確認。architecture.md §1.2 起動手順 1）。
        var secondOpen = OpenSpool(new DiskSpoolOptions { Directory = _directory });

        Assert.True(secondOpen.CurrentUsageBytes > 0);
        var segments = secondOpen.TrySealActiveSegmentAndListDrainable();
        Assert.Single(segments);

        var records = secondOpen.ReadSegmentRecords(segments[0], out var corruptTailDetected);
        Assert.False(corruptTailDetected);
        Assert.Single(records);
        Assert.Equal("left-over-from-previous-run", records[0].LogRecord!.Message);
    }
}

/// <summary>
/// テストがフレーム長を実測するための小さなヘルパー（<see cref="SpoolRecordSerializer"/> は
/// internal だが InternalsVisibleTo 経由で直接参照できる）。
/// </summary>
internal static class SpoolRecordSerializerProbe
{
    public static int SerializeFrameLength(LogRecord record) =>
        SpoolRecordSerializer.SerializeFrame(SpoolRecord.ForLog(record)).Length;
}
