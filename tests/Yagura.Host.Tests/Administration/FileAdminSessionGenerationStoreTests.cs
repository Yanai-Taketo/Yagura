using Yagura.Host.Administration;

namespace Yagura.Host.Tests.Administration;

/// <summary>
/// <see cref="FileAdminSessionGenerationStore"/>（ADR-0013 決定 2 の世代番号方式による緊急全失効）の固定。
/// </summary>
public sealed class FileAdminSessionGenerationStoreTests : IDisposable
{
    private readonly string _dataRoot;

    public FileAdminSessionGenerationStoreTests()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "yagura-gen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
    }

    [Fact]
    public void FreshDataRoot_StartsAtGenerationZero()
    {
        var store = new FileAdminSessionGenerationStore(_dataRoot);
        Assert.Equal(0, store.CurrentGeneration);
    }

    [Fact]
    public void Bump_IncrementsAndPersists()
    {
        var store = new FileAdminSessionGenerationStore(_dataRoot);

        Assert.Equal(1, store.Bump());
        Assert.Equal(1, store.CurrentGeneration);
        Assert.Equal(2, store.Bump());
        Assert.Equal(2, store.CurrentGeneration);
    }

    [Fact]
    public void NewInstance_RestoresPersistedGeneration_SurvivesRestart()
    {
        // 定常再起動をまたいで世代番号が生存する（既発行セッションは無効化されない。ADR-0013 決定 2・6）。
        var store1 = new FileAdminSessionGenerationStore(_dataRoot);
        store1.Bump();
        store1.Bump();

        var store2 = new FileAdminSessionGenerationStore(_dataRoot);
        Assert.Equal(2, store2.CurrentGeneration);
    }
}
