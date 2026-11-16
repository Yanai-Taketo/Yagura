using System.Text.Json;

namespace Yagura.Host.Administration.OldDatabaseDisposal;

/// <summary>
/// 切替の境界の記録（database.md §6.3・DB-14 ①）。データルート直下の
/// <see cref="FileName"/> に、監査記録とは別の状態として置く。
/// </summary>
/// <param name="SwitchedAtUtc">切替を実行した時刻（設定の保存が成立した時刻）。</param>
/// <param name="OldDatabasePath">その時点の組み込み DB ファイルのフルパス。</param>
/// <param name="LatestReceivedAtUtc">その時点の最新レコードの ReceivedAt（0 件・取得できなければ null）。</param>
/// <param name="LatestId">その時点の最新レコードの Id。</param>
/// <param name="RecordCount">その時点の件数（取得できなければ null）。</param>
/// <param name="EffectiveSinceUtc">
/// 実効 provider が SQL Server になった最初の起動の時刻。これより後の ReceivedAt は移行先のライブ受信と
/// 重なるため件数で照合できない。未記録なら <see cref="SwitchedAtUtc"/> を境界とする（照合できない区間が
/// 広がる安全側）。
/// </param>
public sealed record PromotionSwitchBoundary(
    DateTime SwitchedAtUtc,
    string OldDatabasePath,
    DateTime? LatestReceivedAtUtc,
    long? LatestId,
    long? RecordCount,
    DateTime? EffectiveSinceUtc = null)
{
    /// <summary>照合できない区間の始まり（この時刻より後の ReceivedAt は件数で照合できない）。</summary>
    public DateTimeOffset UnreconcilableAfter =>
        new(DateTime.SpecifyKind(EffectiveSinceUtc ?? SwitchedAtUtc, DateTimeKind.Utc));
}

/// <summary>切替の境界の記録の読み書き。</summary>
/// <remarks>
/// 判定の引き金はあくまで旧ファイルの存在という事実であり、本記録は「どこからが照合できない区間か」と
/// 「設定の名前を変えて見失った旧ファイル」を知るための補助に限る。記録が無い・壊れている場合、
/// 読み取り側は境界が分からないものとして扱う（照合できない区間を全区間とみなして明示確認に回す）。
/// </remarks>
public static class PromotionSwitchBoundaryStore
{
    /// <summary>記録のファイル名（データルート直下）。</summary>
    public const string FileName = "promotion-switch-boundary.json";

    private static string PathOf(string dataRoot) => System.IO.Path.Combine(dataRoot, FileName);

    /// <summary>記録を読む。無い・壊れている場合は <see langword="null"/>。</summary>
    public static PromotionSwitchBoundary? TryRead(string dataRoot)
    {
        try
        {
            var path = PathOf(dataRoot);
            if (!File.Exists(path))
            {
                return null;
            }

            var boundary = JsonSerializer.Deserialize<PromotionSwitchBoundary>(File.ReadAllText(path));
            return boundary is { OldDatabasePath: { Length: > 0 } } ? boundary : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// 切替の実行時に記録する（既存の記録は上書きする——最後の切替が境界になる）。
    /// 原子的置換で書く。
    /// </summary>
    public static void Write(string dataRoot, PromotionSwitchBoundary boundary)
    {
        var path = PathOf(dataRoot);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(boundary));
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// 実効 provider が SQL Server で起動したときに呼ぶ。記録があり、反映の時刻が未記録なら
    /// <paramref name="nowUtc"/> を書き足す。記録を書き足したら <see langword="true"/>。
    /// </summary>
    public static bool MarkEffectiveIfAbsent(string dataRoot, DateTime nowUtc)
    {
        var boundary = TryRead(dataRoot);
        if (boundary is null || boundary.EffectiveSinceUtc is not null)
        {
            return false;
        }

        Write(dataRoot, boundary with { EffectiveSinceUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc) });
        return true;
    }
}
