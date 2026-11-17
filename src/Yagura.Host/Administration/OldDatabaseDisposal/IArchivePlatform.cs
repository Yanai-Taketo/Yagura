namespace Yagura.Host.Administration.OldDatabaseDisposal;

/// <summary>保管先ボリュームの種別。</summary>
public enum ArchiveDriveKind
{
    /// <summary>固定ディスク。</summary>
    Fixed,

    /// <summary>取り外せる媒体。</summary>
    Removable,

    /// <summary>光学ドライブ。</summary>
    Optical,

    /// <summary>ネットワークドライブ。</summary>
    Network,

    /// <summary>その他（RAM ディスク・不明）。</summary>
    Other,
}

/// <summary>保管先ボリュームの情報。</summary>
/// <param name="VolumeRoot">ボリュームのルート（同じボリュームかの判定に使う）。</param>
/// <param name="DriveKind">種別。</param>
/// <param name="FileSystemName">ファイルシステム名（取得できなければ <see langword="null"/>）。</param>
/// <param name="PreservesAcl">ACL を保持するファイルシステムか。</param>
/// <param name="AvailableFreeBytes">実行アカウントが使える空き容量。</param>
public sealed record ArchiveVolumeInfo(
    string VolumeRoot,
    ArchiveDriveKind DriveKind,
    string? FileSystemName,
    bool PreservesAcl,
    long AvailableFreeBytes);

/// <summary>作業用サブフォルダの排他作成の結果。</summary>
public enum WorkDirectoryCreation
{
    /// <summary>作成した（作成の時点から保護 ACL を持つ）。</summary>
    Created,

    /// <summary>同じ名前が既にある（上書きしない）。</summary>
    AlreadyExists,

    /// <summary>実行アカウントが親フォルダにフォルダを作れない。</summary>
    AccessDenied,

    /// <summary>その他の失敗。</summary>
    Failed,
}

/// <summary>
/// 保持している作業用サブフォルダ（<see cref="IArchivePlatform.HoldWorkDirectory"/>）。破棄で手放す。
/// </summary>
public interface IArchiveWorkDirectoryHold : IDisposable
{
    /// <summary>
    /// 保持しているフォルダが、保持した時点の場所から動いていないか。フォルダ自身や上位のフォルダが改名・差し替え
    /// されて、作業用サブフォルダのパスが別の場所を指すようになっていれば <see langword="false"/>。
    /// </summary>
    bool StillInPlace();

    /// <summary>保持しているフォルダの今の場所（取得できなければ <see langword="null"/>）。</summary>
    string? CurrentPath();
}

/// <summary>
/// 書き出して保管が依存するファイルシステムの性質と ACL の操作（database.md §6.3「自動テストで固定する
/// 不変条件」——判定は差し替え可能な口の後ろに置き、Windows 以外の開発機でも前提条件・完了判定・削除順序の
/// テストを回せるようにする）。
/// </summary>
public interface IArchivePlatform
{
    /// <summary>この環境で ACL の操作ができるか（できなければ書き出して保管は拒否する）。</summary>
    bool AclSupported { get; }

    /// <summary>実効実行アカウント名（付与コマンドに入れる）。</summary>
    string CurrentIdentityName { get; }

    /// <summary>ローカルの絶対パス（ドライブ文字から始まる形）か。</summary>
    bool IsLocalAbsolutePath(string path);

    /// <summary>ネットワーク上の場所を表す形（UNC・デバイスパス）か。</summary>
    bool IsNetworkOrDevicePath(string path);

    /// <summary>
    /// パスの実体（ジャンクション・シンボリックリンク・フォルダにマウントしたボリュームを辿った先）が属する
    /// ボリュームの情報（取得できなければ <see langword="null"/>）。実体がネットワーク上なら種別は
    /// <see cref="ArchiveDriveKind.Network"/>。
    /// </summary>
    ArchiveVolumeInfo? GetVolume(string path);

    /// <summary>再解析ポイントを解決した実体のパス（解決できない部分はそのまま）。</summary>
    string ResolveFinalPath(string path);

    /// <summary>
    /// 作業用サブフォルダを排他で作る。作成の時点から、親からの継承を切った保護 ACL（<c>SYSTEM</c>・
    /// <c>Administrators</c>・実行アカウント。いずれも配下へ継承）を持たせる。
    /// </summary>
    WorkDirectoryCreation CreateProtectedWorkDirectory(string path, out string? failureDetail);

    /// <summary>
    /// 作業用サブフォルダを処分の終了まで保持する: 改名・削除に要る共有を許さないハンドルを開いたままにし、
    /// その間フォルダを改名・削除・差し替えされないようにする。保持の時点で、フォルダが再解析ポイントでない
    /// ことを確かめる。保持できなければ <see langword="null"/>（<paramref name="failureDetail"/> に理由）。
    /// </summary>
    IArchiveWorkDirectoryHold? HoldWorkDirectory(string path, out string? failureDetail);

