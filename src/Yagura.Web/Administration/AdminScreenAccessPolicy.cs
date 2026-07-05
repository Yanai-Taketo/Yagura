namespace Yagura.Web.Administration;

/// <summary>
/// 管理画面の描画可否判定（M8-4。Issue #71）。
/// </summary>
/// <remarks>
/// <para>
/// <b>なぜルーティング層のガードに加えて circuit 層の判定が要るか</b>: 管理画面は Razor
/// Components のページであり、HTTP の直接到達（初回表示・全ページ読み込み）は
/// <c>ListenerPortGuardMiddleware</c>（エンドポイントの Admin メタデータ + 実ローカルポート
/// 判定）が守る。しかし閲覧リスナ経由で確立済みの circuit 上での対話的ナビゲーション
/// （Blazor の対話的ルーターは同一アセンブリ内の全 <c>@page</c> ルートを解決でき、HTTP 要求を
/// 発生させない）はルーティング層に現れない——security.md §1 L-5 が明示した覆域の限界そのもの。
/// この経路を、circuit 確立時に束縛したリスナ帰属（<c>YaguraCircuitContext.IsAdminListener</c>）の
/// 検査で塞ぐ。
/// </para>
/// <para>
/// <b>fail-closed</b>: 帰属を判定できない場合（<paramref name="circuitIsAdminListener"/> が
/// <see langword="null"/>）は許可しない。設定がどう壊れていても・実装の取得経路が失陥しても、
/// 管理画面が閲覧側へ開く方向には倒れない（configuration.md §1 の縮小側原則と同じ向き）。
/// </para>
/// </remarks>
public static class AdminScreenAccessPolicy
{
    /// <summary>
    /// 描画可否を判定する。
    /// </summary>
    /// <param name="requestLocalPort">
    /// HTTP 要求文脈（静的 SSR / prerender）の実ローカルポート。対話的描画では
    /// <see langword="null"/>（HttpContext は対話的描画で使えない——公式ドキュメントの制約）。
    /// </param>
    /// <param name="circuitIsAdminListener">
    /// circuit のリスナ帰属（<c>YaguraCircuitContext.IsAdminListener</c>。circuit 未確立・
    /// 判定不能は <see langword="null"/>）。
    /// </param>
    /// <param name="adminPort">管理リスナの実ポート。</param>
    public static AdminScreenAccess Decide(int? requestLocalPort, bool? circuitIsAdminListener, int adminPort)
    {
        // HTTP 要求文脈がある（prerender / 静的 SSR）なら、接続の実ローカルポートが唯一の真実
        // （クライアントが偽装できない値——ListenerPortGuardMiddleware と同じ判定根拠）。
        if (requestLocalPort is int port)
        {
            return port == adminPort ? AdminScreenAccess.Allowed : AdminScreenAccess.Denied;
        }

        // 対話的描画では circuit 確立時に束縛した帰属で判定する。
        return circuitIsAdminListener switch
        {
            true => AdminScreenAccess.Allowed,
            false => AdminScreenAccess.Denied,
            null => AdminScreenAccess.Undetermined,
        };
    }
}

/// <summary>管理画面の描画可否。</summary>
public enum AdminScreenAccess
{
    /// <summary>
    /// 帰属を判定できない（circuit の帰属取得が失陥している等）。描画しない（fail-closed）が、
    /// 拒否の監査記録は行わない——閲覧側からの到達と断定できないため（誤検知の証跡を作らない）。
    /// </summary>
    Undetermined,

    /// <summary>管理リスナ帰属が確認できた。描画する。</summary>
    Allowed,

    /// <summary>
    /// 閲覧側からの到達。描画せず、閲覧リスナに到達した管理系要求の拒否として監査記録
    /// （security.md §1 L-3b と同じ事象種別）の対象とする。
    /// </summary>
    Denied,
}
