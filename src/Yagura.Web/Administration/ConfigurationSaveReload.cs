using Yagura.Abstractions.Administration;
using Yagura.Web.Components.Common;

namespace Yagura.Web.Administration;

/// <summary>
/// 管理画面が設定ファイルを変更する保存をしたあと、設定の再読み込みを実行して、反映に再起動が要る
/// 変更を管理ホームの再起動待ちカードへ計上する共通手続き（configuration.md §3）。
/// </summary>
/// <remarks>
/// <para>
/// 再起動待ちカードのデータ源は再読み込みの実行時にしか更新されない。保存だけで終えると、
/// 再起動するまで効かない変更がカードに載らず、利用者は「保存したのに効かない」理由に気づけない。
/// 呼び出し側は、保存で設定ファイルが変わったとき（変更キーが 1 件以上）だけ呼ぶ。
/// </para>
/// <para>
/// 静的メソッドとして提供する理由は <see cref="CertificateAdminScreenSupport"/> と同じ
/// （書き込み系サービスへの注入経路を閲覧側コンポーネントの参照分離検査の対象に広げない）。
/// </para>
/// </remarks>
public static class ConfigurationSaveReload
{
    /// <summary>
    /// 設定の再読み込みを実行する。拒否された場合（設定ファイルの検証失敗）は、保存自体は成立して
    /// いることと手動での再読み込みを促す警告を通知する（画面の保存結果の表示は上書きしない）。
    /// </summary>
    /// <param name="reloadRejectedFormat">拒否時の通知文言（{0} に拒否理由が入る）。</param>
    public static async Task<ConfigurationReloadResult> ReloadAfterSaveAsync(
        IConfigurationReloadService reloadService,
        IYaguraNotifier notifier,
        string? operatorAddress,
        string? operatorScheme,
        string? operatorPrincipal,
        string reloadRejectedFormat = UiText.AdminSaveReloadRejectedFormat)
    {
        ArgumentNullException.ThrowIfNull(reloadService);
        ArgumentNullException.ThrowIfNull(notifier);

        var reload = await reloadService.ReloadAsync(operatorAddress, operatorScheme, operatorPrincipal);
        if (reload.Rejected)
        {
            notifier.NotifyWarning(string.Format(reloadRejectedFormat, reload.RejectionReason));
        }

        return reload;
    }
}
