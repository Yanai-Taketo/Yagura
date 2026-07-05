using Microsoft.Extensions.Logging.Testing;
using Yagura.Host.Observability;
using Yagura.Ingestion.Diagnostics;

namespace Yagura.Host.Tests.Observability;

/// <summary>
/// <see cref="MetadataStore"/> の単体テスト（M4-4。architecture.md §4.3）。
/// </summary>
/// <remarks>
/// <see cref="Configuration.YaguraConfigurationWriterTests"/> と同じ観点で構成する:
/// 保存 → 読み込みの往復、原子性の代替検証（一時ファイルの残骸がないこと）、
/// 破損時のフォールバック（ゼロから再開 + 警告）。
/// </remarks>
public sealed class MetadataStoreTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-metadata-test-{Guid.NewGuid():N}");

    public MetadataStoreTests()
    {
        Directory.CreateDirectory(_dataRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    private string FilePath => MetadataStore.GetFilePath(_dataRoot);

    // ------------------------------------------------------------------
    // 保存 → 読み込みの往復
    // ------------------------------------------------------------------

    [Fact]
    public void Read_FileAbsent_ReturnsInitialState()
    {
        var state = MetadataStore.Read(_dataRoot);

        Assert.Equal(MetadataState.Initial, state);
    }

    [Fact]
    public void SaveThenRead_RoundTripsCountersStopEventAndLiveness()
    {
        var baseline = DateTimeOffset.UtcNow;
        var counters = new IngestionCounterSnapshot(
            InternalBufferDropped: 3,
            TcpConnectionRejected: 1,
            SpoolEvacuated: 42,
            SpoolWriteFailed: 2,
            SpoolDiscarded: 5,
            PersistenceFailed: 7,
            FlowControlDropped: 0);
        var stopEvent = new StopEventRecord(
            ReceiveSocketClosedAt: baseline.AddSeconds(-2),
            StoppedAt: baseline.AddSeconds(-1));
        var state = new MetadataState(counters, stopEvent, LastLivenessAt: baseline);

        MetadataStore.Save(_dataRoot, state);
        var reloaded = MetadataStore.Read(_dataRoot);

        Assert.Equal(state, reloaded);
    }

    [Fact]
    public void SaveThenRead_NullStopEventAndLiveness_RoundTrips()
    {
        var state = new MetadataState(
            new IngestionCounterSnapshot(1, 0, 0, 0, 0, 0, 0),
            LastStopEvent: null,
            LastLivenessAt: null);

        MetadataStore.Save(_dataRoot, state);
        var reloaded = MetadataStore.Read(_dataRoot);

        Assert.Equal(state, reloaded);
    }

    [Fact]
    public void SaveTwice_SecondSaveOverwritesFirst()
    {
        var first = new MetadataState(new IngestionCounterSnapshot(1, 0, 0, 0, 0, 0, 0), null, null);
        var second = new MetadataState(new IngestionCounterSnapshot(99, 0, 0, 0, 0, 0, 0), null, null);

        MetadataStore.Save(_dataRoot, first);
        MetadataStore.Save(_dataRoot, second);

        var reloaded = MetadataStore.Read(_dataRoot);
        Assert.Equal(99, reloaded.Counters.InternalBufferDropped);
    }

    // ------------------------------------------------------------------
    // 原子性の代替検証（一時ファイルの残骸がないこと）
    // ------------------------------------------------------------------

    [Fact]
    public void Save_Success_LeavesNoTemporaryFilesInDataRoot()
    {
        var state = new MetadataState(IngestionCounterSnapshot.Zero, null, null);

        MetadataStore.Save(_dataRoot, state);

        var entries = Directory.GetFileSystemEntries(_dataRoot);
        var entry = Assert.Single(entries);
        Assert.Equal(FilePath, entry);
    }

    [Fact]
    public void Save_WritesUtf8WithoutByteOrderMark()
    {
        MetadataStore.Save(_dataRoot, new MetadataState(IngestionCounterSnapshot.Zero, null, null));

        var bytes = File.ReadAllBytes(FilePath);
        var utf8Bom = new byte[] { 0xEF, 0xBB, 0xBF };
        Assert.False(bytes.Length >= 3 && bytes.AsSpan(0, 3).SequenceEqual(utf8Bom));
    }

    // ------------------------------------------------------------------
    // 破損時のフォールバック（ゼロから再開 + 警告）
    // ------------------------------------------------------------------

    [Fact]
    public void Read_FileContainsInvalidJson_ReturnsInitialStateAndLogsWarning()
    {
        File.WriteAllText(FilePath, "{ this is not valid json");

        var logger = new FakeLogger();
        var state = MetadataStore.Read(_dataRoot, logger);

        Assert.Equal(MetadataState.Initial, state);
        Assert.Contains(logger.Collector.GetSnapshot(), record => record.Message.Contains("[metadata-store-corrupt]"));
    }

    [Fact]
    public void Read_FileIsEmpty_ReturnsInitialStateAndLogsWarning()
    {
        File.WriteAllText(FilePath, string.Empty);

        var logger = new FakeLogger();
        var state = MetadataStore.Read(_dataRoot, logger);

        Assert.Equal(MetadataState.Initial, state);
        Assert.Contains(logger.Collector.GetSnapshot(), record => record.Message.Contains("[metadata-store-corrupt]"));
    }

    [Fact]
    public void Read_FileContainsUnrelatedJson_ReturnsZeroCountersWithoutThrowing()
    {
        // 型は一致する（オブジェクト）が、期待するプロパティを持たない JSON。
        // 例外にはならず、既定値（0）で埋められることを確認する。
        File.WriteAllText(FilePath, """{ "unexpected": "shape" }""");

        var state = MetadataStore.Read(_dataRoot);

        Assert.Equal(IngestionCounterSnapshot.Zero, state.Counters);
        Assert.Null(state.LastStopEvent);
        Assert.Null(state.LastLivenessAt);
    }
}
