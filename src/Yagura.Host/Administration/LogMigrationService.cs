using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Administration.OldDatabaseDisposal;
using Yagura.Storage;
using Yagura.Storage.Sqlite;

namespace Yagura.Host.Administration;

/// <summary>
/// 蓄積ログ移行（database.md §6.2。DB-5）の実体。旧 SQLite ファイルから
/// 現行 provider（昇格後の SQL Server）へ、古い順にバッチで移送する。
/// </summary>
/// <remarks>
/// <para>database.md §6.2 の固定要件 6 点への対応:</para>
/// <list type="number">
/// <item><b>受信を止めない</b>: 書き込みは他経路と同じ書き込みゲート（<see cref="LogStoreWriteGate"/>）
/// を**バッチ単位で取得・解放**して行う——ゲートの保持は 1 バッチ（<see cref="BatchSize"/> 件の
/// INSERT 1 トランザクション）ぶんに限られ、ライブ受信の永続化はバッチの合間に進む</item>
/// <item><b>完全性検証を内蔵</b>: 完了時に「移行元の総件数」と「移行先の移行範囲
/// （最終 ReceivedAt 以前）の件数」を突合する。合格 = 移行先 ≥ 移行元（差分は at-least-once
/// 再開による重複として説明できる。完全一致は要求しない——検証中もライブ受信が継続するため）</item>
/// <item><b>中断・再開が安全</b>: チェックポイント（データルート直下の
/// <see cref="CheckpointFileName"/>。バッチごとに原子的置換で更新）に最終移行位置
/// （ReceivedAt, Id）を持ち、再開はその位置の**次**から読む。クラッシュ時は最終チェック
/// ポイント以降のバッチが重複し得る（at-least-once。要件②が吸収）</item>
/// <item><b>ReceivedAt を再刻印しない</b>: 読み出したレコードをそのまま
/// <see cref="ILogStore.WriteBatchAsync"/> へ渡す（Id のみ provider が再採番——契約どおり）</item>
/// <item><b>移行由来の事後識別</b>: 完了時に移行範囲（最古〜最新 ReceivedAt）と件数を
/// システムイベント（Kind = <see cref="SystemEventKinds.MigrationImport"/>）として移行先に記録する
/// ——「この範囲のレコードは移行で投入された」を後から機械可読に辿れる（レコード単位の
/// マーカー列は追加しない——スキーマ変更を避け、範囲 + 件数の記録で識別要件を満たす判断）</item>
/// <item><b>旧データを残置しない</b>: 旧ファイルの処分は §6.3 の後始末（<see cref="IOldDatabaseDisposalService"/>）。
/// 本サービスは完了状態（<see cref="LogMigrationAvailability.Completed"/>）を提示して引き継ぎ点を作る</item>
/// </list>
/// <para>
/// <b>システムイベントも写す</b>: 再起動による切替では切替前のシステムイベント（受信できなかった時間帯の
/// 記録等）が旧ファイルにだけ残るため、ログ本体の移行の後に移行先に無いものを複写し、完了の条件に含める
/// （<see cref="SystemEventReconciler"/>。重複は照合で避ける）。
/// </para>
/// <para>
/// <b>実行前提</b>: 現行 provider が SQL Server であること（昇格済み）。移行は管理操作であり
/// 監査記録（2018）の対象。旧ファイルを開く操作（状態取得の件数読み取り・移行）は処分と同じ排他
/// （<see cref="OldDatabaseAccessGate"/>）の下で行い、旧ファイルはファイルを作成しない開き方で開く。
/// </para>
/// </remarks>
public sealed class LogMigrationService : ILogMigrationService
{
    /// <summary>チェックポイントファイル名（データルート直下）。</summary>
    public const string CheckpointFileName = "log-migration-checkpoint.json";

    /// <summary>1 バッチの件数（書き込みゲートの保持時間を短く保つ粒度。仮値）。</summary>
    internal const int BatchSize = 500;

