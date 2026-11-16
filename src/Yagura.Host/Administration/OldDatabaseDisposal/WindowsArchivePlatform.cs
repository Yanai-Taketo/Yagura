using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

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
/// <b>ACL を保持するファイルシステムの判定</b>: ファイルシステム名が NTFS または ReFS のときに限る
/// （判定方法の一次情報による確認は DB-12 ③——未実施）。
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
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);
            var kind = drive.DriveType switch
            {
                DriveType.Fixed => ArchiveDriveKind.Fixed,
                DriveType.Removable => ArchiveDriveKind.Removable,
                DriveType.CDRom => ArchiveDriveKind.Optical,
                DriveType.Network => ArchiveDriveKind.Network,
                _ => ArchiveDriveKind.Other,
            };

            if (!drive.IsReady)
            {
                return new ArchiveVolumeInfo(root, kind, null, false, 0);
            }

            var format = drive.DriveFormat;
            var preservesAcl = string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase)
                || string.Equals(format, "ReFS", StringComparison.OrdinalIgnoreCase);
            return new ArchiveVolumeInfo(root, kind, format, preservesAcl, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public string ResolveFinalPath(string path) => ArchivePathRules.ResolveFinalPath(path);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string lpPathName, ref SecurityAttributes lpSecurityAttributes);
}
