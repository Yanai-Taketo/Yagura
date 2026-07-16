using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Retention;

namespace Yagura.Host.Observability.Auditing;

/// <summary>
/// 監査記録の保持期間削除の定期実行スケジューラ（security.md §4.2 SEC-2。Issue #261）。
/// 保持期間（既定 365 日。<c>Audit:RetentionDays</c>）を超過した監査記録ファイルを削除する。
/// </summary>
/// <remarks>
/// <para>
/// <b>削除の単位はファイル</b>: <see cref="FileAuditRecorder"/> の日次ローテーション
/// （<c>audit-yyyyMMdd.jsonl</c>）により、削除は「期限切れファイルの削除」だけで済む——
/// 行レベルの書き換え・切り詰めを一切行わないため、SEC-3 の追記専用 ACE 構成
/// （既存内容の変更を許さない）と両立する。削除判定は**ファイルの最終書き込み時刻（UTC）が
/// cutoff（基準時刻 - 保持日数）より古い**こと——最終書き込みはファイル内の最新事象の時刻に
/// 一致するため、この条件を満たすファイルの中身はすべて保持期間超過である（日付名ファイルでは
/// ファイル名の日付と同値の判定になり、旧単一ファイル <c>audit.jsonl</c>——最終書き込みから
/// 保持期間が経過するまで中身に期限内の事象を含み得る——にも同じ規則で安全に適用できる）。
/// </para>
/// <para>
/// <b>スケジュール</b>: 起動時に 1 回 + 以降はログ本体の保持期間削除と同じ実行時刻
/// （<c>Retention:ExecutionTimeOfDay</c>。既定 03:00）に日次実行する。専用の実行時刻キーは
/// 設けない——運用者から見て「保持期間削除の時間帯」は 1 つであるほうが把握しやすく、
/// 削除はファイル単位で冪等（二重実行しても 2 回目は 0 件）なため、ログ本体側のような
/// キャッチアップ・二重実行防止の機構は要らない（起動時実行が実質のキャッチアップを兼ねる）。
/// </para>
/// <para>
/// <b>削除の証跡</b>: 1 件以上削除した場合、<see cref="AuditEventKind.AuditRetentionApplied"/>
/// （イベント ID 2015。情報）として記録する——証跡の削除自体を証跡に残す。イベントログ併記
/// （<see cref="FileAuditRecorder"/> の多段）により、監査ファイル側が後日消されても削除の事実は
/// イベントログに残る。0 件の実行は記録しない（毎日のノイズ行で監査記録を埋めない。ログ本体の
/// システムイベント記録——0 件でも記録——と異なる判断だが、あちらはキャッチアップ判定の入力を
/// 兼ねるのに対し、こちらの記録は証跡専用であり「破壊が起きたときだけ」で目的を満たす）。
/// </para>
/// <para>
/// <b>SEC-3（追記専用 ACE）構成との関係</b>: 監査記録領域にサービスアカウントの削除を許さない
/// ACE 分離が適用された環境では、本スケジューラの削除は <see cref="UnauthorizedAccessException"/>
/// で失敗する。これは構成として正しい状態（削除権限の分離が機能している）であり得るため、
/// エラーではなく警告として「ACL により削除できない——分離構成では削除を運用手順側で行う」旨を
/// 明示してログする（発火は日次実行時のみで、反復ノイズにならない）。
/// </para>
/// <para>
/// <b>保持期間が無効（<c>null</c>）の場合</b>: 何もしない（削除しない）。設定ファイル未設定時の
/// 既定は 365 日（SEC-2 確定値）であり、<c>null</c> になるのは不正値時のフォールバック
/// （<c>Retention:Days</c> と同じ「意図しない自動削除を避ける」安全側。configuration.md §8）のみ。
/// </para>
/// </remarks>
public sealed class AuditRetentionScheduler : IHostedService, IAsyncDisposable
{
    /// <summary>削除の証跡（2015）に列挙するファイル名の上限（Detail の肥大化防止）。</summary>
    internal const int DetailFileNameLimit = 20;

    private readonly string _auditDirectoryPath;
    private readonly int? _retentionDays;
    private readonly TimeOnly _executionTimeOfDay;
    private readonly IAuditRecorder _auditRecorder;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditRetentionScheduler> _logger;

    private CancellationTokenSource? _stoppingCts;
    private Task? _schedulerTask;

