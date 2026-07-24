using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Yagura.Web.ForwarderKit;

/// <summary>
/// <see cref="IForwarderMsiStore"/> の実体（ADR-0020 決定 2・3。配置経路 (b)）。
/// ステージング → アトミックリネームの書き込み I/O を担い、判定（パターン一致・ハッシュ照合）は
/// <see cref="ForwarderMsiFilter"/>（純粋関数）に委譲する——<see cref="SystemForwarderMsiSource"/> と
/// 同じ設計。
/// </summary>
/// <remarks>
/// <para>
/// データルートの実パスは Host 側が知っており、Web 層は直接知らない——本クラスは
/// コンストラクタ引数でフォルダのフルパスを受け取り、Host の DI 登録（<c>Program.cs</c>）で
/// 実パスを注入する（<see cref="SystemForwarderMsiSource"/> と同じ参照構造）。
/// </para>
/// <para>
/// <b>ProductVersion の読み取りは注入可能</b>: 既定は <see cref="ForwarderMsiProductVersionReader"/>
/// （<c>msi.dll</c> P/Invoke。Windows 専用）。非 Windows 環境（CI のユニットテスト等）では
/// 読み取り不能 = アップロード拒否（<see cref="ForwarderMsiStageError.ProductVersionUnreadable"/>）に
/// 倒れる。テストは読み取り関数を差し替えて版判定・命名の分岐を固定する。
/// </para>
/// <para>
/// <b>ステージングは配置フォルダ内</b>（ADR-0020 決定 3）: <c>%TEMP%</c> 等の広い ACL の領域を
/// 経由しない。ステージング名（<see cref="ForwarderMsiUploadConstraints.StagingFileNamePrefix"/>）は
/// 検出パターンに一致しないため <see cref="IForwarderMsiSource.Lookup"/> から不可視。
/// 同一ボリューム内の <see cref="File.Move(string, string, bool)"/> はアトミックな置換になる。
/// </para>
/// </remarks>
public sealed partial class SystemForwarderMsiStore : IForwarderMsiStore
{
    private readonly Func<string, string?> _productVersionReader;
    private readonly object _gate = new();
    private PendingStaging? _pending;
    private bool _stagingInProgress;

    public SystemForwarderMsiStore(string folderPath, Func<string, string?>? productVersionReader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        FolderPath = folderPath;
        _productVersionReader = productVersionReader ?? DefaultProductVersionReader;
    }

    /// <inheritdoc/>
    public string FolderPath { get; }