    private readonly string _dataRoot;
    private readonly string _oldSqliteDatabasePath;
    private readonly bool _currentProviderIsSqlServer;
    private readonly ILogStore _targetStore;
    private readonly LogStoreWriteGate? _writeGate;
    private readonly IAuditRecorder _auditRecorder;
    private readonly ILogger<LogMigrationService> _logger;
    private readonly OldDatabaseAccessGate _accessGate;
    private readonly CancellationToken _stopping;

    /// <summary>状態取得が旧ファイルの排他を待つ上限（確認材料の読み取りなど短い保持と競合した場合）。</summary>
    private static readonly TimeSpan StatusGateWait = TimeSpan.FromSeconds(5);

    public LogMigrationService(
        string dataRoot,
        string oldSqliteDatabasePath,
        bool currentProviderIsSqlServer,
        ILogStore targetStore,
        LogStoreWriteGate? writeGate,
        IAuditRecorder auditRecorder,
        ILogger<LogMigrationService>? logger = null,
        OldDatabaseAccessGate? accessGate = null,
        CancellationToken stopping = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldSqliteDatabasePath);
        ArgumentNullException.ThrowIfNull(targetStore);
        ArgumentNullException.ThrowIfNull(auditRecorder);

        _dataRoot = dataRoot;
        _oldSqliteDatabasePath = oldSqliteDatabasePath;
        _currentProviderIsSqlServer = currentProviderIsSqlServer;
        _targetStore = targetStore;
        _writeGate = writeGate;
        _auditRecorder = auditRecorder;
        _logger = logger ?? NullLogger<LogMigrationService>.Instance;
        _accessGate = accessGate ?? new OldDatabaseAccessGate();
        _stopping = stopping;
    }

    private string CheckpointPath => CheckpointPathOf(_dataRoot);

    private static string CheckpointPathOf(string dataRoot) => Path.Combine(dataRoot, CheckpointFileName);

    /// <inheritdoc />
    public async Task<LogMigrationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!_currentProviderIsSqlServer)
        {
            return new LogMigrationStatus(LogMigrationAvailability.NotPromoted);
        }

        using var lease = await _accessGate.TryAcquireAsync(
            OldDatabaseAccessHolder.Inspection, StatusGateWait, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            return new LogMigrationStatus(_accessGate.Holder == OldDatabaseAccessHolder.Disposal
                ? LogMigrationAvailability.DisposalInProgress
                : LogMigrationAvailability.Running);
        }

        return await GetStatusCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 状態の判定（呼び出し側が旧ファイルの排他を保持する）。完了の判定は database.md §6.3 の規則 1・2:
    /// 完了記録が移行済み位置を持ち、その位置より後のレコードが旧ファイルに無いこと。位置を持たない完了記録
    /// （移行元 0 件で完了）は、旧ファイルにレコードが 1 件でもあれば未完了とみなす。完了後でも移行先に無い
    /// システムイベントがあれば追補できるよう実行可能を返す。
    /// </summary>
    private async Task<LogMigrationStatus> GetStatusCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_oldSqliteDatabasePath))
        {
            return new LogMigrationStatus(LogMigrationAvailability.NoSourceDatabase);
        }

        var checkpoint = ReadCheckpoint();
        var completedRecorded = checkpoint?.CompletedAtUtc is not null;
        var hasPosition = checkpoint?.LastReceivedAtUtc is not null && checkpoint.LastId is not null;

        long sourceCount;
        long addedAfterCompletion = 0;
        var missingSystemEventCount = 0;
        try
        {
            await using (var source = new SqliteLogStore(_oldSqliteDatabasePath, createIfMissing: false))
            {
                sourceCount = await ((IBulkLogReader)source).CountAsync(null, cancellationToken).ConfigureAwait(false);
            }

            if (completedRecorded && hasPosition)
            {
                addedAfterCompletion = (await OldSqliteDatabase.RangeAfterPositionAsync(
                    _oldSqliteDatabasePath,
                    new DateTimeOffset(DateTime.SpecifyKind(checkpoint!.LastReceivedAtUtc!.Value, DateTimeKind.Utc)),
                    checkpoint.LastId!.Value,
                    cancellationToken).ConfigureAwait(false)).Count;
            }

            if (completedRecorded)
            {
                var sourceEvents = await OldSqliteDatabase.ReadSystemEventsAsync(_oldSqliteDatabasePath, cancellationToken)
                    .ConfigureAwait(false);
                missingSystemEventCount = (await SystemEventReconciler.FindMissingInTargetAsync(
                    sourceEvents, _targetStore, cancellationToken).ConfigureAwait(false)).Count;
            }
        }
        catch (Exception ex) when (ex is SqliteException or LogStoreWriteException)
        {
            // 内容を読めない旧ファイル（0 バイト・識別子の破損）は移行の対象にならない——後始末（§6.3）が
            // 削除でのみ扱う。
            _logger.LogInformation(ex, "旧 SQLite ファイルを読めないため、移行の対象はありません。");
            return new LogMigrationStatus(LogMigrationAvailability.NoSourceDatabase);
        }

        if (completedRecorded)
        {
            var logsCompleted = hasPosition ? addedAfterCompletion == 0 : sourceCount == 0;
            if (logsCompleted && missingSystemEventCount == 0)
            {
                return new LogMigrationStatus(LogMigrationAvailability.Completed, MigratedCount: checkpoint!.MigratedCount);
            }

            return new LogMigrationStatus(
                LogMigrationAvailability.Ready,
                SourceRecordCount: sourceCount,
                MigratedCount: hasPosition ? checkpoint!.MigratedCount : 0,
                RecordsAddedAfterCompletion: hasPosition ? addedAfterCompletion : 0);
        }

        return new LogMigrationStatus(
            LogMigrationAvailability.Ready,
            SourceRecordCount: sourceCount,
            MigratedCount: checkpoint?.MigratedCount ?? 0);
    }

    /// <inheritdoc />
    public async Task<LogMigrationResult> RunAsync(
        string? operatorAddress,
        string? authenticationScheme,
        string? authenticatedPrincipal,
        IProgress<LogMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // サービスの停止要求でも止める——呼び出し側（画面）が取り消しトークンを渡さなくても、停止手順
        // （メモリ上のログの一時保管への退避）を移行が待たせない。中断はチェックポイントから再開できる。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping);
        cancellationToken = linked.Token;

        using var lease = await _accessGate.TryAcquireAsync(
            OldDatabaseAccessHolder.Migration, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            throw new InvalidOperationException(_accessGate.Holder == OldDatabaseAccessHolder.Disposal
                ? "以前の保存先のファイルの後始末を実行中のため、移行を開始できません。"
                : "蓄積ログの移行は既に実行中です。");
        }

        var status = await GetStatusCoreAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (status.Availability is not LogMigrationAvailability.Ready)
            {
                throw new InvalidOperationException($"移行を実行できる状態ではありません（{status.Availability}）。");
            }

            var result = await ExecuteAsync(status, progress, cancellationToken).ConfigureAwait(false);
            await RecordMigrationAuditAsync(operatorAddress, authenticationScheme, authenticatedPrincipal, result.Message).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex) when (status.Availability is LogMigrationAvailability.Ready)
        {
            // 監査の網羅性（fail-open 観点）: 移行の途中失敗（WriteBatchAsync 例外等）は移行先 DB を
            // 実際に変更した管理操作であるため、無記録のままにしない——失敗・部分適用こそ証跡価値が
            // 高い。例外経路でも監査を残してから再送出する（チェックポイントは保持され再実行で
            // 追補できる。Detail に「途中失敗」と原因を含める）。実行不能状態（Ready 以外）での
            // 早期 throw は DB を変更しないため記録しない（when フィルタで除外）。
            await RecordMigrationAuditAsync(
                operatorAddress, authenticationScheme, authenticatedPrincipal,
                $"途中失敗（一部が移行先へ書き込まれた可能性あり。再実行で追補可能）: {ex.Message}").ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 移行実行の監査記録（2018。イベントログ併記込み）。<b>記録失敗は移行の結果・失敗を
    /// 覆い隠してはならない</b>——監査シンク障害で移行報告が別の結果に化けないよう、
    /// <see cref="IAuditRecorder.RecordAsync"/>（例外を投げない契約）の呼び出し自体も
    /// try/catch で保護する。
    /// </summary>
    private async Task RecordMigrationAuditAsync(
        string? operatorAddress, string? authenticationScheme, string? authenticatedPrincipal, string detailMessage)
    {
        try
        {
            await _auditRecorder.RecordAsync(
                new AuditEvent(
                    OccurredAt: DateTimeOffset.UtcNow,
                    Kind: AuditEventKind.LogMigrationExecuted,
                    RemoteAddress: operatorAddress,
                    RemotePort: null,
                    Detail: $"蓄積ログの移行（SQLite → SQL Server）: {detailMessage}",
                    AuthenticationScheme: authenticationScheme,
                    AuthenticatedPrincipal: authenticatedPrincipal),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "蓄積ログ移行の監査記録に失敗しました（移行自体の結果には影響しません）。");
        }
    }

    private async Task<LogMigrationResult> ExecuteAsync(
        LogMigrationStatus status,
        IProgress<LogMigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sourceTotal = status.SourceRecordCount!.Value;
        var checkpoint = ReadCheckpoint() ?? new CheckpointFile();
        var migrated = checkpoint.MigratedCount;
        var resumeAfter = checkpoint.LastReceivedAtUtc is { } lastReceivedAt && checkpoint.LastId is { } lastId
            ? new BulkReadCursor(new DateTimeOffset(lastReceivedAt, TimeSpan.Zero), lastId)
            : null;

        DateTimeOffset? oldestMigrated = checkpoint.OldestReceivedAtUtc is { } oldest
            ? new DateTimeOffset(oldest, TimeSpan.Zero)
            : null;
        DateTimeOffset? newestMigrated = resumeAfter?.ReceivedAt;

        await using (var source = new SqliteLogStore(_oldSqliteDatabasePath, createIfMissing: false))
        {
            var batch = new List<LogRecord>(BatchSize);

            await foreach (var record in ((IBulkLogReader)source).ReadAllAscendingAsync(resumeAfter, cancellationToken)
                .ConfigureAwait(false))
            {
                batch.Add(record);
                if (batch.Count >= BatchSize)
                {
                    (migrated, oldestMigrated, newestMigrated) = await FlushBatchAsync(
                        batch, migrated, oldestMigrated, cancellationToken).ConfigureAwait(false);
                    progress?.Report(new LogMigrationProgress(migrated, sourceTotal));
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                (migrated, oldestMigrated, newestMigrated) = await FlushBatchAsync(
                    batch, migrated, oldestMigrated, cancellationToken).ConfigureAwait(false);
                progress?.Report(new LogMigrationProgress(migrated, sourceTotal));
            }
        }

        // 完全性検証（要件②）: 移行先の「移行範囲内（最終 ReceivedAt 以前）」件数 ≥ 移行元総件数。
        var targetCount = newestMigrated is { } newest && _targetStore is IBulkLogReader targetReader
            ? await targetReader.CountAsync(newest, cancellationToken).ConfigureAwait(false)
            : 0;

        var succeeded = sourceTotal == 0 || targetCount >= sourceTotal;
        var message = succeeded
            ? $"完了（検証合格: 移行先範囲内 {targetCount} ≥ 移行元 {sourceTotal}。差分は再開時の重複として説明可能）"
            : $"検証不合格（移行先範囲内 {targetCount} < 移行元 {sourceTotal}——欠落の疑い。再実行してください）";

        if (succeeded)
        {
            var (copied, stillMissing) = await MigrateSystemEventsAsync(cancellationToken).ConfigureAwait(false);
            if (stillMissing > 0)
            {
                succeeded = false;
                message = $"検証不合格（システムイベント {stillMissing} 件が移行先にありません。再実行してください）";
            }
            else
            {
                message += $"。システムイベント {copied} 件を複写";
            }
        }

        if (succeeded)
        {
            // 移行由来の事後識別（要件⑤）: 移行範囲と件数をシステムイベントとして移行先に残す。
            if (sourceTotal > 0 && oldestMigrated is { } rangeStart && newestMigrated is { } rangeEnd)
            {
                await WriteUnderGateAsync(
                    () => _targetStore.WriteSystemEventAsync(
                        new SystemEvent(
                            SystemEventKinds.MigrationImport,
                            rangeStart,
                            rangeEnd,
                            Approximate: false,
                            Details: $"migrated={migrated} sourceTotal={sourceTotal}"),
                        cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }

            WriteCheckpoint(ReadCheckpoint() is { } finalState
                ? finalState with { CompletedAtUtc = DateTime.UtcNow }
                : new CheckpointFile { MigratedCount = migrated, CompletedAtUtc = DateTime.UtcNow });

            _logger.LogInformation(
                "蓄積ログの移行が完了しました（移行元 {SourceTotal} 件・累計移行 {Migrated} 件・検証合格）。" +
                "以前の保存先（組み込みデータベース）のファイルの後始末は、管理画面の昇格ページで行ってください。",
                sourceTotal,
                migrated);
        }
        else
        {
            _logger.LogWarning(
                "蓄積ログの移行の完全性検証に不合格です（{Message}）。" +
                "チェックポイントは保持されています——再実行で不足分を追補できます。",
                message);
        }

        return new LogMigrationResult(succeeded, migrated, sourceTotal, targetCount, message);
    }

    /// <summary>
    /// 旧ファイルのシステムイベントのうち移行先に無いものを複写し、複写後にもう一度照合する。
    /// </summary>
    private async Task<(int Copied, int StillMissing)> MigrateSystemEventsAsync(CancellationToken cancellationToken)
    {
        var sourceEvents = await OldSqliteDatabase.ReadSystemEventsAsync(_oldSqliteDatabasePath, cancellationToken)
            .ConfigureAwait(false);
        var missing = await SystemEventReconciler.FindMissingInTargetAsync(sourceEvents, _targetStore, cancellationToken)
            .ConfigureAwait(false);

        foreach (var systemEvent in missing)
        {
            await WriteUnderGateAsync(
                () => _targetStore.WriteSystemEventAsync(systemEvent with { Id = null }, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        var stillMissing = missing.Count == 0
            ? 0
            : (await SystemEventReconciler.FindMissingInTargetAsync(sourceEvents, _targetStore, cancellationToken)
                .ConfigureAwait(false)).Count;
        return (missing.Count, stillMissing);
    }

    private async Task<(long Migrated, DateTimeOffset? Oldest, DateTimeOffset? Newest)> FlushBatchAsync(
        List<LogRecord> batch,
        long migratedSoFar,
        DateTimeOffset? oldestMigrated,
        CancellationToken cancellationToken)
    {
        // 要件④: レコードはそのまま書く（ReceivedAt 再刻印なし。Id は provider 再採番——契約どおり）。
        await WriteUnderGateAsync(
            () => _targetStore.WriteBatchAsync(batch, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        var migrated = migratedSoFar + batch.Count;
        var oldest = oldestMigrated ?? batch[0].ReceivedAt;
        var last = batch[^1];

        // 要件③: バッチ確定ごとにチェックポイントを原子的置換で更新する。
        WriteCheckpoint(new CheckpointFile
        {
            LastReceivedAtUtc = last.ReceivedAt.UtcDateTime,
            LastId = last.Id,
            OldestReceivedAtUtc = oldest.UtcDateTime,
            MigratedCount = migrated,
        });

        return (migrated, oldest, last.ReceivedAt);
    }

    /// <summary>
    /// 書き込みゲート越しに 1 操作を実行する（要件①: ゲート保持をバッチ単位に限定し、
    /// ライブ受信の永続化をバッチの合間に通す。ILogStore の単一 writer 契約）。
    /// </summary>
    private async Task WriteUnderGateAsync(Func<Task> writeOperation, CancellationToken cancellationToken)
    {
        if (_writeGate is null)
        {
            await writeOperation().ConfigureAwait(false);
            return;
        }

        using var gateHold = await _writeGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await writeOperation().ConfigureAwait(false);
    }

    /// <summary>
    /// 移行チェックポイントの状態を読む（処分の完了判定が使う）。無い場合は <see langword="null"/>。
    /// 読めない場合は「完了記録なし」として扱う（欠落側に倒さない）。
    /// </summary>
    public static LogMigrationCheckpointState? ReadCheckpointState(string dataRoot)
    {
        CheckpointFile? checkpoint;
        try
        {
            var path = CheckpointPathOf(dataRoot);
            if (!File.Exists(path))
            {
                return null;
            }

            checkpoint = JsonSerializer.Deserialize<CheckpointFile>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new LogMigrationCheckpointState(Completed: false, Position: null, MigratedCount: 0);
        }

        if (checkpoint is null)
        {
            return new LogMigrationCheckpointState(Completed: false, Position: null, MigratedCount: 0);
        }

        (DateTimeOffset, long)? position = checkpoint.LastReceivedAtUtc is { } lastAt && checkpoint.LastId is { } lastId
            ? (new DateTimeOffset(DateTime.SpecifyKind(lastAt, DateTimeKind.Utc)), lastId)
            : null;
        return new LogMigrationCheckpointState(checkpoint.CompletedAtUtc is not null, position, checkpoint.MigratedCount);
    }

    /// <summary>
    /// 移行チェックポイントを撤去する（処分の完了時。旧ファイルの消滅後に残すと、将来あらためて組み込み DB
    /// から昇格した際に移行を「完了済み」と誤判定するため）。撤去したチェックポイントの状態を返す。
    /// </summary>
    public static LogMigrationCheckpointState? RemoveCheckpoint(string dataRoot)
    {
        var state = ReadCheckpointState(dataRoot);
        var path = CheckpointPathOf(dataRoot);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return state;
    }

    private CheckpointFile? ReadCheckpoint()
    {
        try
        {
            if (!File.Exists(CheckpointPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<CheckpointFile>(File.ReadAllText(CheckpointPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 破損したチェックポイントは「先頭からやり直し」に倒す（at-least-once——重複は
            // 検証（要件②）が説明する。欠落側に倒れないことが重要）。
            _logger.LogWarning(ex, "移行チェックポイントを読めないため、先頭からの移行として扱います。");
            return null;
        }
    }

    private void WriteCheckpoint(CheckpointFile checkpoint)
    {
        var tempPath = CheckpointPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(checkpoint));
        File.Move(tempPath, CheckpointPath, overwrite: true);
    }

    /// <summary>チェックポイントのファイル形式（additive-only。データルート直下）。</summary>
    private sealed record CheckpointFile
    {
        public DateTime? LastReceivedAtUtc { get; init; }
        public long? LastId { get; init; }
        public DateTime? OldestReceivedAtUtc { get; init; }
        public long MigratedCount { get; init; }
        public DateTime? CompletedAtUtc { get; init; }
    }
}

/// <summary>移行チェックポイントの状態（処分の完了判定・撤去の要約に使う）。</summary>
/// <param name="Completed">完了記録（完了時の検証の合格）があるか。</param>
/// <param name="Position">移行済み位置（最終の <c>(ReceivedAt, Id)</c>）。移行元 0 件で完了した場合は無い。</param>
/// <param name="MigratedCount">累計の移行件数。</param>
public sealed record LogMigrationCheckpointState(
    bool Completed,
    (DateTimeOffset ReceivedAt, long Id)? Position,
    long MigratedCount);
