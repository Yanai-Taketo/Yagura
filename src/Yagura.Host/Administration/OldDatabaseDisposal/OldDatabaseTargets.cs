using Yagura.Abstractions.Administration;
using Yagura.Host.Configuration;
using Yagura.Host.Firewall;
using Yagura.Host.Observability;
using Yagura.Host.Observability.Auditing;

namespace Yagura.Host.Administration.OldDatabaseDisposal;

/// <summary>
/// 処分の対象 3 ファイルのパス（database.md §6.3「処分の対象」）。SQLite provider の書き込み先・
/// 移行元と同じ導出（データルート + <c>Storage:SqliteFileName</c>）で決める。利用者の入力は使わない。
/// </summary>
public sealed record OldDatabaseTargetPaths(string DataRoot, string BodyPath)
{
    /// <summary>WAL ファイル。</summary>
    public string WalPath => BodyPath + "-wal";

    /// <summary>共有メモリファイル。</summary>
    public string ShmPath => BodyPath + "-shm";

    /// <summary>削除の順序（<c>-wal</c>・<c>-shm</c> を先に、本体を最後に）。</summary>
    public IReadOnlyList<string> DeletionOrder => [WalPath, ShmPath, BodyPath];

    /// <summary>データルートと設定上のファイル名から導く。</summary>
    public static OldDatabaseTargetPaths Derive(string dataRoot, string sqliteFileName) =>
        new(Path.GetFullPath(dataRoot), Path.GetFullPath(Path.Combine(dataRoot, sqliteFileName)));
}

/// <summary>対象の安全検査（①〜③）の結果。</summary>
/// <param name="Kind">対象の種類。</param>
/// <param name="RejectionReason"><see cref="OldDatabaseTargetKind.Unsafe"/> の理由。</param>
/// <param name="ExistingFiles">存在する対象（削除の順序）。</param>
public sealed record OldDatabaseTargetInspection(
    OldDatabaseTargetKind Kind,
    string? RejectionReason,
    IReadOnlyList<OldDatabaseFile> ExistingFiles)
{
    /// <summary>対象が 1 つも無いか。</summary>
    public bool NoTargets => ExistingFiles.Count == 0;

    /// <summary>対象の合計サイズ（読めなかったものは 0 として数える）。</summary>
    public long TotalSizeBytes => ExistingFiles.Sum(file => file.SizeBytes ?? 0);
}

/// <summary>
/// 対象の安全検査（database.md §6.3）。設定の誤記で別のファイルを指していても消さないための検査であり、
/// 旧ファイルをデータベースとしては開かない（先頭 16 バイトを読むだけ）。
/// </summary>
public static class OldDatabaseTargetInspector
{
    /// <summary>SQLite のデータベースファイルの先頭の識別子（"SQLite format 3\0"）。</summary>
    private static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();

    /// <summary>
    /// 製品がデータルート直下に置く他のファイルの名前。0 バイト・識別子の壊れた本体を削除の対象にする前に、
    /// これらの名前でないことを確かめる。
    /// </summary>
    public static IReadOnlySet<string> ReservedFileNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        YaguraConfigurationLoader.ConfigurationFileName,
        LastKnownGoodConfiguration.FileName,
        LastAppliedConfigurationSnapshotStore.FileName,
        FirewallStartupInspector.InstallationRecordFileName,
        FirewallStartupInspector.TranscribedMarkerFileName,
        ServiceAccountStartupInspector.InstallationRecordFileName,
        ServiceAccountStartupInspector.TranscribedMarkerFileName,
        ServiceAccountStartupInspector.LastAccountFileName,
        FileAuditRecorder.LegacyFileName,
        MetadataStore.FileName,
        LogMigrationService.CheckpointFileName,
        PromotionSwitchBoundaryStore.FileName,
    };

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>3 ファイルの存在と安全検査 ①〜③ を判定する。</summary>
    public static OldDatabaseTargetInspection Inspect(OldDatabaseTargetPaths paths)
    {
        var existing = new List<OldDatabaseFile>();
        foreach (var path in paths.DeletionOrder)
        {
            if (!PathExists(path))
            {
                continue;
            }

            existing.Add(new OldDatabaseFile(path, TryGetLength(path)));

            // ① 正規化後のパスでデータルート直下にあること。
            var parent = Path.GetDirectoryName(Path.GetFullPath(path));
            if (parent is null || !string.Equals(
                    Path.TrimEndingDirectorySeparator(parent),
                    Path.TrimEndingDirectorySeparator(paths.DataRoot),
                    PathComparison))
            {
                return Unsafe(existing, $"データフォルダの直下にありません: {path}");
            }

            // ② 通常のファイルであること（ディレクトリ・再解析ポイントではない）。
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Unsafe(existing, $"属性を読み取れません: {path}（{ex.Message}）");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                return Unsafe(existing, $"ファイルではなくフォルダです: {path}");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return Unsafe(existing, $"シンボリックリンク等の再解析ポイントです: {path}");
            }
        }

        if (existing.Count == 0)
        {
            return new OldDatabaseTargetInspection(OldDatabaseTargetKind.SidecarFilesOnly, null, existing);
        }

        var body = existing.FirstOrDefault(file => string.Equals(file.Path, paths.BodyPath, PathComparison));
        if (body is null)
        {
            // 本体が無く -wal・-shm だけが残る: 削除の対象にする（退避の対象にはしない）。
            return new OldDatabaseTargetInspection(OldDatabaseTargetKind.SidecarFilesOnly, null, existing);
        }

        var bodyName = Path.GetFileName(paths.BodyPath);
        if (body.SizeBytes == 0)
        {
            return ReservedFileNames.Contains(bodyName)
                ? Unsafe(existing, $"製品が使う別のファイルの名前です: {bodyName}")
                : new OldDatabaseTargetInspection(OldDatabaseTargetKind.EmptyBody, null, existing);
        }

        // ③ 本体が存在する場合は、先頭の識別子で SQLite のデータベースファイルであることを確かめる。
        bool? hasHeader;
        string? readFailure = null;
        try
        {
            hasHeader = HasSqliteHeader(paths.BodyPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            hasHeader = null;
            readFailure = ex.Message;
        }

        if (hasHeader is null)
        {
            return Unsafe(existing, $"先頭を読み取れません: {paths.BodyPath}（{readFailure}）");
        }

        if (hasHeader == false)
        {
            return ReservedFileNames.Contains(bodyName)
                ? Unsafe(existing, $"製品が使う別のファイルの名前です: {bodyName}")
                : new OldDatabaseTargetInspection(OldDatabaseTargetKind.UnverifiableBody, null, existing);
        }

        return new OldDatabaseTargetInspection(OldDatabaseTargetKind.SqliteDatabase, null, existing);
    }

    /// <summary>
    /// 存在の判定。シンボリックリンクは参照先が無くても「ある」とみなす（②で拒否するため）。
    /// </summary>
    private static bool PathExists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static long? TryGetLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool HasSqliteHeader(string path)
    {
        // SQLite と同時に開いても邪魔をしない共有モードで読む（内容の検査であり、ロックを取らない）。
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            FileOptions.None);
        var buffer = new byte[SqliteHeader.Length];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return read == buffer.Length && buffer.AsSpan().SequenceEqual(SqliteHeader);
    }

    private static OldDatabaseTargetInspection Unsafe(List<OldDatabaseFile> existing, string reason) =>
        new(OldDatabaseTargetKind.Unsafe, reason, existing);
}
