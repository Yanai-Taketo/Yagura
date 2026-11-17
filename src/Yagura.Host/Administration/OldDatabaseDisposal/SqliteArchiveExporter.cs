using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Yagura.Abstractions.Administration;

namespace Yagura.Host.Administration.OldDatabaseDisposal;

/// <summary>書き出しと検証の結果。</summary>
/// <param name="ExportedFilePath">検証に合格した作業中のファイル（改名前）。</param>
/// <param name="Sha256">そのファイルの SHA-256（16 進小文字）。</param>
/// <param name="IntegrityCheck"><c>PRAGMA integrity_check</c> の結果（合格なら <c>ok</c>）。</param>
/// <param name="SourceRecordCount">旧ファイルのログ件数。</param>
/// <param name="ExportedRecordCount">書き出したファイルのログ件数。</param>
/// <param name="SourceSystemEventCount">旧ファイルのシステムイベント件数。</param>
/// <param name="ExportedSystemEventCount">書き出したファイルのシステムイベント件数。</param>
/// <param name="SchemaVersion">スキーマ版。</param>
/// <param name="OldestReceivedAt">ログの ReceivedAt の最古。</param>
/// <param name="NewestReceivedAt">同、最新。</param>
public sealed record ArchiveExportResult(
    string ExportedFilePath,
    string Sha256,
    string IntegrityCheck,
    long SourceRecordCount,
    long ExportedRecordCount,
    long SourceSystemEventCount,
    long ExportedSystemEventCount,
    int? SchemaVersion,
    DateTimeOffset? OldestReceivedAt,
    DateTimeOffset? NewestReceivedAt);

/// <summary>書き出し・検証の失敗（退避の失敗として扱う。元のファイルは変更しない）。</summary>
public sealed class ArchiveExportException(OldDatabaseDisposalReason reason, string message, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>失敗の理由。</summary>
    public OldDatabaseDisposalReason Reason { get; } = reason;
}

/// <summary>
/// 旧ファイルの内容を作業用サブフォルダの中へ単一ファイルとして書き出し、検証する（database.md §6.3 手順 5〜7）。
/// </summary>
/// <remarks>
/// <para>
/// <b>方式</b>: <c>VACUUM INTO</c> を 2 段で使う——①旧ファイル → 作業ファイル 1、②作業ファイル 1 から
/// 管理者アカウント台帳の表を削除 → 作業ファイル 2 へ <c>VACUUM INTO</c>。<c>VACUUM INTO</c> は論理的な内容から
/// ファイルを組み立て直すため、旧ファイルの空き領域（保存期間で削除したログ・削除された行）も、②で落とした台帳の
/// ページも出力に現れない。ページを空き領域ごと複写する online backup API の単独使用は採らない（DB-12 ②）。
/// 一時領域も作業用サブフォルダの中だけに置く（作業ファイル 1 と 2 が一時的に同時に存在する）。
/// </para>
/// <para>
/// <b>取り消し</b>: 文の実行中は <c>sqlite3_interrupt</c> で止める（停止要求に文の途中で応じる）。
/// </para>
/// <para>
/// 呼び出し側が旧ファイルの排他を保持し、旧ファイルは WAL を反映済み（静止した状態）であることを前提とする。
/// </para>
/// </remarks>
public static class SqliteArchiveExporter
{
    /// <summary>管理者アカウント台帳の表（書き出しから除く）。</summary>
    internal static readonly IReadOnlyList<string> ExcludedTables = ["AdminAccounts", "AdminAccountsSchemaVersion"];

