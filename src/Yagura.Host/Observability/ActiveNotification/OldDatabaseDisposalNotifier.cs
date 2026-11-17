using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Administration.OldDatabaseDisposal;

namespace Yagura.Host.Observability.ActiveNotification;

/// <summary>
/// 旧・組み込み DB ファイルの処分（後始末）の未完了・失敗の能動通知（EventId 1041）と、製品の処分によらず
/// 旧ファイルが無くなったことの監査記録（2031）（database.md §6.3「未完了の判定と可視化・通知」）。
/// </summary>
/// <remarks>
/// <para>
/// <b>周期評価</b>（<see cref="EvaluateAsync"/>。<see cref="ActiveNotificationMonitor"/> の周期から呼ぶ）:
/// 未完了が継続判定期間（<see cref="ActiveNotificationConstants.OldDatabaseDisposalIncompleteContinuationThreshold"/>）
/// 連続して観測されたら発火し、以後は専用の抑制窓で繰り返す。継続はプロセス内の観測で判定し、サービスの
/// 再起動で起算し直す。移行・処分の実行中は発火しない。
/// </para>
/// <para>
/// <b>即時発火</b>（<see cref="NotifyFinalized"/>。処分の背景処理から呼ぶ）: 処分が退避の失敗・削除の部分失敗・
/// 中断で終わったら、継続判定期間・抑制窓によらず 1 回発火する（本文に結果と理由）。以後は処分待ちと同じ。
/// </para>
/// <para>
/// <b>製品の外での消失（2031）はファイルごとに判定する</b>: 未完了の間に観測した旧ファイル（今の設定名のファイルと、
/// 切替の時点の別名のファイル）を覚えておき、解消を観測したとき、このプロセスで後始末の完了を記録していない
/// ファイルがあれば、そのパスを記録する。今の設定名のファイルを製品で処分した後に、別名のファイルが製品の外で
/// 消えた場合も辿れるようにするため。<c>-wal</c>・<c>-shm</c> は本体と同じファイルとして扱う（製品が旧ファイルを
/// 開いて閉じると SQLite が消すことがあり、別に数えると製品の操作を製品の外での消失と誤る）。
/// </para>
/// <para>
/// 旧ファイルはデータベースとしては開かない。周期評価と即時発火は別スレッドから来るため、状態はロックで守る。
/// </para>
/// </remarks>
public sealed class OldDatabaseDisposalNotifier
{
    private readonly Func<OldDatabaseDisposalObservation> _observe;
    private readonly IAuditRecorder _auditRecorder;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OldDatabaseDisposalNotifier> _logger;
    private readonly object _gate = new();

    private DateTimeOffset? _incompleteSince;
    private DateTimeOffset? _lastNotifiedAt;
    private bool _wasIncomplete;
    private readonly HashSet<string> _observedFiles = new(StringComparer.OrdinalIgnoreCase);

    public OldDatabaseDisposalNotifier(
        Func<OldDatabaseDisposalObservation> observe,
        IAuditRecorder auditRecorder,
        TimeProvider? timeProvider = null,
        ILogger<OldDatabaseDisposalNotifier>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(observe);
        ArgumentNullException.ThrowIfNull(auditRecorder);

        _observe = observe;
        _auditRecorder = auditRecorder;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<OldDatabaseDisposalNotifier>.Instance;
    }

    /// <summary>周期評価。</summary>
    public async Task EvaluateAsync(CancellationToken cancellationToken = default)
    {
        var observation = _observe();
        var now = _timeProvider.GetUtcNow();
        var status = observation.Status;
        var incomplete = observation.EffectiveProviderIsSqlServer
            && (status.State is not OldDatabaseDisposalState.NotApplicable
                || status.MisplacedRecordedOldDatabasePath is not null);
        var running = status.State is OldDatabaseDisposalState.MigrationRunning
            or OldDatabaseDisposalState.DisposalRunning
            or OldDatabaseDisposalState.DisposalDeleting;

        List<string> removedOutside = [];
        lock (_gate)
        {
            if (!incomplete)
            {
                if (_wasIncomplete && observation.EffectiveProviderIsSqlServer)
                {
                    var disposed = new HashSet<string>(observation.DisposedPathsInThisProcess, StringComparer.OrdinalIgnoreCase);
                    removedOutside = _observedFiles.Where(path => !disposed.Contains(path)).Order(StringComparer.OrdinalIgnoreCase).ToList();
                }

                _wasIncomplete = false;
                _incompleteSince = null;
                _lastNotifiedAt = null;
                _observedFiles.Clear();
            }
            else
            {
                _wasIncomplete = true;
                _observedFiles.UnionWith(DatabaseFilesOf(status));
                _incompleteSince ??= now;
                var continued = now - _incompleteSince.Value;
                if (!running
                    && continued >= ActiveNotificationConstants.OldDatabaseDisposalIncompleteContinuationThreshold
                    && (_lastNotifiedAt is not { } last
                        || now - last >= ActiveNotificationConstants.OldDatabaseDisposalIncompleteSuppressionWindow))
                {
                    _lastNotifiedAt = now;
                    LogIncomplete(observation, continued);
                }
            }
        }

        if (removedOutside.Count > 0)
        {
            await RecordRemovedOutsideProductAsync(removedOutside).ConfigureAwait(false);
        }
    }