    /// <summary>
    /// フォルダの ACL が製品の作業中の構成（継承なし・<c>SYSTEM</c>・<c>Administrators</c>・実行アカウント）と
    /// 一致するか。クラッシュ後の掃除は、命名規則とこの一致の両方を満たすフォルダだけを削除する。
    /// </summary>
    bool HasWorkInProgressAcl(string directory);

    /// <summary>
    /// 確定時の最終の ACL を書き出したファイル・作業用サブフォルダに適用する（<c>SYSTEM</c>・<c>Administrators</c>
    /// のフルコントロールのみ、継承なし、実行アカウントの ACE を外し、所有者の暗黙の権限を抑止する）。
    /// 適用できなければ例外を投げる。
    /// </summary>
    void ApplyFinalAcl(string workDirectory, string archiveFile);

    /// <summary>最終の ACL が適用されていることを確かめる（確かめられなければ <see langword="false"/>）。</summary>
    bool HasFinalAcl(string workDirectory, string archiveFile);
}

/// <summary>ACL の操作ができない環境（Windows 以外）。書き出して保管は常に拒否される。</summary>
public sealed class UnsupportedArchivePlatform : IArchivePlatform
{
    /// <inheritdoc />
    public bool AclSupported => false;

    /// <inheritdoc />
    public string CurrentIdentityName => Environment.UserName;

    /// <inheritdoc />
    public bool IsLocalAbsolutePath(string path) => ArchivePathRules.IsDriveAbsolutePath(path);

    /// <inheritdoc />
    public bool IsNetworkOrDevicePath(string path) => ArchivePathRules.IsNetworkOrDevicePath(path);

    /// <inheritdoc />
    public ArchiveVolumeInfo? GetVolume(string path) => null;

    /// <inheritdoc />
    public string ResolveFinalPath(string path) => ArchivePathRules.ResolveFinalPath(path);

    /// <inheritdoc />
    public WorkDirectoryCreation CreateProtectedWorkDirectory(string path, out string? failureDetail)
    {
        failureDetail = "この環境では ACL を付けられません。";
        return WorkDirectoryCreation.Failed;
    }

    /// <inheritdoc />
    public IArchiveWorkDirectoryHold? HoldWorkDirectory(string path, out string? failureDetail)
    {
        failureDetail = "この環境ではフォルダを保持できません。";
        return null;
    }

    /// <inheritdoc />
    public bool HasWorkInProgressAcl(string directory) => false;

    /// <inheritdoc />
    public void ApplyFinalAcl(string workDirectory, string archiveFile) =>
        throw new PlatformNotSupportedException("この環境では ACL を付けられません。");

    /// <inheritdoc />
    public bool HasFinalAcl(string workDirectory, string archiveFile) => false;
}

/// <summary>保管先のパスの形の判定と、再解析ポイントの解決（OS によらない文字列・ファイルシステム操作）。</summary>
public static class ArchivePathRules
{
    /// <summary>ドライブ文字から始まる絶対パス（<c>D:\...</c>）か。</summary>
    public static bool IsDriveAbsolutePath(string path) =>
        path.Length >= 3
        && char.IsAsciiLetter(path[0])
        && path[1] == ':'
        && (path[2] == '\\' || path[2] == '/');

    /// <summary>UNC（<c>\\server\share</c>）・デバイスパス（<c>\\?\</c>・<c>\\.\</c>）の形か。</summary>
    public static bool IsNetworkOrDevicePath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    /// <summary>
    /// 先頭から順に各要素の再解析ポイント（シンボリックリンク・ジャンクション）を解決した実体のパスを返す。
    /// 解決できない要素（リンク以外の再解析ポイント等）はそのまま残す。
    /// </summary>
    public static string ResolveFinalPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var current = root;
        var segments = full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            try
            {
                var info = new DirectoryInfo(current);
                if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0
                    && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    current = Path.GetFullPath(target.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 解決できない要素は字面のまま比べる（判定は呼び出し側の安全側の比較に委ねる）。
            }
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    /// <summary><paramref name="path"/> が <paramref name="ancestor"/> と同じか、その配下か。</summary>
    public static bool IsSameOrUnder(string path, string ancestor)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedPath = Path.TrimEndingDirectorySeparator(path);
        var normalizedAncestor = Path.TrimEndingDirectorySeparator(ancestor);
        if (string.Equals(normalizedPath, normalizedAncestor, comparison))
        {
            return true;
        }

        // ルート（C:\ や /）は末尾の区切りが残るため、そのまま前方一致で比べる。
        var prefix = Path.EndsInDirectorySeparator(normalizedAncestor)
            ? normalizedAncestor
            : normalizedAncestor + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(prefix, comparison);
    }
}