    /// <param name="dataRoot">データルートの絶対パス。</param>
    /// <param name="retentionDays">
    /// 監査記録の保持日数（<c>Audit:RetentionDays</c> の検証済み値。<c>null</c> = 削除しない）。
    /// </param>
    /// <param name="executionTimeOfDay">日次実行の時刻（サーバのローカル時刻。ログ本体と共有）。</param>
    /// <param name="auditRecorder">削除の証跡（2015）の記録先。</param>
    /// <param name="timeProvider">時刻源（<c>null</c> は <see cref="TimeProvider.System"/>）。</param>
    public AuditRetentionScheduler(
        string dataRoot,
        int? retentionDays,
        TimeOnly executionTimeOfDay,
        IAuditRecorder auditRecorder,
        TimeProvider? timeProvider = null,
        ILogger<AuditRetentionScheduler>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(auditRecorder);

        _auditDirectoryPath = Path.Combine(dataRoot, FileAuditRecorder.DirectoryName);
        _retentionDays = retentionDays;
        _executionTimeOfDay = executionTimeOfDay;
        _auditRecorder = auditRecorder;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<AuditRetentionScheduler>.Instance;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_stoppingCts is not null)
        {
            throw new InvalidOperationException("スケジューラは既に開始されている。");
        }

        _stoppingCts = new CancellationTokenSource();
        _schedulerTask = Task.Run(() => RunAsync(_stoppingCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stoppingCts is null)
        {
            return;
        }

        await _stoppingCts.CancelAsync().ConfigureAwait(false);

        if (_schedulerTask is not null)
        {
            try
            {
                await _schedulerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 停止要求による正常終了。
            }
        }

        _stoppingCts.Dispose();
        _stoppingCts = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        // 起動時に 1 回実行する（実質のキャッチアップ。remarks「スケジュール」参照）。
        await DeleteExpiredOnceAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = RetentionScheduler.ComputeDelayUntilNextExecution(
                _timeProvider.GetUtcNow(), _executionTimeOfDay);

            try
            {
                await Task.Delay(delay, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await DeleteExpiredOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 保持期間削除を 1 回実行する（テスト用の直接呼び出し口を兼ねる）。削除したファイル数を返す。
    /// いかなる失敗でも例外を伝播させない（削除の失敗が監査記録・受信・UI のいずれも妨げない）。
    /// </summary>
    internal async Task<int> DeleteExpiredOnceAsync(CancellationToken cancellationToken)
    {
        if (_retentionDays is not { } retentionDays)
        {
            return 0;
        }

        var now = _timeProvider.GetUtcNow();
        var cutoffUtc = now - TimeSpan.FromDays(retentionDays);
        var deletedFileNames = new List<string>();
        var aclDenied = false;

        try
        {
            if (!Directory.Exists(_auditDirectoryPath))
            {
                return 0;
            }

            foreach (var filePath in Directory.EnumerateFiles(
                _auditDirectoryPath, FileAuditRecorder.AuditFileSearchPattern))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // 判定はファイルの最終書き込み時刻（= ファイル内の最新事象の時刻）——
                    // これが cutoff より古ければ中身はすべて保持期間超過（クラス remarks 参照）。
                    if (File.GetLastWriteTimeUtc(filePath) >= cutoffUtc.UtcDateTime)
                    {
                        continue;
                    }

                    File.Delete(filePath);
                    deletedFileNames.Add(Path.GetFileName(filePath));
                }
                catch (UnauthorizedAccessException ex)
                {
                    // SEC-3 の追記専用 ACE 構成（削除権限の分離）では正しい挙動であり得る
                    // （クラス remarks 参照）。ファイルごとに反復せず、実行 1 回につき 1 度だけ警告する。
                    if (!aclDenied)
                    {
                        aclDenied = true;
                        _logger.LogWarning(
                            ex,
                            "[audit-retention-acl-denied] 保持期間を超過した監査記録ファイル {Path} を ACL のため削除できません。" +
                            "監査記録領域に削除権限の分離（追記専用 ACE。security.md §4.2 SEC-3）を適用している場合、" +
                            "期限切れファイルの削除は ACL を管理する側の運用手順で行ってください。",
                            filePath);
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(
                        ex,
                        "[audit-retention-delete-failed] 保持期間を超過した監査記録ファイル {Path} の削除に失敗しました" +
                        "（次回の定期実行で再試行されます）。",
                        filePath);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停止要求。ここまでに削除できた分の証跡は下で記録する。
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "[audit-retention-enumerate-failed] 監査記録ディレクトリ {Path} の列挙に失敗しました。",
                _auditDirectoryPath);
        }

        if (deletedFileNames.Count > 0)
        {
            var listedNames = string.Join(",", deletedFileNames.Take(DetailFileNameLimit));
            var suffix = deletedFileNames.Count > DetailFileNameLimit
                ? $",...(+{deletedFileNames.Count - DetailFileNameLimit})"
                : string.Empty;

            // 証跡の削除自体を証跡に残す（2015。イベントログ併記込み——FileAuditRecorder の多段）。
            await _auditRecorder.RecordAsync(
                new AuditEvent(
                    OccurredAt: now,
                    Kind: AuditEventKind.AuditRetentionApplied,
                    RemoteAddress: null,
                    RemotePort: null,
                    Detail: $"deleted={deletedFileNames.Count} retentionDays={retentionDays} " +
                        $"cutoffUtc={cutoffUtc.UtcDateTime:O} files={listedNames}{suffix}"),
                CancellationToken.None).ConfigureAwait(false);
        }

        return deletedFileNames.Count;
    }
}