    /// <inheritdoc/>
    public ForwarderMsiWriteAccess CheckWriteAccess()
    {
        if (!Directory.Exists(FolderPath))
        {
            return new ForwarderMsiWriteAccess(false, "placement folder does not exist");
        }

        var probePath = Path.Combine(
            FolderPath,
            ForwarderMsiUploadConstraints.WriteProbeFileNamePrefix + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            // 実書き込みプローブ（ADR-0020 委任 2）: プローブ名は検出パターン非一致であり、
            // 作成 → 即削除で痕跡を残さない。削除まで成功して初めて「開放」と判定する
            // （作成のみ可・削除不可という部分的な ACE では配置フロー全体が成立しないため）。
            using (var stream = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            File.Delete(probePath);
            return new ForwarderMsiWriteAccess(true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            TryDeleteQuietly(probePath);
            return new ForwarderMsiWriteAccess(false, ex.GetType().Name);
        }
    }

    /// <inheritdoc/>
    public int CleanupStagingFiles()
    {
        lock (_gate)
        {
            if (_stagingInProgress)
            {
                // 進行中のステージング本体は消さない（掃除は開始前・起動時のみ）。
                return 0;
            }

            _pending = null;
            return CleanupStagingFilesCore();
        }
    }

    /// <inheritdoc/>
    public async Task<ForwarderMsiStageResult> StageAsync(
        ForwarderMsiArchitecture architecture,
        Stream content,
        long? declaredLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        // --- 事前拒否（本文を読む前）: Content-Length 申告のサイズ上限超過（ADR-0020 決定 3） ---
        if (declaredLength is > ForwarderMsiUploadConstraints.MaxUploadBytes)
        {
            return ForwarderMsiStageResult.Failed(ForwarderMsiStageError.DeclaredLengthExceedsLimit);
        }

        // --- 事前拒否: 空き容量（「書き込み完了後も 1006 の警告閾値を下回らない」。申告なしは
        //     サイズ上限基準——ADR-0020 決定 3・委任 4） ---
        var requiredBytes = declaredLength ?? ForwarderMsiUploadConstraints.MaxUploadBytes;
        if (!HasSufficientFreeSpace(requiredBytes, out var freeSpaceReason))
        {
            return freeSpaceReason;
        }

        // --- 単一飛行（プロセス全体。ADR-0020 決定 3） ---
        lock (_gate)
        {
            if (_stagingInProgress)
            {
                return ForwarderMsiStageResult.Failed(ForwarderMsiStageError.AnotherUploadInProgress);
            }

            _stagingInProgress = true;
            // 新規アップロード開始時の孤児掃除（前回の保留・中断の残骸。ADR-0020 決定 3）。
            _pending = null;
            CleanupStagingFilesCore();
        }

        var stagingPath = Path.Combine(
            FolderPath,
            ForwarderMsiUploadConstraints.StagingFileNamePrefix + Guid.NewGuid().ToString("N") + ".msi");

        try
        {
            // --- ステージング書き込み（累積カウントでの上限打ち切りを伴う） ---
            var writeResult = await WriteStagingAsync(stagingPath, content, cancellationToken).ConfigureAwait(false);
            if (writeResult is not null)
            {
                TryDeleteQuietly(stagingPath);
                return writeResult;
            }

            // --- 検証: ProductVersion（クライアント申告のファイル名は信用しない——版の根拠は
            //     ProductVersion のみ。読み取り失敗は拒否。ADR-0020 決定 3） ---
            var productVersion = _productVersionReader(stagingPath);
            if (string.IsNullOrWhiteSpace(productVersion))
            {
                TryDeleteQuietly(stagingPath);
                return ForwarderMsiStageResult.Failed(ForwarderMsiStageError.ProductVersionUnreadable);
            }

            // --- 検証: 正式ファイル名の構成（検出パターンに一致する名前を Yagura が生成する） ---
            if (!TryBuildFinalFileName(productVersion, architecture, out var finalFileName))
            {
                TryDeleteQuietly(stagingPath);
                return ForwarderMsiStageResult.Failed(ForwarderMsiStageError.ProductVersionInvalid);
            }

            // --- SHA256・公式ハッシュ照合（配置時点の早期フィードバック。ADR-0008 設計条件 9） ---
            var (sha256, length) = ComputeSha256AndLength(stagingPath);
            var officialHashMatch = ForwarderMsiFilter.MatchesOfficialHash(
                sha256, ForwarderMsiConstraints.GetOfficialSha256(architecture));
            var versionMismatch = !ForwarderMsiFilter.MatchesVerifiedVersion(
                productVersion, ForwarderKitConstraints.VerifiedFluentBitVersion);

            // --- 既存ファイルとの関係（単一化。置換確認の材料） ---
            var existing = ListExistingFiles(architecture);
            if (existing.Count > 1)
            {
                TryDeleteQuietly(stagingPath);
                return ForwarderMsiStageResult.Failed(ForwarderMsiStageError.MultipleExistingFiles);
            }

            string? existingFileName = null;
            string? existingSha256 = null;
            if (existing.Count == 1)
            {
                existingFileName = Path.GetFileName(existing[0]);
                (existingSha256, _) = ComputeSha256AndLength(existing[0]);
            }

            var token = Guid.NewGuid().ToString("N");
            lock (_gate)
            {
                _pending = new PendingStaging(
                    token, stagingPath, architecture, finalFileName, productVersion!, sha256, length,
                    officialHashMatch, versionMismatch, existingFileName, existingSha256);
            }

            return new ForwarderMsiStageResult(
                Success: true,
                StagingToken: token,
                FinalFileName: finalFileName,
                ProductVersion: productVersion,
                Sha256: sha256,
                Length: length,
                OfficialHashMatch: officialHashMatch,
                VersionMismatch: versionMismatch,
                ExistingFileName: existingFileName,
                ExistingSha256: existingSha256);
        }
        catch (OperationCanceledException)
        {
            TryDeleteQuietly(stagingPath);
            return ForwarderMsiStageResult.Failed(ForwarderMsiStageError.Cancelled);
        }
        finally
        {
            lock (_gate)
            {
                _stagingInProgress = false;
            }
        }
    }

    /// <inheritdoc/>
    public ForwarderMsiCommitResult Commit(string stagingToken, bool versionMismatchAcknowledged, bool replaceAcknowledged)
    {
        lock (_gate)
        {
            var pending = _pending;
            if (pending is null || !string.Equals(pending.Token, stagingToken, StringComparison.Ordinal) ||
                !File.Exists(pending.StagingPath))
            {
                return ForwarderMsiCommitResult.Failed(ForwarderMsiCommitError.UnknownStagingToken);
            }

            // --- 二段階確認の強制（サーバ側の最終防御。ADR-0020 決定 3） ---
            if (pending.OfficialHashMatch != OfficialHashMatchResult.Match && !versionMismatchAcknowledged)
            {
                return ForwarderMsiCommitResult.Failed(ForwarderMsiCommitError.VersionMismatchNotAcknowledged);
            }

            // --- 既存ファイルとの関係の再検証（ステージング時の表示内容と実状態の一致——TOCTOU ガード） ---
            var existing = ListExistingFiles(pending.Architecture);
            string? replacedSha256 = null;
            if (pending.ExistingFileName is null)
            {
                if (existing.Count != 0)
                {
                    return ForwarderMsiCommitResult.Failed(ForwarderMsiCommitError.FolderStateChanged);
                }
            }
            else
            {
                if (existing.Count != 1 ||
                    !string.Equals(Path.GetFileName(existing[0]), pending.ExistingFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return ForwarderMsiCommitResult.Failed(ForwarderMsiCommitError.FolderStateChanged);
                }

                var (currentSha256, _) = ComputeSha256AndLength(existing[0]);
                if (!string.Equals(currentSha256, pending.ExistingSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return ForwarderMsiCommitResult.Failed(ForwarderMsiCommitError.FolderStateChanged);
                }

                if (!replaceAcknowledged)
                {
                    return ForwarderMsiCommitResult.Failed(ForwarderMsiCommitError.ReplaceNotAcknowledged);
                }

                replacedSha256 = pending.ExistingSha256;
            }

            var finalPath = Path.Combine(FolderPath, pending.FinalFileName);
            try
            {
                // アトミックリネーム（同一ボリューム。overwrite = 同名置換も原子的）。
                File.Move(pending.StagingPath, finalPath, overwrite: true);

                // 単一化: 正式ファイル以外の同一アーキ MSI（別名の旧版）を除去する。
                foreach (var other in ListExistingFiles(pending.Architecture))
                {
                    if (!string.Equals(other, finalPath, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(other);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                TryDeleteQuietly(pending.StagingPath);
                _pending = null;
                return ForwarderMsiCommitResult.Failed(ForwarderMsiCommitError.WriteFailed);
            }

            _pending = null;
            return new ForwarderMsiCommitResult(
                Success: true,
                FinalFileName: pending.FinalFileName,
                ProductVersion: pending.ProductVersion,
                Sha256: pending.Sha256,
                Length: pending.Length,
                OfficialHashMatch: pending.OfficialHashMatch,
                VersionMismatch: pending.VersionMismatch,
                VersionMismatchAcknowledged: versionMismatchAcknowledged,
                ReplacedSha256: replacedSha256);
        }
    }

    /// <inheritdoc/>
    public ForwarderMsiDiscardResult Discard(string stagingToken)
    {
        lock (_gate)
        {
            var pending = _pending;
            if (pending is null || !string.Equals(pending.Token, stagingToken, StringComparison.Ordinal))
            {
                return new ForwarderMsiDiscardResult(false);
            }

            TryDeleteQuietly(pending.StagingPath);
            _pending = null;
            return new ForwarderMsiDiscardResult(true, pending.Sha256, pending.ProductVersion);
        }
    }

    /// <inheritdoc/>
    public ForwarderMsiDeleteResult Delete(ForwarderMsiArchitecture architecture, string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);

        lock (_gate)
        {
            var existing = ListExistingFiles(architecture);
            if (existing.Count == 0)
            {
                return new ForwarderMsiDeleteResult(false, ForwarderMsiDeleteError.NotFound);
            }

            if (existing.Count > 1)
            {
                return new ForwarderMsiDeleteResult(false, ForwarderMsiDeleteError.MultipleExistingFiles);
            }

            var filePath = existing[0];
            var fileName = Path.GetFileName(filePath);
            var (actualSha256, _) = ComputeSha256AndLength(filePath);
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new ForwarderMsiDeleteResult(false, ForwarderMsiDeleteError.Sha256Mismatch);
            }

            try
            {
                File.Delete(filePath);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return new ForwarderMsiDeleteResult(false, ForwarderMsiDeleteError.WriteFailed);
            }

            return new ForwarderMsiDeleteResult(true, DeletedFileName: fileName, DeletedSha256: actualSha256);
        }
    }

    private static string? DefaultProductVersionReader(string filePath) =>
        OperatingSystem.IsWindows() ? ForwarderMsiProductVersionReader.TryRead(filePath) : null;

    private async Task<ForwarderMsiStageResult?> WriteStagingAsync(
        string stagingPath, Stream content, CancellationToken cancellationToken)
    {
        FileStream stagingStream;
        try
        {
            stagingStream = new FileStream(
                stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // 書き込み経路が未開放（ACE 未付与）の典型経路。UI は CheckWriteAccess で事前案内する
            // が、レース（撤去直後等）はここで安全側に倒れる。
            _ = ex;
            return ForwarderMsiStageResult.Failed(ForwarderMsiStageError.WriteFailed);
        }

        await using (stagingStream.ConfigureAwait(false))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > ForwarderMsiUploadConstraints.MaxUploadBytes)
                {
                    // 申告なし・虚偽申告のストリーミング打ち切り（ADR-0020 決定 3）。
                    return ForwarderMsiStageResult.Failed(ForwarderMsiStageError.StreamExceedsLimit);
                }

                await stagingStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    private bool HasSufficientFreeSpace(long requiredBytes, out ForwarderMsiStageResult failure)
    {
        failure = ForwarderMsiStageResult.Failed(ForwarderMsiStageError.InsufficientDiskSpace);
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(FolderPath));
            if (string.IsNullOrEmpty(root))
            {
                return true; // ルートを特定できない場合は判定不能——受信系の監視（1006）に委ねる。
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return true;
            }

            return drive.AvailableFreeSpace - requiredBytes >= ForwarderMsiUploadConstraints.FreeSpaceFloorBytes;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return true; // 取得不能は安全側で「判定しない」（MonitoredVolumeInfo と同じ判断）。
        }
    }

    private static bool TryBuildFinalFileName(
        string productVersion, ForwarderMsiArchitecture architecture, out string finalFileName)
    {
        finalFileName = string.Empty;
        // ProductVersion 由来の文字列からファイル名を構成する——パス区切り・パターン破壊文字を
        // 拒否する（クライアント申告値ではないが、MSI 内のメタデータも信用の対象外——安全側）。
        if (!ProductVersionShapeRegex().IsMatch(productVersion))
        {
            return false;
        }

        var suffix = architecture == ForwarderMsiArchitecture.WinArm64 ? "winarm64" : "win64";
        var candidate = $"fluent-bit-{productVersion}-{suffix}.msi";
        if (!ForwarderMsiFilter.IsCandidateFileName(candidate, architecture))
        {
            return false;
        }

        finalFileName = candidate;
        return true;
    }

    private List<string> ListExistingFiles(ForwarderMsiArchitecture architecture)
    {
        if (!Directory.Exists(FolderPath))
        {
            return [];
        }

        return Directory.EnumerateFiles(FolderPath)
            .Where(path => ForwarderMsiFilter.IsCandidateFileName(Path.GetFileName(path), architecture))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private int CleanupStagingFilesCore()
    {
        if (!Directory.Exists(FolderPath))
        {
            return 0;
        }

        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(
                     FolderPath, ForwarderMsiUploadConstraints.StagingFileSearchPattern))
        {
            if (TryDeleteQuietly(path))
            {
                removed++;
            }
        }

        return removed;
    }

    private static bool TryDeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // 掃除は失敗しても本処理を止めない（次回の掃除・ACE 再付与時に再試行される）。
        }

        return false;
    }

    private static (string Sha256, long Length) ComputeSha256AndLength(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hashBytes = SHA256.HashData(stream);
        return (Convert.ToHexStringLower(hashBytes), stream.Length);
    }

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$")]
    private static partial Regex ProductVersionShapeRegex();

    private sealed record PendingStaging(
        string Token,
        string StagingPath,
        ForwarderMsiArchitecture Architecture,
        string FinalFileName,
        string ProductVersion,
        string Sha256,
        long Length,
        OfficialHashMatchResult OfficialHashMatch,
        bool VersionMismatch,
        string? ExistingFileName,
        string? ExistingSha256);
}
