using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Storage;
using Yagura.Storage.Sqlite;

namespace Yagura.Host.Administration;

/// <summary>
/// 蓄積ログ移行（database.md §6.2。DB-5。Issue #266）の実体。旧 SQLite ファイルから
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
/// <item><b>旧データを残置しない</b>: 旧ファイルの処分自体は DB-7（昇格ウィザードの処分手順）の
/// 管轄。本サービスは完了状態（<see cref="LogMigrationAvailability.Completed"/>）を提示して
/// 処分手順への引き継ぎ点を作る</item>
/// </list>
/// <para>
/// <b>実行前提</b>: 現行 provider が SQL Server であること（昇格済み）。移行は管理操作であり
/// 監査記録（2018）の対象。二重実行はサービス内の排他で直列化する。
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
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public LogMigrationService(
        string dataRoot,
        string oldSqliteDatabasePath,
        bool currentProviderIsSqlServer,
        ILogStore targetStore,
        LogStoreWriteGate? writeGate,
        IAuditRecorder auditRecorder,
        ILogger<LogMigrationService>? logger = null)
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
    }

    private string CheckpointPath => Path.Combine(_dataRoot, CheckpointFileName);

    /// <inheritdoc />
    public async Task<LogMigrationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!_currentProviderIsSqlServer)
        {
            return new LogMigrationStatus(LogMigrationAvailability.NotPromoted);
        }

        var checkpoint = ReadCheckpoint();
        if (checkpoint?.CompletedAtUtc is not null)
        {
            return new LogMigrationStatus(LogMigrationAvailability.Completed, MigratedCount: checkpoint.MigratedCount);
        }

        if (!File.Exists(_oldSqliteDatabasePath))
        {
            return new LogMigrationStatus(LogMigrationAvailability.NoSourceDatabase);
        }

        await using var source = new SqliteLogStore(_oldSqliteDatabasePath);
        var sourceCount = await ((IBulkLogReader)source).CountAsync(null, cancellationToken).ConfigureAwait(false);

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
        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("蓄積ログの移行は既に実行中です。");
        }

        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
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
            // レビュー指摘（fail-open 観点）: 移行の途中失敗（WriteBatchAsync 例外等）は移行先 DB を
            // 実際に変更した管理操作でありながら無記録になっていた——失敗・部分適用こそ証跡価値が
            // 高い。例外経路でも監査を残してから再送出する（チェックポイントは保持され再実行で
            // 追補できる。Detail に「途中失敗」と原因を含める）。実行不能状態（Ready 以外）での
            // 早期 throw は DB を変更しないため記録しない（when フィルタで除外）。
            await RecordMigrationAuditAsync(
                operatorAddress, authenticationScheme, authenticatedPrincipal,
                $"途中失敗（一部が移行先へ書き込まれた可能性あり。再実行で追補可能）: {ex.Message}").ConfigureAwait(false);
            throw;
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>
    /// 移行実行の監査記録（2018。イベントログ併記込み）。<b>記録失敗は移行の結果・失敗を
    /// 覆い隠してはならない</b>——監査シンク障害で移行報告が別の結果に化けないよう、
    /// <see cref="IAuditRecorder.RecordAsync"/>（例外を投げない契約）の呼び出し自体も
    /// try/catch で保護する（レビュー指摘への対応）。
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

        await using (var source = new SqliteLogStore(_oldSqliteDatabasePath))
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
                "旧 SQLite ファイルの処分は昇格ウィザードの処分手順（DB-7）で行ってください。",
                sourceTotal,
                migrated);
        }
        else
        {
            _logger.LogWarning(
                "蓄積ログの移行の完全性検証に不合格です（移行先範囲内 {TargetCount} < 移行元 {SourceTotal}）。" +
                "チェックポイントは保持されています——再実行で不足分を追補できます。",
                targetCount,
                sourceTotal);
        }

        return new LogMigrationResult(succeeded, migrated, sourceTotal, targetCount, message);
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
    /// ライブ受信の永続化をバッチの合間に通す。ILogStore の単一 writer 契約——Issue #151）。
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
