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
    /// <param name="adminPorts">
    /// 管理リスナが実際に bind している全ポート（ADR-0010 Phase 2 決定 1。リモートバインド有効時は
    /// loopback 用ポートに加えリモート HTTPS 用ポートも含む）。
    /// </param>
    public static AdminScreenAccess Decide(int? requestLocalPort, bool? circuitIsAdminListener, IReadOnlyList<int> adminPorts)
    {
        // HTTP 要求文脈がある（prerender / 静的 SSR）なら、接続の実ローカルポートが唯一の真実
        // （クライアントが偽装できない値——ListenerPortGuardMiddleware と同じ判定根拠）。
        if (requestLocalPort is int port)
        {
            return adminPorts.Contains(port) ? AdminScreenAccess.Allowed : AdminScreenAccess.Denied;
        }

        // 対話的描画では circuit 確立時に束縛した帰属で判定する。
        return circuitIsAdminListener switch
        {
            true => AdminScreenAccess.Allowed,
            false => AdminScreenAccess.Denied,
            null => AdminScreenAccess.Undetermined,
        };
    }

    /// <summary>
    /// 認証の充足を判定する（ADR-0010 決定 1・2）。リスナ帰属（<see cref="Decide"/>）で
    /// <see cref="AdminScreenAccess.Allowed"/> になった後の第二段判定として使う——両者は独立
    /// （認証は「誰が到達できるか」を絞る機構であり、「どの経路が存在するか」を検証する
    /// リスナ帰属検査とは直交する。security.md §1 決定 7 の整理と同じ考え方）。
    /// </summary>
    /// <param name="authenticationRequired">
    /// loopback 認証 opt-in の実効値（<see cref="AdminAuthenticationRuntimeOptions.RequireAuthentication"/>）。
    /// <see langword="false"/> の場合、認証状態に関わらず常に充足する（既定は現状維持）。
    /// </param>
    /// <param name="isLoginRoute">
    /// ログイン画面自身への遷移かどうか。ログイン画面は認証充足判定の対象外
    /// （未認証で到達できなければならない——循環防止）。
    /// </param>
    /// <param name="isAuthorizedUser">
    /// 現在の circuit 認証状態が管理権限を満たすか
    /// （<c>AdminAuthenticationExtensions.IsWindowsAdministrator</c> または
    /// <c>IsAppAuthenticated</c>。呼び出し側が判定して渡す——本メソッドはクレーム判定の詳細を
    /// 知らない、純粋な組み立てに留める）。
    /// </param>
    public static bool IsAuthenticationSatisfied(bool authenticationRequired, bool isLoginRoute, bool isAuthorizedUser) =>
        !authenticationRequired || isLoginRoute || isAuthorizedUser;
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
