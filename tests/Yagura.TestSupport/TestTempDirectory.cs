namespace Yagura.TestSupport;

/// <summary>
/// テスト専用の使い捨てディレクトリ。<see cref="Dispose"/>で再帰削除する。
/// </summary>
/// <remarks>
/// ディレクトリ自体は作らない(呼び出し側・テスト対象が必要な時点で作る。DiskSpoolTests 等
/// 既存テストの流儀と同じ)。存在しなければ削除もしない。他プロセス/ハンドルがロック中で
/// 削除に失敗した場合も無視する——後始末の失敗でテスト自体を失敗させない(実体はテスト実行後に
/// 残っても実害が小さい一時ディレクトリのため)。
/// </remarks>
public sealed class TestTempDirectory : IDisposable
{
    public TestTempDirectory(string prefix)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"yagura-{prefix}-{Guid.NewGuid():N}");
    }

    public string Path { get; }

    public void Dispose()
    {
        if (!Directory.Exists(Path))
        {
            return;
        }

        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
