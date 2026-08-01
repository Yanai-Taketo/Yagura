namespace Yagura.Storage;

/// <summary>
/// スキーマバージョン管理の判定ロジック（database.md §1.2 契約 1「読む→判定→適用」）を
/// provider 間で共通化する。SQL 文・接続・トランザクション境界は呼び出し側が保持し続け、
/// 本クラスは読む→判定→どちらを呼ぶかの分岐のみを負う。
/// </summary>
/// <remarks>
/// <see cref="ILogStore"/> 実装（<c>SqliteLogStore</c>・<c>SqlServerLogStore</c>）と
/// <see cref="Administration.IAdminAccountStore"/> 実装（<c>SqliteAdminAccountStore</c>・
/// <c>SqlServerAdminAccountStore</c>）は「版の記録」タイミングが異なる——前者は現行版と
/// 一致すれば記録自体を省略し、後者はバージョン表が無いことが「新規」と「バージョン管理
/// 導入前の無版数データ」のどちらも意味し得るため、一致していても常に記録し直す。
/// この差は意図的な実装差であり単一の分岐へ無理に統合しない: <see cref="RunAsync"/> が
/// 前者の形、<see cref="ResolveMigrationStartVersion"/> が後者の形をそれぞれ担う。
/// </remarks>
internal static class SchemaMigrationRunner
{
    /// <summary>
    /// 記録済みバージョンを読み、未記録（<see langword="null"/>）なら
    /// <paramref name="recordFreshVersion"/> を、現行未満なら <paramref name="applyMigrationsFrom"/>
    /// を呼ぶ。現行と一致する場合は何もしない（database.md §1.2「既に適用済みなら何もしない」）。
    /// </summary>
    public static async Task RunAsync(
        Func<Task<int?>> readRecordedVersion,
        int currentVersion,
        Func<Task> recordFreshVersion,
        Func<int, Task> applyMigrationsFrom)
    {
        var recordedVersion = await readRecordedVersion().ConfigureAwait(false);

        if (recordedVersion is null)
        {
            await recordFreshVersion().ConfigureAwait(false);
        }
        else if (recordedVersion.Value < currentVersion)
        {
            await applyMigrationsFrom(recordedVersion.Value).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 記録済みバージョンが <see langword="null"/> のとき、移行の起点を決める。バージョン表は
    /// 無いが対象テーブル自体は既に存在する場合は「バージョン管理導入前」を意味し v1 起点、
    /// テーブルも新規なら「DDL が最新形状のまま作った」を意味し現行版起点（=以降の移行ステップは
    /// 実質何もしない）とする——バージョン表の不在だけでは両者を区別できないための判定。
    /// </summary>
    public static int ResolveMigrationStartVersion(int? recordedVersion, bool tableExistedBeforeVersioning, int currentVersion) =>
        recordedVersion ?? (tableExistedBeforeVersioning ? 1 : currentVersion);
}
