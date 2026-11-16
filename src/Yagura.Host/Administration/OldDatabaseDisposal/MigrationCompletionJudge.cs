using Yagura.Abstractions.Administration;
using Yagura.Storage;

namespace Yagura.Host.Administration.OldDatabaseDisposal;

/// <summary>
/// 処分の直前に行う移行の完了判定（database.md §6.3「移行の完了判定と明示確認」の規則 1〜4 と、
/// システムイベントの引き継ぎ）。呼び出し側が旧ファイルの排他を保持し、旧ファイルは静止していることを前提とする。
/// </summary>
/// <remarks>
/// <para>
/// 処分は製品がログを消す操作であり、その許可条件を移行の総数の比較に頼らない——静止した旧ファイルを
/// 時間帯（UTC の正時ごと）に数え直し、すべての時間帯で「移行先の件数 ≥ 旧ファイルの件数」であることを確かめる。
/// </para>
/// <para>
/// <b>保存期間を過ぎた時間帯は照合しない</b>: 移行先では保存期間による削除が進むため、移行済みでも保存期間を
/// 過ぎた時間帯は移行先の件数が旧ファイルを下回る。それらは製品の約束の上では既に消えているはずのログであり、
/// 「まだ移行していない」と呼んで明示確認を求めると、移行を済ませた利用者に誤った警告を出し続けることになる。
/// 正時の時間帯の終わりが保存期間の境界以前のものだけを除く。
/// </para>
/// <para>
/// 切替の境界の記録が無い・読めない場合は、照合できない区間を全区間とみなす（安全側——明示確認に回す）。
/// </para>
/// </remarks>
public static class MigrationCompletionJudge
{
    /// <summary>判定する。</summary>
    /// <param name="oldPath">旧ファイル（本体）のパス。</param>
    /// <param name="summary">旧ファイルの要約。</param>
    /// <param name="checkpoint">移行チェックポイントの状態（無ければ <see langword="null"/>）。</param>
    /// <param name="boundary">切替の境界の記録（無い・読めなければ <see langword="null"/>）。</param>
    /// <param name="target">移行先（現行 provider）。</param>
    /// <param name="retentionCutoff">保存期間の境界（これ以前に終わる時間帯は照合しない。無期限なら <see langword="null"/>）。</param>
    /// <param name="cancellationToken">取り消し。</param>
    public static async Task<OldDatabaseMigrationJudgment> JudgeAsync(
        string oldPath,
        OldDatabaseSummary summary,
        LogMigrationCheckpointState? checkpoint,
        PromotionSwitchBoundary? boundary,
        ILogStore target,
        DateTimeOffset? retentionCutoff,
        CancellationToken cancellationToken)
    {
        var gaps = new List<OldDatabaseMigrationGap>();
        (DateTimeOffset ReceivedAt, long Id)? position = null;
        var everyRecordUnmigrated = false;

        // 規則 1・2: 完了記録と移行済み位置。
        if (checkpoint is not { Completed: true })
        {
            gaps.Add(OldDatabaseMigrationGap.NotCompleted);
            position = checkpoint?.Position;
            everyRecordUnmigrated = position is null;
        }
        else if (checkpoint.Position is { } completedAt)
        {
            position = completedAt;
            var added = await OldSqliteDatabase.RangeAfterPositionAsync(
                oldPath, completedAt.ReceivedAt, completedAt.Id, cancellationToken).ConfigureAwait(false);
            if (added.Count > 0)
            {
                gaps.Add(OldDatabaseMigrationGap.RecordsAddedAfterCompletion);
            }
        }
        else if (summary.RecordCount > 0)
        {
            gaps.Add(OldDatabaseMigrationGap.CompletedWithoutPosition);
            everyRecordUnmigrated = true;
        }

        // 規則 3・4: 時間帯ごとの照合と、照合できない区間。
        DateTimeOffset? unreconcilableAfter = null;
        var afterSwitch = OldDatabaseRecordRange.Empty;
        var failedHours = new List<DateTimeOffset>();
        if (summary.RecordCount > 0)
        {
            if (boundary is null)
            {
                gaps.Add(OldDatabaseMigrationGap.SwitchBoundaryUnknown);
                everyRecordUnmigrated = true;
            }
            else
            {
                unreconcilableAfter = boundary.UnreconcilableAfter;
                afterSwitch = await OldSqliteDatabase.RangeAfterAsync(oldPath, unreconcilableAfter.Value, cancellationToken)
                    .ConfigureAwait(false);
                if (afterSwitch.Count > 0)
                {
                    gaps.Add(OldDatabaseMigrationGap.RecordsInUnreconcilableInterval);
                }

                if (target is IHourlyLogCountReader targetHistogram)
                {
                    var toExclusive = unreconcilableAfter.Value.AddTicks(1);
                    var oldHours = await OldSqliteDatabase.CountByHourAsync(oldPath, toExclusive, cancellationToken)
                        .ConfigureAwait(false);
                    var comparable = oldHours
                        .Where(hour => retentionCutoff is not { } cutoff || hour.HourStartUtc.AddHours(1) > cutoff)
                        .ToList();
                    if (comparable.Count > 0)
                    {
                        var targetHours = (await targetHistogram.CountByHourAsync(
                                comparable[0].HourStartUtc, toExclusive, cancellationToken).ConfigureAwait(false))
                            .ToDictionary(hour => hour.HourStartUtc.UtcTicks, hour => hour.Count);
                        failedHours.AddRange(comparable
                            .Where(hour => targetHours.GetValueOrDefault(hour.HourStartUtc.UtcTicks) < hour.Count)
                            .Select(hour => hour.HourStartUtc));
                        if (failedHours.Count > 0)
                        {
                            gaps.Add(OldDatabaseMigrationGap.HourlyReconciliationFailed);
                        }
                    }
                }
                else
                {
                    gaps.Add(OldDatabaseMigrationGap.TargetCannotReconcile);
                    everyRecordUnmigrated = true;
                }
            }
        }

        // システムイベントの引き継ぎ（移行がシステムイベントも写す）。
        var oldEvents = await OldSqliteDatabase.ReadSystemEventsAsync(oldPath, cancellationToken).ConfigureAwait(false);
        var missingEvents = await SystemEventReconciler.FindMissingInTargetAsync(oldEvents, target, cancellationToken)
            .ConfigureAwait(false);
        if (missingEvents.Count > 0)
        {
            gaps.Add(OldDatabaseMigrationGap.SystemEventsNotMigrated);
        }

        OldDatabaseRecordRange unmigrated;
        if (summary.RecordCount == 0)
        {
            unmigrated = OldDatabaseRecordRange.Empty;
        }
        else if (everyRecordUnmigrated)
        {
            unmigrated = new OldDatabaseRecordRange(summary.RecordCount, summary.OldestReceivedAt, summary.NewestReceivedAt);
        }
        else
        {
            unmigrated = await OldSqliteDatabase.RangeUnmigratedAsync(
                oldPath, failedHours, unreconcilableAfter, position, cancellationToken).ConfigureAwait(false);

            // 完了記録が無いのに照合ではすべて揃って見える場合も、完了を確かめられないものとして扱う。
            if (gaps.Contains(OldDatabaseMigrationGap.NotCompleted) && unmigrated.Count == 0)
            {
                unmigrated = new OldDatabaseRecordRange(summary.RecordCount, summary.OldestReceivedAt, summary.NewestReceivedAt);
            }
        }

        return new OldDatabaseMigrationJudgment(
            Migrated: gaps.Count == 0,
            Gaps: gaps,
            UnmigratedRecordCount: unmigrated.Count,
            UnmigratedOldest: unmigrated.Oldest,
            UnmigratedNewest: unmigrated.Newest,
            RecordsAfterSwitchCount: afterSwitch.Count,
            RecordsAfterSwitchOldest: afterSwitch.Oldest,
            RecordsAfterSwitchNewest: afterSwitch.Newest,
            UnmigratedSystemEventCount: missingEvents.Count,
            UnmigratedSystemEventOldest: missingEvents.Count > 0 ? missingEvents.Min(e => e.StartAt) : null,
            UnmigratedSystemEventNewest: missingEvents.Count > 0 ? missingEvents.Max(e => e.EndAt) : null);
    }
}
