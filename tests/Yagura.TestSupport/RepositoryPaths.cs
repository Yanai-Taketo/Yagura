namespace Yagura.TestSupport;

/// <summary>
/// テストから見たリポジトリルート解決。テストプロジェクトごとに private 実装を持たせると
/// 探索ロジックの改修(探索起点・打ち切り条件等)が実装数だけ発生するため 1 箇所に集約する。
/// </summary>
public static class RepositoryPaths
{
    /// <summary>
    /// <see cref="AppContext.BaseDirectory"/> から親方向へ<c>Yagura.sln</c>を探す。
    /// テスト出力先(bin/Debug/net10.0 等)の深さはビルド構成やターゲットフレームワークで
    /// 変わり得るため、固定段数の相対パスではなくファイル存在で探索する。
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <c>Yagura.sln</c>を含むディレクトリが見つからない場合。
    /// </exception>
    public static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagura.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Yagura.sln を含むリポジトリルートが見つからない。");
    }
}
