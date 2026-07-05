using Yagura.Storage.Spool;

namespace Yagura.Storage.Tests.Spool;

/// <summary>
/// スプール書き込み失敗時のリトライ→破棄（architecture.md §3.1「スプール書き込み: —
/// （機構の失敗） | リトライ後に破棄」）。
/// </summary>
public sealed class DiskSpoolWriteFailureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"yagura-diskspool-writefail-tests-{Guid.NewGuid():N}");
    private DiskSpool? _spool;

    public void Dispose()
    {
        _spool?.Dispose();

        if (Directory.Exists(_directory))
        {
            // 読み取り専用属性を外してから削除する（テストで付与した場合の後始末）。
            foreach (var file in Directory.EnumerateFiles(_directory))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task TryAppendAsync_DirectoryRemovedAfterOpen_RetriesThenReturnsWriteFailed()
    {
        _spool = DiskSpool.TryOpen(new DiskSpoolOptions { Directory = _directory }, out _);
        Assert.NotNull(_spool);

        // 開いた直後にディレクトリそのものを削除する——以降の追記は「機構の失敗」として
        // IOException になる（ディスク障害・ACL 破損等を模す）。
        Directory.Delete(_directory, recursive: true);

        var record = new LogRecord(
            ReceivedAt: DateTimeOffset.UtcNow,
            SourceAddress: "10.0.0.1",
            SourcePort: 514,
            Protocol: Protocol.Udp,
            ParseStatus: ParseStatus.Parsed,
            Message: "should-fail-to-write");

        var result = await _spool.TryAppendAsync(SpoolRecord.ForLog(record));

        Assert.Equal(SpoolAppendResult.WriteFailed, result);
        Assert.Equal(0, _spool.CurrentUsageBytes);
    }
}
