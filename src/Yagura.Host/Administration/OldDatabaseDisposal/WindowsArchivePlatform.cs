using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Yagura.Host.Administration.OldDatabaseDisposal;

/// <summary>
/// Windows の <see cref="IArchivePlatform"/>（security.md §5.3）。
/// </summary>
/// <remarks>
/// <para>
/// <b>作成の時点からの保護</b>: 作業用サブフォルダは <c>CreateDirectoryW</c> にセキュリティ記述子を渡して
/// 作る——既にあれば失敗する（排他）うえ、作成と同時に保護 ACL を持つため、付け替えまでの間に親フォルダの
/// 継承 ACL で読める窓ができない。SQLite が書き出しの過程で作る付随ファイルも、この ACL を継承する。
/// </para>
/// <para>
/// <b>所有者の暗黙の権限の抑止</b>: 作成した実行アカウントは所有者になり、DACL によらず ACL の読み書きができる。
/// 確定時に <c>OWNER RIGHTS</c>（S-1-3-4）の ACE を属性の読み取りだけで置き、所有者に暗黙の権限を与えない。
/// 所有者の移し替えは仮想サービスアカウントが通常持たない特権を要するため採らない。実機の <c>icacls</c> 出力と
/// 実行アカウントからのアクセス試行の拒否による確認は DB-12 ④（未実施）。
/// </para>
/// <para>
/// <b>ボリュームは実体で判定する</b>: 保管先のフォルダをハンドルで開き（再解析ポイントを辿る）、
/// <c>GetFinalPathNameByHandleW</c> で実体のパスを、<c>GetVolumeInformationByHandleW</c> でそのボリュームの
/// ファイルシステムを求める。字面のドライブ文字で判定すると、ジャンクション・シンボリックリンク・フォルダに
/// マウントしたボリュームを経由した取り外せる媒体・ACL を持たないファイルシステム・ネットワーク上の共有を
/// 見逃す。実体がドライブ文字で表せない（UNC・ボリューム GUID のパス）場合は受け付けない。
/// </para>
/// <para>
/// <b>ACL を保持するファイルシステムの判定</b>: ボリュームの属性 <c>FILE_PERSISTENT_ACLS</c> による
/// （<c>CreateDirectoryW</c> に渡したセキュリティ記述子は、この属性を持たないボリュームでは黙って無視される）。
/// 実機での確認は DB-12 ③——未実施。
/// </para>
/// <para>
/// <b>作業用サブフォルダの保持</b>: 作成した作業用サブフォルダを、削除の共有を許さないハンドル（データの読み取りの
/// 権限つき——共有モードはデータの読み書き・削除の権限を伴うハンドルにだけ効く）で処分の終了まで開いたままにする。
/// その間、親フォルダで子の削除権を持つ利用者でも、フォルダを改名してジャンクションに差し替えることはできない。
/// 上位のフォルダの改名で場所が変わったことは、ハンドルから求めた実体のパスの変化で検出する。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsArchivePlatform : IArchivePlatform
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorAlreadyExists = 183;

    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");

    private readonly SecurityIdentifier _currentIdentity;

    public WindowsArchivePlatform()
    {
        using var identity = WindowsIdentity.GetCurrent();
        _currentIdentity = identity.User ?? throw new InvalidOperationException("実行アカウントの SID を取得できません。");
        CurrentIdentityName = identity.Name;
    }

    /// <inheritdoc />
    public bool AclSupported => true;

    /// <inheritdoc />
    public string CurrentIdentityName { get; }

    /// <inheritdoc />
    public bool IsLocalAbsolutePath(string path) => ArchivePathRules.IsDriveAbsolutePath(path);

    /// <inheritdoc />
    public bool IsNetworkOrDevicePath(string path) => ArchivePathRules.IsNetworkOrDevicePath(path);

    /// <inheritdoc />
    public ArchiveVolumeInfo? GetVolume(string path)
    {
        try
        {
            using var handle = OpenForQuery(Path.GetFullPath(path));
            if (handle.IsInvalid || FinalPathOf(handle) is not { } finalPath)
            {
                return null;
            }

            if (ToUncPath(finalPath) is { } unc)
            {
                return new ArchiveVolumeInfo(unc, ArchiveDriveKind.Network, null, false, 0);
            }

            var local = StripLongPathPrefix(finalPath);
            if (!ArchivePathRules.IsDriveAbsolutePath(local))
            {
                return new ArchiveVolumeInfo(finalPath, ArchiveDriveKind.Other, null, false, 0);
            }

            var mountPoint = VolumePathOf(local);
            if (mountPoint is null)
            {
                return null;
            }

            var kind = GetDriveTypeW(mountPoint) switch
            {
                DriveFixed => ArchiveDriveKind.Fixed,
                DriveRemovable => ArchiveDriveKind.Removable,
                DriveCdRom => ArchiveDriveKind.Optical,
                DriveRemote => ArchiveDriveKind.Network,
                _ => ArchiveDriveKind.Other,
            };

            var fileSystemName = new char[MaxPathChars];
            if (!GetVolumeInformationByHandleW(handle, null, 0, out _, out _, out var flags, fileSystemName, (uint)fileSystemName.Length))
            {
                return null;
            }

            var free = GetDiskFreeSpaceExW(local, out var available, out _, out _) ? (long)Math.Min(available, long.MaxValue) : 0;
            return new ArchiveVolumeInfo(
                VolumeNameOf(mountPoint) ?? mountPoint,
                kind,
                TrimNull(fileSystemName),
                (flags & FilePersistentAcls) != 0,
                free);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public string ResolveFinalPath(string path)
    {
        try
        {
            using var handle = OpenForQuery(Path.GetFullPath(path));
            if (!handle.IsInvalid && FinalPathOf(handle) is { } finalPath)
            {
                return Path.TrimEndingDirectorySeparator(ToUncPath(finalPath) ?? StripLongPathPrefix(finalPath));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 開けない（まだ無い等）場所は要素ごとの解決に委ねる。
        }

        return ArchivePathRules.ResolveFinalPath(path);
    }

    /// <inheritdoc />
    public IArchiveWorkDirectoryHold? HoldWorkDirectory(string path, out string? failureDetail)
    {
        var handle = CreateFileW(
            path,
            FileListDirectory | FileReadAttributes | Synchronize,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            failureDetail = new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()).Message;
            handle.Dispose();
            return null;
        }

        if (!GetFileInformationByHandle(handle, out var information)
            || (information.FileAttributes & FileAttributeDirectory) == 0
            || (information.FileAttributes & FileAttributeReparsePoint) != 0
            || FinalPathOf(handle) is not { } finalPath)
        {
            failureDetail = "作業用のフォルダが通常のフォルダではありません（再解析ポイントに差し替えられた可能性があります）。";
            handle.Dispose();
            return null;
        }

        failureDetail = null;
        return new WorkDirectoryHold(handle, finalPath);
    }

    /// <inheritdoc />
    public WorkDirectoryCreation CreateProtectedWorkDirectory(string path, out string? failureDetail)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { LocalSystem, Administrators, _currentIdentity })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var handle = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = handle.AddrOfPinnedObject(),
                InheritHandle = 0,
            };

            if (CreateDirectoryW(path, ref attributes))
            {
                failureDetail = null;
                return WorkDirectoryCreation.Created;
            }

            var error = Marshal.GetLastPInvokeError();
            failureDetail = new System.ComponentModel.Win32Exception(error).Message;
            return error switch
            {
                ErrorAlreadyExists => WorkDirectoryCreation.AlreadyExists,
                ErrorAccessDenied => WorkDirectoryCreation.AccessDenied,
                _ => WorkDirectoryCreation.Failed,
            };
        }
        finally
        {
            handle.Free();
        }
    }

    /// <inheritdoc />
    public bool HasWorkInProgressAcl(string directory)
    {
        try
        {
            var security = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access);
            if (!security.AreAccessRulesProtected)
            {
                return false;
            }

            var expected = new HashSet<SecurityIdentifier> { LocalSystem, Administrators, _currentIdentity };
            var seen = new HashSet<SecurityIdentifier>();
            foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
            {
                if (rule.IsInherited
                    || rule.AccessControlType != AccessControlType.Allow
                    || rule.IdentityReference is not SecurityIdentifier sid
                    || !expected.Contains(sid)
                    || (rule.FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl)
                {
                    return false;
                }

                seen.Add(sid);
            }

            return seen.SetEquals(expected);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void ApplyFinalAcl(string workDirectory, string archiveFile)
    {
        // ファイルを先に絞る（フォルダを先に絞ると、実行アカウントがファイルへ辿り着けなくなる）。
        var fileSecurity = new FileSecurity();
        fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        fileSecurity.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, AccessControlType.Allow));
        fileSecurity.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        fileSecurity.AddAccessRule(new FileSystemAccessRule(OwnerRights, FileSystemRights.ReadAttributes, AccessControlType.Allow));
        new FileInfo(archiveFile).SetAccessControl(fileSecurity);

        var directorySecurity = new DirectorySecurity();
        directorySecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { LocalSystem, Administrators })
        {
            directorySecurity.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        directorySecurity.AddAccessRule(new FileSystemAccessRule(OwnerRights, FileSystemRights.ReadAttributes, AccessControlType.Allow));
        new DirectoryInfo(workDirectory).SetAccessControl(directorySecurity);
    }

    /// <inheritdoc />
    public bool HasFinalAcl(string workDirectory, string archiveFile)
    {
        // 最終の ACL の下では実行アカウントは ACL を読めない（所有者の暗黙の READ_CONTROL も抑止している）。
        // 読めないこと自体が、実行アカウントの到達経路が無いことの観測になる。読める場合（管理者として
        // 実行している等）は内容を突き合わせる。
        return IsFinal(() => new FileInfo(archiveFile).GetAccessControl(AccessControlSections.Access), inheritable: false)
            && IsFinal(() => new DirectoryInfo(workDirectory).GetAccessControl(AccessControlSections.Access), inheritable: true);
    }

    private bool IsFinal(Func<FileSystemSecurity> read, bool inheritable)
    {
        FileSystemSecurity security;
        try
        {
            security = read();
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return false;
        }

        if (!security.AreAccessRulesProtected)
        {
            return false;
        }

        var sawSystem = false;
        var sawAdministrators = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference is not SecurityIdentifier sid)
            {
                return false;
            }

            if (sid == _currentIdentity)
            {
                return false;
            }

            if (sid == OwnerRights)
            {
                if ((rule.FileSystemRights & ~(FileSystemRights.ReadAttributes | FileSystemRights.Synchronize)) != 0)
                {
                    return false;
                }

                continue;
            }

            if (sid != LocalSystem && sid != Administrators)
            {
                return false;
            }

            if ((rule.FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl)
            {
                return false;
            }

            if (inheritable && rule.InheritanceFlags != (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit))
            {
                return false;
            }

            sawSystem |= sid == LocalSystem;
            sawAdministrators |= sid == Administrators;
        }

        return sawSystem && sawAdministrators;
    }

    /// <summary>再解析ポイントを辿って開く（属性の読み取りだけ。共有はすべて許す）。</summary>
    private static SafeFileHandle OpenForQuery(string path) => CreateFileW(
        path,
        FileReadAttributes,
        FileShareRead | FileShareWrite | FileShareDelete,
        IntPtr.Zero,
        OpenExisting,
        FileFlagBackupSemantics,
        IntPtr.Zero);

    private static string? FinalPathOf(SafeFileHandle handle)
    {
        var buffer = new char[MaxPathChars];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, FileNameNormalized | VolumeNameDos);
        if (length == 0)
        {
            return null;
        }

        if (length >= buffer.Length)
        {
            buffer = new char[length + 1];
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, FileNameNormalized | VolumeNameDos);
            if (length == 0 || length >= buffer.Length)
            {
                return null;
            }
        }

        return new string(buffer, 0, (int)length);
    }

    private static string StripLongPathPrefix(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

    /// <summary>実体が UNC（<c>\\?\UNC\server\share</c>）なら <c>\\server\share</c> の形を返す。</summary>
    private static string? ToUncPath(string finalPath) =>
        finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + finalPath[8..]
        : finalPath.StartsWith(@"\\", StringComparison.Ordinal) && !finalPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? finalPath
        : null;

    private static string? VolumePathOf(string path)
    {
        var buffer = new char[MaxPathChars];
        return GetVolumePathNameW(path, buffer, (uint)buffer.Length) ? TrimNull(buffer) : null;
    }

    private static string? VolumeNameOf(string mountPoint)
    {
        var buffer = new char[MaxPathChars];
        return GetVolumeNameForVolumeMountPointW(mountPoint, buffer, (uint)buffer.Length) ? TrimNull(buffer) : null;
    }

    private static string TrimNull(char[] buffer)
    {
        var end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end);
    }

    /// <summary>作業用サブフォルダのハンドル（削除の共有を許さない）と、保持した時点の実体のパス。</summary>
    private sealed class WorkDirectoryHold(SafeFileHandle handle, string finalPathAtHold) : IArchiveWorkDirectoryHold
    {
        public bool StillInPlace() =>
            !handle.IsClosed
            && FinalPathOf(handle) is { } current
            && string.Equals(current, finalPathAtHold, StringComparison.OrdinalIgnoreCase);

        public string? CurrentPath() =>
            handle.IsClosed || FinalPathOf(handle) is not { } current
                ? null
                : ToUncPath(current) ?? StripLongPathPrefix(current);

        public void Dispose() => handle.Dispose();
    }

    private const int MaxPathChars = 32768;
    private const uint FileListDirectory = 0x0001;
    private const uint FileReadAttributes = 0x0080;
    private const uint Synchronize = 0x00100000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint FileShareDelete = 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint FileNameNormalized = 0x0;
    private const uint VolumeNameDos = 0x0;
    private const uint FilePersistentAcls = 0x00000008;
    private const uint DriveRemovable = 2;
    private const uint DriveFixed = 3;
    private const uint DriveRemote = 4;
    private const uint DriveCdRom = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string lpPathName, ref SecurityAttributes lpSecurityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(
        SafeFileHandle hFile,
        [Out] char[]? lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        [Out] char[] lpFileSystemNameBuffer,
        uint nFileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string lpszFileName, [Out] char[] lpszVolumePathName, uint cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string lpszVolumeMountPoint, [Out] char[] lpszVolumeName, uint cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string lpRootPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);
}
