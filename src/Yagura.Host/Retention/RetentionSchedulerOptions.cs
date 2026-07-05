namespace Yagura.Host.Retention;

/// <summary>
/// 保持期間削除の定期実行スケジューラ（<see cref="RetentionScheduler"/>）の構成
/// （database.md §3。configuration.md §8「保持期間」区分）。
/// </summary>
/// <param name="RetentionDays">
/// 保持期間（日数）。<c>null</c> は「削除しない」。既定値（database.md DB-1）は
/// 2026-07-05 オーナー決定により 30 日——設定ファイル未設定時は 30 が適用される。
/// <c>null</c> になるのは、設定ファイルで <c>Retention:Days</c> に不正値（0 以下・
/// 数値以外）が指定された場合のフォールバック先としてのみである（意図しない自動削除の
/// 開始を避ける安全側の判断。詳細は
/// <see cref="Yagura.Host.Configuration.YaguraConfigurationLoader"/> のコメント参照）。
/// </param>
/// <param name="ExecutionTimeOfDay">定期実行の開始時刻（サーバのローカル時刻）。</param>
public sealed record RetentionSchedulerOptions(int? RetentionDays, TimeOnly ExecutionTimeOfDay)
{
    /// <summary>
    /// 定期実行の開始時刻の既定値（暫定値。実測確定待ち）。深夜帯を既定とし、日中の受信・
    /// 閲覧への影響を避ける。
    /// </summary>
    public static readonly TimeOnly DefaultExecutionTimeOfDay = new(hour: 3, minute: 0);
}
