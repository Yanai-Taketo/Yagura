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
/// 再読み込みは画面が保存したキーに限らず、設定ファイル全体を前回適用時点と比べて即時反映の口を
/// 持つ変更をすべて適用する。保存のみで残された変更（初期セットアップウィザードの保存のみ・手編集）が
/// ここで黙って効くと、利用者は受信ポートなどが変わったことに気づけないため、保存したキー以外の
/// 適用キーは警告として列挙する。
/// </para>
/// <para>
/// 静的メソッドとして提供する理由は <see cref="CertificateAdminScreenSupport"/> と同じ
/// （書き込み系サービスへの注入経路を閲覧側コンポーネントの参照分離検査の対象に広げない）。
/// </para>
/// </remarks>
public static class ConfigurationSaveReload
{
    /// <summary>
    /// 設定の再読み込みを実行する。保存は呼び出し前に成立しているため、画面の保存結果の表示は
    /// 上書きせず、次の場合に警告だけを通知する。
    /// <list type="bullet">
    /// <item>拒否された（設定ファイルの検証失敗）、または実行中に例外が出た——手動での再読み込みを促す</item>
    /// <item><paramref name="savedKeys"/> 以外のキーが即時反映された——反映されたキーを列挙する</item>
    /// </list>
    /// </summary>
    /// <param name="savedKeys">この保存で画面が書き換えたキー（これ以外の適用キーを警告に列挙する）。</param>
    /// <param name="reloadRejectedFormat">拒否時の通知文言（{0} に拒否理由が入る）。</param>
    /// <returns>再読み込みの結果。実行中に例外が出た場合は <see langword="null"/>。</returns>
    public static async Task<ConfigurationReloadResult?> ReloadAfterSaveAsync(
        IConfigurationReloadService reloadService,
        IYaguraNotifier notifier,
        IReadOnlyCollection<string> savedKeys,
        string? operatorAddress,
        string? operatorScheme,
        string? operatorPrincipal,
        string reloadRejectedFormat = UiText.AdminSaveReloadRejectedFormat)
    {
        ArgumentNullException.ThrowIfNull(reloadService);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(savedKeys);

        ConfigurationReloadResult reload;
        try
        {
            reload = await reloadService.ReloadAsync(operatorAddress, operatorScheme, operatorPrincipal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 設定ファイルの一時的なロック（IOException 等）や即時反映の失敗は拒否として返らない。
            // ボタン共通の受け皿（「操作を完了できませんでした」）まで伝えると保存まで失敗したように
            // 見えるため、ここで受けて、保存済みであることと手動での再読み込みを伝える。
            notifier.NotifyWarning(string.Format(UiText.AdminSaveReloadFailedFormat, ex.Message));
            return null;
        }

        if (reload.Rejected)
        {
            notifier.NotifyWarning(string.Format(reloadRejectedFormat, reload.RejectionReason));
            return reload;
        }

        var otherAppliedKeys = reload.AppliedKeys
            .Where(key => !savedKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (otherAppliedKeys.Length > 0)
        {
            notifier.NotifyWarning(string.Format(
                UiText.AdminSaveReloadAppliedOtherChangesFormat, string.Join("、", otherAppliedKeys)));
        }

        return reload;
    }
}
