using Yagura.Storage.Spool;

namespace Yagura.Storage.Tests.Spool;

/// <summary>
/// スプール領域を開けない場合、例外を投げず <c>null</c> を返すことの確認
/// （architecture.md §1.2「手順 1 が失敗した場合…受信は開始し、スプールなしの
/// 縮退運転として続行する」の前提となる、DiskSpool 側の契約）。
/// </summary>
public sealed class DiskSpoolOpenFailureTests
{
    [Fact]
    public void TryOpen_DirectoryPathPointsToExistingFile_ReturnsNullWithFailure()
    {
        // ディレクトリとして使うパスに、あらかじめ同名の「通常ファイル」を置いておくと、
        // Directory.CreateDirectory はそのパスをディレクトリとして扱えず失敗する
        // （ACL 破損等、ディスク側の異常でディレクトリを開けない状況の代替模擬）。
        var parent = Path.Combine(Path.GetTempPath(), $"yagura-spool-openfail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(parent);
        var blockedPath = Path.Combine(parent, "spool-should-be-a-directory");
        File.WriteAllBytes(blockedPath, [1, 2, 3]);

        try
        {
            var spool = DiskSpool.TryOpen(new DiskSpoolOptions { Directory = blockedPath }, out var failure);

            Assert.Null(spool);
            Assert.NotNull(failure);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }
}
