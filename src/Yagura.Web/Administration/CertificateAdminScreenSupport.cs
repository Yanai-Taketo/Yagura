using Yagura.Abstractions.Administration;
using Yagura.Web.Components.Common;

namespace Yagura.Web.Administration;

/// <summary>
/// 証明書設定 3 画面（閲覧 HTTPS・TLS 受信・管理リモート HTTPS）の <c>@code</c> に共通する
/// 手続きの共有ヘルパ（Phase 5 重複解消）。表示（バッジ・注記）は
/// <see cref="YaguraCertificateSelectionPanel"/> が担い、本クラスは画面の状態遷移のみを担う。
/// </summary>
/// <remarks>
/// 静的メソッドとして提供する（コンポーネントにしない）——ここに書き込み系サービス
/// （<see cref="IConfigurationReloadService"/> 等）への注入経路を作ると、
/// <c>Yagura.Web.Components</c> 名前空間の閲覧側コンポーネントからの参照分離検査
/// （<c>ViewerComponentReferenceIsolationTests</c>）の対象が広がる。各画面が自身の
/// <c>@inject</c> で取得したインスタンスを引数として渡す形にとどめる。
/// </remarks>
public static class CertificateAdminScreenSupport
{
    /// <summary>
    /// 証明書ストアの列挙。例外を握り潰さず、失敗理由をそのまま呼び出し側へ返す
    /// （契約経由の呼び出しで想定外の失敗があれば、画面にそのまま出して切り分けに使えるようにする）。
    /// </summary>
    public static (IReadOnlyList<CertificateCandidate> Candidates, string? Error) LoadCertificates(
        ICertificateStoreReader certificateStoreReader)
    {
        try
        {
            return (certificateStoreReader.ListServerAuthCertificates(), null);
        }
        catch (Exception ex)
        {
            return ([], ex.Message);
        }
    }

    /// <summary>
    /// 選択中（または永続値の）拇印が列挙結果に存在しないか（証明書の削除・条件不適合への変化を
    /// 隠さない）。列挙自体が失敗している間は判定しない（誤警告を避ける）。
    /// </summary>
    public static bool IsSelectedThumbprintMissingFromList(
        string? certificateListError,
        string selectedThumbprint,
        IReadOnlyList<CertificateCandidate> candidates) =>
        certificateListError is null &&
        !string.IsNullOrEmpty(selectedThumbprint) &&
        !candidates.Any(c => string.Equals(c.Thumbprint, selectedThumbprint, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>ApplyAsync</c> 末尾の共通処理: 変更キー 0 件なら Info 通知のみ、それ以外は要約を組み立てて
    /// Ok 通知し、再読み込みを実行して再起動待ちカードへ計上する（Rejected なら Warning 通知）。
    /// </summary>
    /// <param name="onSaved">
    /// 保存要約の通知後・再読み込みの前に実行する画面固有の後処理（任意。変更ゼロの no-op 経路では
    /// 呼ばれない）。閲覧 HTTPS 画面の「有効化時のみ案内カードを出す」設定などに使う。
    /// </param>
    /// <returns>保存要約（no-op のときは null）。</returns>
    public static async Task<string?> ApplyChangeSummaryAsync(
        IReadOnlyList<string> changedKeys,
        string savedFormat,
        string savedNoChanges,
        string reloadRejectedFormat,
        IYaguraNotifier notifier,
        IConfigurationReloadService reloadService,
        string? operatorAddress,
        string? operatorScheme,
        string? operatorPrincipal,
        Action? onSaved = null)
    {
        if (changedKeys.Count == 0)
        {
            notifier.NotifyInfo(savedNoChanges);
            return null;
        }

        var summary = string.Format(savedFormat, string.Join("、", changedKeys));
        notifier.NotifyOk(summary);
        onSaved?.Invoke();

        var reload = await reloadService.ReloadAsync(operatorAddress, operatorScheme, operatorPrincipal);
        if (reload.Rejected)
        {
            notifier.NotifyWarning(string.Format(reloadRejectedFormat, reload.RejectionReason));
        }

        return summary;
    }
}
