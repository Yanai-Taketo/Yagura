using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace Yagura.Host.Administration.AdminAuthentication;

/// <summary>
/// AD グループ指定（名 <c>DOMAIN\Group</c> または SID <c>S-1-...</c>）を SID 集合へ解決する
/// （SEC-9。ADR-0010 決定 5・7・委任事項 8）。起動時に一度だけ実行し、結果を
/// <see cref="Yagura.Web.Administration.WindowsGroupAuthorizationOptions"/> として DI へ供給する。
/// </summary>
/// <remarks>
/// <para>
/// <b>名 → SID 変換</b>: <see cref="NTAccount.Translate(System.Type)"/> を用いる。これはローカル/ドメインの
/// アカウント DB（到達可能なら DC）へ問い合わせる Windows 専用 API のため、本クラスは
/// <see cref="SupportedOSPlatformAttribute"/> を付与する（呼び出し元 <see cref="Program"/> も Windows 専用）。
/// 既に SID 形式（<c>S-1-...</c>）で与えられた指定はそのまま <see cref="SecurityIdentifier"/> で正規化して受理し、
/// 変換問い合わせを発しない（DC 非依存で解決できる指定を優先できる）。
/// </para>
/// <para>
/// <b>解決できない指定は起動を止めず警告してスキップする</b>（security.md §1 の縮小側原則と同じ向き——
/// 認可を付与しない安全側）: 存在しないグループ名・タイプミス・DC 到達不能等で変換に失敗しても、
/// syslog 受信自体を止める理由にはならない。スキップされたグループの所属者は認可されないだけであり、
/// これは「開放側へ倒す」誤りにはならない。解決できた SID のみを集合に含める。
/// </para>
/// <para>
/// <b>照合は SID で一本化する</b>（<see cref="Yagura.Web.Administration.WindowsGroupAuthorizationOptions"/> の
/// remarks 参照）: トークン側のグループは <c>WindowsIdentity.Groups</c>（推移的・ネスト展開済み）として
/// SID でしか露出しないため、設定側も SID へ正規化して集合交差で判定する。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class WindowsSecurityGroupResolver
{
    /// <summary>
    /// グループ指定の生リスト（名/SID）を SID 文字列集合へ解決する。解決できない指定は
    /// <paramref name="logger"/> へ警告して除外する。
    /// </summary>
    /// <param name="specs">グループ名（<c>DOMAIN\Group</c>）または SID（<c>S-1-...</c>）の一覧。</param>
    /// <param name="configKey">警告メッセージに含める設定キー（例 <c>Viewer:Authentication:Windows:ViewerGroups</c>）。</param>
    /// <param name="logger">解決失敗の警告出力先。</param>
    public static IReadOnlySet<string> ResolveToSids(
        IReadOnlyList<string> specs,
        string configKey,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(specs);
        ArgumentNullException.ThrowIfNull(logger);

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (specs.Count == 0)
        {
            return result;
        }

        // 名解決を要する指定を集め、SID 形式は即時に受理する（DC 問い合わせを発しない）。
        var nameSpecs = new List<string>();
        foreach (var spec in specs)
        {
            if (string.IsNullOrWhiteSpace(spec))
            {
                continue;
            }

            var trimmed = spec.Trim();

            // 既に SID 形式（S-1-...）で与えられた指定は変換問い合わせを発さずに正規化して受理する。
            // ただし「S- で始まるが正しい SID ではない」場合はここで捨てず、名解決へフォールバックする——
            // 「S-」で始まる実在グループ名（ドメイン修飾なし）を SID 形式の誤りと誤判定して黙殺しないため
            // （SEC-9 レビュー指摘）。
            if (trimmed.StartsWith("S-", StringComparison.OrdinalIgnoreCase) && TryParseSidValue(trimmed, out var sidValue))
            {
                result.Add(sidValue);
                continue;
            }

            nameSpecs.Add(trimmed);
        }

        if (nameSpecs.Count > 0)
        {
            ResolveNamesWithBoundedTimeout(nameSpecs, configKey, result, logger);
        }

        return result;
    }

    /// <summary>
    /// 名（<c>DOMAIN\Group</c> 等）→ SID 変換の全体タイムアウト（レビュー指摘: 鈴木）。<c>NTAccount.Translate</c> は
    /// DC/LSA へ同期問い合わせを発し、DC 不達時は最悪数十秒ブロックしうる。この解決は起動シーケンス上
    /// リスナ bind より前に走るため、無制限だと <b>syslog 受信の開始をブロックする（ロス窓）</b>。全名を並列に
    /// 解決し、単一のこの締切で有界化する——締切超過分は解決不能として除外（認可を付与しない安全側）し、
    /// 起動（＝受信）を先へ進める。
    /// </summary>
    private static readonly TimeSpan NameResolutionTimeout = TimeSpan.FromSeconds(5);

    private static bool TryParseSidValue(string spec, out string sidValue)
    {
        try
        {
            sidValue = new SecurityIdentifier(spec).Value;
            return true;
        }
        catch (ArgumentException)
        {
            sidValue = string.Empty;
            return false;
        }
    }

    private static void ResolveNamesWithBoundedTimeout(
        IReadOnlyList<string> nameSpecs, string configKey, HashSet<string> result, ILogger logger)
    {
        // 各名を並列に解決し（Task.Run 内で例外を捕捉し null を返す——タスク自体は faulted にしない）、
        // 全体で NameResolutionTimeout により有界化する。締切超過のタスクは背後で完走/破棄されるが、
        // 起動はブロックしない。
        var tasks = nameSpecs
            .Select(name => Task.Run(() => TranslateNameOrNull(name)))
            .ToArray();

        var allCompleted = Task.WaitAll(tasks, NameResolutionTimeout);

        for (var i = 0; i < tasks.Length; i++)
        {
            var task = tasks[i];
            if (task.IsCompletedSuccessfully && task.Result is { } sid)
            {
                result.Add(sid);
                continue;
            }

            if (!task.IsCompleted)
            {
                logger.LogWarning(
                    "[sec9-group-unresolved] 設定 {ConfigKey} のグループ名 {Spec} の SID 解決が {Seconds} 秒以内に" +
                    "完了しませんでした（DC 到達不能・応答遅延の可能性）。このグループはマッピングから除外して起動を" +
                    "続行します（syslog 受信をブロックしないため——所属者は認可されません）。SID 形式（S-1-...）での" +
                    "指定は DC 問い合わせを発さず即時に解決できます。",
                    configKey,
                    nameSpecs[i],
                    (int)NameResolutionTimeout.TotalSeconds);
                continue;
            }

            // 完了したが解決できなかった（存在しない名・変換不能）。
            logger.LogWarning(
                "[sec9-group-unresolved] 設定 {ConfigKey} のグループ名 {Spec} を SID へ解決できませんでした" +
                "（存在しない・タイプミス等）。このグループはマッピングから除外します（所属者は認可されません）。" +
                "名が正しいか、または SID 形式（S-1-...）での指定を検討してください。",
                configKey,
                nameSpecs[i]);
        }

        _ = allCompleted; // 全完了フラグは個別判定で代替（未使用の明示）。
    }

    /// <summary>名を SID 文字列へ変換する。解決できない場合（存在しない・DC 不達・変換不能）は <see langword="null"/>。</summary>
    private static string? TranslateNameOrNull(string name)
    {
        try
        {
            var account = new NTAccount(name);
            return ((SecurityIdentifier)account.Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return null;
        }
    }
}
