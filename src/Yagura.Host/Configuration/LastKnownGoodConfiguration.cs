using System.Globalization;

namespace Yagura.Host.Configuration;

/// <summary>
/// 直近に読み取りと検証を通った設定ファイルの写し（configuration.md §1）。
/// </summary>
/// <remarks>
/// <para>
/// <b>目的</b>: 設定ファイルを読み取れずに起動失敗したとき、利用者が戻せる先を製品側に持たせる。
/// 保存経路（<see cref="YaguraConfigurationWriter"/>）は原子的置換のみで世代を残さないため、
/// これが無いと「直前のファイルに戻してください」という復旧案内が成立しない。
/// </para>
/// <para>
/// <b>自動適用はしない</b>（§1）。理由は「稼働中の構成とディスク上のファイルが食い違うから」では
/// ない——§3 の再読み込みや §2 の DPAPI 縮小など、食い違いを許容している決定は本文書に複数ある。
/// 自動適用が採れないのは、<b>写しの内容が現在の意図より開放側でありうる</b>ためである。公開範囲を
/// 狭める編集をした利用者がタイポで壊した場合、写しに入っているのは編集前のより開放側の値であり、
/// それを黙って適用することは「セキュリティ項目は不正値のとき開放側へ落とさない」という §1 の
/// 不変条件に反する。復元は利用者が内容を確認したうえで行う。
/// </para>
/// <para>
/// <b>1 世代のみ・旧世代を残さない</b>（§1）。手編集で平文の資格情報が書かれた場合（§2 で受理すると
/// 決めている）、写しにもそれが含まれる。世代を溜めると、ウィザードで暗号化表現へ移行した後も
/// 平文が無期限に残り続ける。置き場所はデータルート直下であり、ACL はデータルートのもの
/// （サービスアカウント + Administrators のみ。ADR-0004 決定 5）を継承する。
/// </para>
/// <para>
/// <b>更新契機は「起動時」と「再読み込みの成功時」の両方</b>（§1）。起動時のみにすると、再読み込みで
/// 適用済みの変更が写しに入らず、復元したときに黙って巻き戻る——本規定が防ごうとしている
/// 「意図しない設定で動く」事故を、復旧手段自体が起こすことになる。
/// </para>
/// </remarks>
public static class LastKnownGoodConfiguration
{
    /// <summary>写しのファイル名。設定ファイル名に接尾辞を付けた固定名（1 世代のみ）。</summary>
    public const string FileName = YaguraConfigurationLoader.ConfigurationFileName + ".last-good";

    /// <summary>写しのフルパスを返す（存在するとは限らない）。</summary>
    public static string GetPath(string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);

        return Path.Combine(dataRoot, FileName);
    }

    /// <summary>
    /// 現在の設定ファイルを写しとして保存する。読み取りと検証を通った直後にのみ呼ぶこと
    /// （壊れたファイルを写しにしてしまうと復旧元として役に立たない）。
    /// </summary>
    /// <remarks>
    /// <b>失敗しても呼び出し側を巻き込まない</b>。写しは復旧の利便のためのものであり、これが作れない
    /// ことで起動や再読み込みを止めるのは本末転倒である（§1 の「受信を止めない」優先）。失敗は
    /// <paramref name="onFailure"/> で通知し、処理は続行する。
    /// </remarks>
    public static void Save(string dataRoot, Action<Exception>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);

        var source = Path.Combine(dataRoot, YaguraConfigurationLoader.ConfigurationFileName);

        // 設定ファイルが無い状態（ゼロ設定ファーストラン）では写す対象が無い。既定値のみで
        // 動いている状態に戻すのに写しは要らないため、何もしない。
        if (!File.Exists(source))
        {
            return;
        }

        var destination = GetPath(dataRoot);
        var temporary = destination + ".tmp";

        try
        {
            // 一時ファイル経由で置換する（写しの書き込み中に電源断が起きても、
            // 半分だけ書かれた写しを復旧元として掴ませない）。
            File.Copy(source, temporary, overwrite: true);

            if (File.Exists(destination))
            {
                File.Replace(temporary, destination, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, destination);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onFailure?.Invoke(ex);

            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception cleanupFailure) when (cleanupFailure is IOException or UnauthorizedAccessException)
            {
                // 後始末の失敗は握りつぶす（本処理の失敗は既に通知済み）。
            }
        }
    }

    /// <summary>
    /// 復旧案内に載せる文言を組み立てる。写しが無い場合はウィザードでの再生成のみを案内する。
    /// </summary>
    /// <remarks>
    /// <b>日時を必ず併記する</b>（§1）。「いつ時点の構成に戻るのか」が分からないまま復元させると、
    /// 復元したのに設定が違うという、起動しないことより気づきにくい事故になる。
    /// </remarks>
    public static string BuildRecoveryGuidance(string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);

        var path = GetPath(dataRoot);

        if (!File.Exists(path))
        {
            return "復旧するには、設定ファイルを直すか、管理画面のウィザードで再生成してください"
                + "（利用可能な良好構成の写しはありません）。";
        }

        var savedAt = File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        return $"復旧するには、設定ファイルを直すか、良好構成の写し「{path}」（{savedAt} 時点）を"
            + $"「{YaguraConfigurationLoader.ConfigurationFileName}」へ戻してください。"
            + "写しはその時点の構成であり、以後に加えた変更は含まれません。内容を確認してから戻してください。";
    }
}