    /// <summary>処分が失敗・部分失敗・中断で終わったときの即時発火。</summary>
    public void NotifyFinalized(OldDatabaseDisposalResult result, OldDatabaseDisposalObservation observation)
    {
        lock (_gate)
        {
            _lastNotifiedAt = _timeProvider.GetUtcNow();
            if (observation.Status.State is not OldDatabaseDisposalState.NotApplicable)
            {
                _wasIncomplete = true;
                _observedFiles.UnionWith(DatabaseFilesOf(observation.Status));
            }

            _incompleteSince ??= _lastNotifiedAt;
        }

        var outcome = result.Outcome switch
        {
            OldDatabaseDisposalOutcome.ArchiveFailed => "書き出して保管に失敗（元のファイルは変更していません）",
            OldDatabaseDisposalOutcome.DeletePartiallyFailed => "元のファイルの一部を削除できませんでした",
            OldDatabaseDisposalOutcome.Interrupted => "中断",
            _ => result.Outcome.ToString(),
        };

        _logger.LogWarning(
            ActiveNotificationEventIds.OldDatabaseDisposalIncomplete,
            "[old-database-disposal-failed] 受信と保存は正常に続いています。以前の保存先（組み込みデータベース）の" +
            "ファイルの後始末が完了しませんでした（結果: {Outcome}。理由: {Reason}）。管理画面の昇格ページ" +
            "（/admin/promotion）で内容を確認し、もう一度実行してください。対象: {Files}（合計 {TotalSize}）。{Unresolvable}",
            outcome,
            result.Message,
            FormatFiles(observation.Status),
            FormatBytes(observation.Status.TotalSizeBytes),
            observation.UnresolvableReason is { } reason ? UnresolvableSentence(reason) : string.Empty);
    }

    private void LogIncomplete(OldDatabaseDisposalObservation observation, TimeSpan continued)
    {
        _logger.LogWarning(
            ActiveNotificationEventIds.OldDatabaseDisposalIncomplete,
            "[old-database-disposal-incomplete] 受信と保存は正常に続いています。以前の保存先（組み込みデータベース）の" +
            "ファイルの後始末が済んでいません（{ContinuedHours:F0} 時間継続）。ログを保存しておく期間による自動削除は、" +
            "このファイルには効きません。管理画面の昇格ページ（/admin/promotion）で後始末を行ってください。" +
            "対象: {Files}（合計 {TotalSize}）。{Unresolvable}本通知は {SuppressionWindow} ごとに再表示されます。",
            continued.TotalHours,
            FormatFiles(observation.Status),
            FormatBytes(observation.Status.TotalSizeBytes),
            observation.UnresolvableReason is { } reason ? UnresolvableSentence(reason) : string.Empty,
            ActiveNotificationConstants.OldDatabaseDisposalIncompleteSuppressionWindow);
    }

    /// <summary>旧ファイル（本体のパスで表す。<c>-wal</c>・<c>-shm</c> は本体に寄せる）。</summary>
    private static IEnumerable<string> DatabaseFilesOf(OldDatabaseDisposalStatus status)
    {
        foreach (var file in status.TargetFiles)
        {
            yield return file.Path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
                || file.Path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)
                    ? file.Path[..^4]
                    : file.Path;
        }

        if (status.MisplacedRecordedOldDatabasePath is { } misplaced)
        {
            yield return misplaced;
        }
    }

    private async Task RecordRemovedOutsideProductAsync(IReadOnlyList<string> paths)
    {
        try
        {
            await _auditRecorder.RecordAsync(
                new AuditEvent(
                    OccurredAt: _timeProvider.GetUtcNow(),
                    Kind: AuditEventKind.OldDatabaseRemovedOutsideProduct,
                    RemoteAddress: null,
                    RemotePort: null,
                    Detail: $"以前の保存先（組み込みデータベース）のファイル（{string.Join("、", paths)}）が、製品の後始末によらず" +
                            "無くなったことを観測しました（このプロセスでは、このファイルの後始末の完了を記録していません）。"),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 監査記録の経路は自身の障害時保持・書き戻しを持つため、ここでは診断に留める。
            _logger.LogInformation(ex, "以前の保存先のファイルの消失の監査記録に失敗しました。");
        }
    }

    private static string UnresolvableSentence(string reason) =>
        $"この状態は管理画面の操作では解消できません（{reason}）。利用者向けドキュメントの手作業の手順で後始末してください。";

    private static string FormatFiles(OldDatabaseDisposalStatus status)
    {
        var paths = status.TargetFiles.Select(file => file.Path).ToList();
        if (status.MisplacedRecordedOldDatabasePath is { } misplaced)
        {
            paths.Add(misplaced);
        }

        return paths.Count == 0 ? "-" : string.Join("、", paths);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("F1", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("F1", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KB",
    };
}
