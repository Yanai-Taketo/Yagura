using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Yagura.Host.Administration.ForwarderKitUpload;

/// <summary>
/// フォワーダ MSI 配置フォルダの ACL を読み取り、実効実行アカウントの書き込み ACE の有無を
/// 判定する（ADR-0020 決定 2・委任 7。Issue #283）。
/// </summary>
/// <remarks>
/// <para>
/// <b>なぜ実書き込みプローブではなく ACL の読み取りか</b>: 周期監視
/// （<see cref="Yagura.Host.Observability.ActiveNotification.ActiveNotificationMonitor"/>。1 分間隔）から
/// 毎周期呼ばれるため、実書き込みプローブ（ファイル作成 → 削除）では SACL（OS 監査。
/// ADR-0020 決定 4 が「開くなら見張る」として推奨する構成）の監査ログを毎分汚染してしまう。
/// ACL の読み取りは SACL の書き込み監査に載らず、副作用ゼロで判定できる。
/// 管理画面の書き込み可否検出（<see cref="Yagura.Web.ForwarderKit.IForwarderMsiStore.CheckWriteAccess"/>——
/// 利用者操作の頻度でしか呼ばれない）が実書き込みプローブを使うのとは目的・頻度が異なる。
/// </para>
/// <para>
/// <b>判定は近似</b>: 実効実行アカウント（ユーザー SID + トークンのグループ SID）に対する
/// Allow ACE のうち書き込み系権利（<see cref="FileSystemRights.CreateFiles"/> /
/// <see cref="FileSystemRights.WriteData"/>）を含むものの有無を見る（Deny ACE が同権利を含めば
/// 不可側に倒す）。条件付き ACE 等の完全な実効権限評価（AuthzAccessCheck 相当）は行わない——
/// 検出対象は利用者ガイドが案内する <c>icacls ... /grant "アカウント:(OI)(CI)(M)"</c> の
/// 定型付与であり、この近似で十分（判定不能・想定外の形は <see langword="null"/> = 沈黙側に倒す。
/// 検出仕様と切替レールの実挙動の突合は ADR-0020 決定 5 lab ④）。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ForwarderMsiFolderAclInspector
{
    /// <summary>
    /// 実効実行アカウントが配置フォルダへ書き込めるか。
    /// <see langword="true"/> = 書き込み ACE あり（開放中）/ <see langword="false"/> = なし（既定）/
    /// <see langword="null"/> = 判定不能（フォルダ不在・ACL 読み取り失敗——安全側で通知しない）。
    /// </summary>
    internal static bool? IsWritableByCurrentIdentity(string folderPath)
    {
        try
        {
            if (!Directory.Exists(folderPath))
            {
                return null;
            }

            using var identity = WindowsIdentity.GetCurrent();
            var identitySids = new HashSet<SecurityIdentifier>();
            if (identity.User is not null)
            {
                identitySids.Add(identity.User);
            }

            if (identity.Groups is not null)
            {
                foreach (var group in identity.Groups)
                {
                    if (group is SecurityIdentifier sid)
                    {
                        identitySids.Add(sid);
                    }
                }
            }

            var security = new DirectoryInfo(folderPath).GetAccessControl(AccessControlSections.Access);
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));

            const FileSystemRights writeRights = FileSystemRights.CreateFiles | FileSystemRights.WriteData;
            var allowed = false;
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.IdentityReference is not SecurityIdentifier ruleSid || !identitySids.Contains(ruleSid))
                {
                    continue;
                }

                if ((rule.FileSystemRights & writeRights) == 0)
                {
                    continue;
                }

                if (rule.AccessControlType == AccessControlType.Deny)
                {
                    return false;
                }

                allowed = true;
            }

            return allowed;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SystemException)
        {
            return null;
        }
    }
}
