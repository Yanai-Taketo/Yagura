using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Yagura.Host.Administration.OldDatabaseDisposal;
using Yagura.TestSupport;

namespace Yagura.Host.Tests.Administration.OldDatabaseDisposal;

/// <summary>
/// <see cref="WindowsArchivePlatform"/> の ACL の作成・検査・確定（security.md §5.3）。Windows でのみ実行する
/// （非 Windows では skip として明示する）。
/// </summary>
/// <remarks>
/// テストの実行アカウントは通常 Administrators の一員であり、確定後も Administrators の ACE で読める。そのため
/// 「実行アカウントの ACE が無い」「継承を切っている」「SYSTEM・Administrators のフルコントロール」を DACL の
/// 内容で確かめる。仮想サービスアカウントからのアクセス試行が拒否されることの実機確認は DB-12 ④に残る。
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsArchivePlatformTests : IDisposable
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private readonly TestTempDirectory _temp = new("archive-acl");

    public WindowsArchivePlatformTests()
    {
        // TestTempDirectory はディレクトリ自体を作らない。作業用フォルダは CreateDirectoryW で親フォルダの直下に
        // 作るため、親が無いと ERROR_PATH_NOT_FOUND で失敗する。
        Directory.CreateDirectory(_temp.Path);
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows() && Directory.Exists(_temp.Path))
        {
            // 確定後の ACL では親フォルダごとの削除に所有者の権限が要る場合があるため、継承を戻してから消す。
            foreach (var directory in Directory.EnumerateDirectories(_temp.Path, "*", SearchOption.AllDirectories).Reverse())
            {
                TryRestoreInheritance(directory);
            }
        }

        _temp.Dispose();
    }

    [SkippableFact]
    public void CreateProtectedWorkDirectory_IsExclusive_AndProtectedFromTheParentAtCreation()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows の ACL が必要");
        var platform = new WindowsArchivePlatform();
        var work = Path.Combine(_temp.Path, "yagura-archive-20260601T000000Z");

        Assert.Equal(WorkDirectoryCreation.Created, platform.CreateProtectedWorkDirectory(work, out _));
        Assert.Equal(WorkDirectoryCreation.AlreadyExists, platform.CreateProtectedWorkDirectory(work, out _));

        var security = new DirectoryInfo(work).GetAccessControl(AccessControlSections.Access);
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.All(rules, rule => Assert.False(rule.IsInherited));
        Assert.Equal(
            new HashSet<SecurityIdentifier> { LocalSystem, Administrators, CurrentUser() },
            rules.Select(rule => (SecurityIdentifier)rule.IdentityReference).ToHashSet());
        Assert.True(platform.HasWorkInProgressAcl(work));

        // 作業中に作られるファイルも保護 ACL を継承し、親フォルダの ACE を受けない。
        var file = Path.Combine(work, "export-1.tmp");
        File.WriteAllText(file, "x");
        var fileRules = new FileInfo(file).GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(rule => (SecurityIdentifier)rule.IdentityReference)
            .ToHashSet();
        Assert.Subset(new HashSet<SecurityIdentifier> { LocalSystem, Administrators, CurrentUser() }, fileRules);
    }

    [SkippableFact]
    public void ApplyFinalAcl_LeavesOnlySystemAndAdministrators_AndRemovesTheServiceAccount()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows の ACL が必要");
        Skip.If(CurrentUser() == LocalSystem, "実行アカウントが SYSTEM だと「実行アカウントの ACE が無い」を区別できない");
        var platform = new WindowsArchivePlatform();
        var work = Path.Combine(_temp.Path, "yagura-archive-20260601T000001Z");
        Assert.Equal(WorkDirectoryCreation.Created, platform.CreateProtectedWorkDirectory(work, out _));
        var archive = Path.Combine(work, "yagura-archive-20260601T000001Z.db");
        File.WriteAllText(archive, "archive");

        platform.ApplyFinalAcl(work, archive);

        Assert.True(platform.HasFinalAcl(work, archive));
        Assert.False(platform.HasWorkInProgressAcl(work));
        foreach (var read in new Func<FileSystemSecurity>[]
                 {
                     () => new FileInfo(archive).GetAccessControl(AccessControlSections.Access),
                     () => new DirectoryInfo(work).GetAccessControl(AccessControlSections.Access),
                 })
        {
            FileSystemSecurity security;
            try
            {
                security = read();
            }
            catch (UnauthorizedAccessException)
            {
                // 昇格していない実行では Administrators の ACE が効かず ACL を読めない——それ自体が、実行アカウントに
                // 到達の経路が無いことの観測になる。
                continue;
            }

            Assert.True(security.AreAccessRulesProtected);
            var sids = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Select(rule => (SecurityIdentifier)rule.IdentityReference)
                .ToList();
            Assert.DoesNotContain(CurrentUser(), sids);
            Assert.Contains(LocalSystem, sids);
            Assert.Contains(Administrators, sids);
        }
    }

    [SkippableFact]
    public void HasWorkInProgressAcl_FolderWithInheritedAcl_IsNotTheProducts()
    {
        // クラッシュ後の掃除は名前の一致だけを根拠にしない——同じ名前でも製品の作業中の ACL でなければ対象外。
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows の ACL が必要");
        var platform = new WindowsArchivePlatform();
        var lookalike = Directory.CreateDirectory(Path.Combine(_temp.Path, "yagura-archive-20260601T000002Z")).FullName;

        Assert.False(platform.HasWorkInProgressAcl(lookalike));
    }

    [SkippableFact]
    public void GetVolume_LocalTempFolder_IsAFixedDriveThatPreservesAcl()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows のドライブ情報が必要");

        var volume = new WindowsArchivePlatform().GetVolume(_temp.Path);

        Assert.NotNull(volume);
        Assert.Equal(ArchiveDriveKind.Fixed, volume.DriveKind);
        Assert.True(volume.PreservesAcl, volume.FileSystemName);
    }

    [SkippableFact]
    public void GetVolume_ThroughAJunction_IsTheTargetsVolume_AndResolvesToTheTarget()
    {
        // ボリュームは字面のドライブではなく実体で判定する（ジャンクション経由の取り外せる媒体等を見逃さない）。
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows のジャンクションが必要");
        var target = Directory.CreateDirectory(Path.Combine(_temp.Path, "real")).FullName;
        var link = Path.Combine(_temp.Path, "via-junction");
        Skip.IfNot(TryCreateJunction(link, target), "ジャンクションを作れない環境");
        try
        {
            var platform = new WindowsArchivePlatform();
            Assert.Equal(platform.ResolveFinalPath(target), platform.ResolveFinalPath(link), ignoreCase: true);

            var viaLink = platform.GetVolume(link);
            var direct = platform.GetVolume(target);
            Assert.NotNull(viaLink);
            Assert.NotNull(direct);
            Assert.Equal(direct.VolumeRoot, viaLink.VolumeRoot, ignoreCase: true);
            Assert.Equal(direct.DriveKind, viaLink.DriveKind);
            Assert.Equal(direct.PreservesAcl, viaLink.PreservesAcl);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [SkippableFact]
    public void HoldWorkDirectory_PreventsRenamingTheHeldFolder_ButNotWritingIntoIt()
    {
        // 親フォルダで子の削除権を持つ利用者でも、保持中の作業用サブフォルダを改名してジャンクションに差し替えられない。
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows の共有モードが必要");
        var platform = new WindowsArchivePlatform();
        var work = Path.Combine(_temp.Path, "yagura-archive-20260601T000003Z");
        Assert.Equal(WorkDirectoryCreation.Created, platform.CreateProtectedWorkDirectory(work, out _));

        using (var hold = platform.HoldWorkDirectory(work, out var failure))
        {
            Assert.True(hold is not null, failure);
            Assert.True(hold.StillInPlace());

            var moved = Record.Exception(() => Directory.Move(work, work + "-renamed"));
            Assert.True(moved is IOException or UnauthorizedAccessException, moved?.ToString() ?? "保持中のフォルダを改名できた。");
            Assert.True(Directory.Exists(work));

            File.WriteAllText(Path.Combine(work, "export-1.tmp"), "x");
            Assert.True(hold.StillInPlace());
        }

        // 手放した後は改名できる（保持が処分の終了を越えて残らない）。
        Directory.Move(work, work + "-renamed");
    }

    [SkippableFact]
    public void HoldWorkDirectory_RejectsAJunction()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows のジャンクションが必要");
        var target = Directory.CreateDirectory(Path.Combine(_temp.Path, "elsewhere")).FullName;
        var link = Path.Combine(_temp.Path, "yagura-archive-20260601T000004Z");
        Skip.IfNot(TryCreateJunction(link, target), "ジャンクションを作れない環境");
        try
        {
            Assert.Null(new WindowsArchivePlatform().HoldWorkDirectory(link, out var failure));
            Assert.NotNull(failure);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    private static bool TryCreateJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start);
        if (process is null || !process.WaitForExit(10_000))
        {
            return false;
        }

        return process.ExitCode == 0 && Directory.Exists(link);
    }

    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!;
    }

    private static void TryRestoreInheritance(string directory)
    {
        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                CurrentUser(),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var fileSecurity = new FileSecurity();
                fileSecurity.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                new FileInfo(file).SetAccessControl(fileSecurity);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            // 掃除はベストエフォート（一時フォルダに残っても他のテストに影響しない）。
        }
    }
}