    /// <summary>書き出しと検証を行う。</summary>
    public static async Task<ArchiveExportResult> ExportAsync(
        string sourcePath,
        string workDirectory,
        Action<OldDatabaseDisposalStage> onStage,
        CancellationToken cancellationToken)
    {
        var stage1 = Path.Combine(workDirectory, "export-1.tmp");
        var stage2 = Path.Combine(workDirectory, "export-2.tmp");

        onStage(OldDatabaseDisposalStage.Exporting);
        await RunGuardedAsync(OldDatabaseDisposalReason.ExportFailed, "書き出し", async () =>
        {
            await using var source = await OldSqliteDatabase.OpenAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            await VacuumIntoAsync(source, stage1, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        onStage(OldDatabaseDisposalStage.RemovingResidue);
        await RunGuardedAsync(OldDatabaseDisposalReason.ExportFailed, "管理者アカウント台帳と空き領域の残留の除去", async () =>
        {
            await using (var intermediate = await OldSqliteDatabase.OpenAsync(stage1, cancellationToken).ConfigureAwait(false))
            {
                foreach (var table in ExcludedTables)
                {
                    await ExecuteAsync(intermediate, $"DROP TABLE IF EXISTS \"{table}\";", cancellationToken).ConfigureAwait(false);
                }

                await VacuumIntoAsync(intermediate, stage2, cancellationToken).ConfigureAwait(false);
            }

            DeleteWithSidecars(stage1);

            // 単一ファイルで完結させる（-wal・-shm を伴わない形にする）。
            await using var exported = await OldSqliteDatabase.OpenAsync(stage2, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(exported, "PRAGMA journal_mode=DELETE;", cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        onStage(OldDatabaseDisposalStage.Verifying);
        return await VerifyAsync(sourcePath, stage2, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>検証（database.md §6.3 手順 7）。いずれかに不合格なら <see cref="ArchiveExportException"/>。</summary>
    internal static async Task<ArchiveExportResult> VerifyAsync(string sourcePath, string exportedPath, CancellationToken cancellationToken)
    {
        OldDatabaseSummary source;
        OldDatabaseSummary exported;
        string integrity;
        var leftoverTables = new List<string>();

        try
        {
            source = await OldSqliteDatabase.ReadSummaryAsync(sourcePath, cancellationToken).ConfigureAwait(false);

            await using var connection = await OldSqliteDatabase.OpenAsync(exportedPath, cancellationToken, SqliteOpenMode.ReadOnly)
                .ConfigureAwait(false);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA integrity_check;";
                using var interrupt = OldSqliteDatabase.RegisterInterrupt(connection, cancellationToken);
                integrity = Convert.ToString(
                    await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture) ?? string.Empty;
            }

            exported = await OldSqliteDatabase.ReadSummaryAsync(connection, cancellationToken).ConfigureAwait(false);
            foreach (var table in ExcludedTables)
            {
                if (await OldSqliteDatabase.TableExistsAsync(connection, table, cancellationToken).ConfigureAwait(false))
                {
                    leftoverTables.Add(table);
                }
            }
        }
        catch (SqliteException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("検証を取り消しました。", ex, cancellationToken);
        }
        catch (SqliteException ex)
        {
            throw new ArchiveExportException(OldDatabaseDisposalReason.VerificationFailed, $"書き出したファイルを検証できません: {ex.Message}", ex);
        }

        if (!string.Equals(integrity, "ok", StringComparison.Ordinal))
        {
            throw new ArchiveExportException(OldDatabaseDisposalReason.VerificationFailed, $"整合性検査に不合格です（{integrity}）。");
        }

        if (source.RecordCount != exported.RecordCount || source.SystemEventCount != exported.SystemEventCount)
        {
            throw new ArchiveExportException(
                OldDatabaseDisposalReason.VerificationFailed,
                $"件数が一致しません（ログ {exported.RecordCount}/{source.RecordCount}・" +
                $"システムイベント {exported.SystemEventCount}/{source.SystemEventCount}）。");
        }

        if (source.SchemaVersion != exported.SchemaVersion)
        {
            throw new ArchiveExportException(
                OldDatabaseDisposalReason.VerificationFailed,
                $"スキーマ版が一致しません（{exported.SchemaVersion}/{source.SchemaVersion}）。");
        }

        if (leftoverTables.Count > 0)
        {
            throw new ArchiveExportException(
                OldDatabaseDisposalReason.VerificationFailed,
                $"管理者アカウント台帳が残っています（{string.Join(", ", leftoverTables)}）。");
        }

        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            if (File.Exists(exportedPath + suffix))
            {
                throw new ArchiveExportException(
                    OldDatabaseDisposalReason.VerificationFailed,
                    $"書き出したファイルが単一ファイルで完結していません（{Path.GetFileName(exportedPath + suffix)} が残っています）。");
            }
        }

        var sha256 = await ComputeSha256Async(exportedPath, cancellationToken).ConfigureAwait(false);
        return new ArchiveExportResult(
            exportedPath,
            sha256,
            integrity,
            source.RecordCount,
            exported.RecordCount,
            source.SystemEventCount,
            exported.SystemEventCount,
            exported.SchemaVersion,
            exported.OldestReceivedAt,
            exported.NewestReceivedAt);
    }

    /// <summary>SHA-256 を求める（取り消しに応じる）。</summary>
    internal static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>作業ファイルと、SQLite が作り得る付随ファイルを削除する。</summary>
    internal static void DeleteWithSidecars(string path)
    {
        foreach (var candidate in new[] { path, path + "-journal", path + "-wal", path + "-shm" })
        {
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
    }

    private static async Task VacuumIntoAsync(SqliteConnection connection, string destination, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $destination;";
        command.Parameters.Add("$destination", SqliteType.Text).Value = destination;
        using var interrupt = OldSqliteDatabase.RegisterInterrupt(connection, cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var interrupt = OldSqliteDatabase.RegisterInterrupt(connection, cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunGuardedAsync(
        OldDatabaseDisposalReason reason,
        string stepName,
        Func<Task> step,
        CancellationToken cancellationToken)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (SqliteException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException($"{stepName}を取り消しました。", ex, cancellationToken);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw new ArchiveExportException(reason, $"{stepName}に失敗しました: {ex.Message}", ex);
        }
    }
}
