using System.Net;

namespace Yagura.Abstractions.Administration;

/// <summary>
/// アプリ独自 ID/パスワード認証（ADR-0011 決定 2 の三層防御）のログイン試行検証契約。
/// </summary>
/// <remarks>
/// <para>
/// <b>配置</b>: ログイン POST エンドポイント（<c>Yagura.Web</c> の管理系ルート）が、三層防御（IP
/// レート制限・グローバルトークンバケット・アカウント単位バックオフ）・パスワードハッシュの実体
/// （設定ファイル・DB 接続を管轄する <c>Yagura.Host</c>）へ到達するための契約。
/// <see cref="ISetupWizardService"/> と同じ「Web は契約のみ参照し、Host が結線する」パターン
/// （architecture.md §1.1）。
/// </para>
/// <para>
/// <b>書き込み系サービス</b>: ログイン試行はバックオフ・レート制限の状態を変化させる副作用を持つため
/// <see cref="IYaguraWriteService"/> を実装する（security.md §1 L-5 の参照分離検査の対象——
/// 閲覧リスナ側コンポーネントから参照してはならない）。
/// </para>
/// <para>
/// <b>ADR-0011 決定 3・6 の応答統一</b>: <see cref="AppAuthenticationResult"/> は実在アカウントの
/// バックオフ待機・非実在ユーザー名・IP レート制限拒否・グローバルトークンバケット涸渇のいずれでも
/// 同一の <see cref="AppAuthenticationResult.Denied"/> を返す——呼び出し元は
/// <see cref="AppAuthenticationOutcome.DenialLayer"/>（監査記録専用。利用者応答には出さない）で
/// 原因を区別してはならない。利用者に見せてよいのは
/// <see cref="AppAuthenticationOutcome.WaitSeconds"/> の数値（カウントダウン表示用）のみ。
/// </para>
/// </remarks>
public interface IAppAdminAuthenticator : IYaguraWriteService
{
    /// <summary>ログイン試行を検証する（成功/失敗いずれもユーザー列挙耐性を伴う）。</summary>
    Task<AppAuthenticationOutcome> TryAuthenticateAsync(
        string username, string password, AdminAuthAttemptContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// ログイン試行の接続コンテキスト（ADR-0011 決定 4）。三層防御の loopback 除外・IP レート制限の
/// キーに使う。<c>isLoopback</c> の判定は
/// <see cref="Yagura.Web.Administration.AdminAuthenticationExtensions.IsLoopbackAdminConnection"/>
/// と同一の判定点（管理リスナの loopback 束縛ポートへの到達）を単一の正とする。
/// </summary>
/// <param name="RemoteAddress">接続元 IP アドレス（取得できない場合は <see langword="null"/>）。</param>
/// <param name="IsLoopback">管理リスナの loopback 束縛ポート経由の接続かどうか。</param>
public sealed record AdminAuthAttemptContext(IPAddress? RemoteAddress, bool IsLoopback);

/// <summary><see cref="IAppAdminAuthenticator.TryAuthenticateAsync"/> の結果種別。</summary>
public enum AppAuthenticationResult
{
    /// <summary>認証成功。</summary>
    Success,

    /// <summary>
    /// 資格情報が誤り（またはユーザー名が存在しない——利用者応答上は区別しない）。バックオフ猶予閾値
    /// 未満のため待機は発生していない。
    /// </summary>
    InvalidCredentials,

    /// <summary>
    /// 拒否（ADR-0011 決定 3・6）: 実在アカウントのバックオフ待機・IP レート制限・グローバル
    /// トークンバケット涸渇のいずれか。原因は <see cref="AppAuthenticationOutcome.DenialLayer"/>
    /// （監査記録専用）でのみ区別する——利用者応答・UI 文言では区別しない。
    /// </summary>
    Denied,
}

/// <summary>
/// <see cref="AppAuthenticationOutcome.DenialLayer"/> の値（ADR-0011 決定 9。監査記録専用——
/// 利用者応答に出してはならない）。
/// </summary>
public enum AdminAuthDenialLayer
{
    /// <summary>拒否なし（<see cref="AppAuthenticationResult.Success"/>/<see cref="AppAuthenticationResult.InvalidCredentials"/>）。</summary>
    None,

    /// <summary>アカウント単位バックオフによる待機（決定 3）。</summary>
    Backoff,

    /// <summary>送信元 IP 単位のレート制限による拒否（決定 2 評価順序 ①）。</summary>
    IpRateLimit,

    /// <summary>プロセス全体のグローバルトークンバケット涸渇による拒否（決定 2 評価順序 ②）。</summary>
    GlobalBucket,
}

/// <param name="Result">結果種別。</param>
/// <param name="Username">試行されたユーザー名（監査記録用。security.md §4.3「試行されたユーザー名を保持する」）。</param>
/// <param name="WaitSeconds">
/// 利用者への統一待機表示（ADR-0011 決定 6）に使う秒数。<see cref="AppAuthenticationResult.Denied"/>
/// の場合のみ意味を持つ——バックオフ層は「今回の試行に適用された遅延」、IP レート制限/グローバル
/// トークンバケット層は「有限の Retry-After」を表す（両者は数値としては区別され得るが、これは
/// ADR-0011 決定 3 が明示的に受け入れるタイミング非対称であり、応答種別・UI 文言自体は統一する）。
/// </param>
/// <param name="DenialLayer">拒否の主因層（監査記録専用。<see cref="IAppAdminAuthenticator"/> の remarks 参照）。</param>
/// <param name="BackoffCapReached">
/// <see cref="DenialLayer"/> が <see cref="AdminAuthDenialLayer.Backoff"/> の場合に、今回の試行が
/// cap（上限遅延）下で行われたかどうか（監査記録専用。ADR-0011 決定 9 のイベント ID 3006 の
/// 発火判定に使う——利用者応答には出さない）。
/// </param>
public sealed record AppAuthenticationOutcome(
    AppAuthenticationResult Result,
    string Username,
    int? WaitSeconds,
    AdminAuthDenialLayer DenialLayer,
    bool BackoffCapReached = false);
