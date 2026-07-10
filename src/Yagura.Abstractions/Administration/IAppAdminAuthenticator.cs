namespace Yagura.Abstractions.Administration;

/// <summary>
/// アプリ独自 ID/パスワード認証（ADR-0010 決定 3）のログイン試行検証契約。
/// </summary>
/// <remarks>
/// <para>
/// <b>配置</b>: ログイン POST エンドポイント（<c>Yagura.Web</c> の管理系ルート）が、ロックアウト・
/// パスワードハッシュの実体（設定ファイル・DB 接続を管轄する <c>Yagura.Host</c>）へ到達するための
/// 契約。<see cref="ISetupWizardService"/> と同じ「Web は契約のみ参照し、Host が結線する」
/// パターン（architecture.md §1.1）。
/// </para>
/// <para>
/// <b>書き込み系サービス</b>: ログイン試行はロックアウト状態を変化させる副作用を持つため
/// <see cref="IYaguraWriteService"/> を実装する（security.md §1 L-5 の参照分離検査の対象——
/// 閲覧リスナ側コンポーネントから参照してはならない）。
/// </para>
/// </remarks>
public interface IAppAdminAuthenticator : IYaguraWriteService
{
    /// <summary>ログイン試行を検証する（成功/失敗いずれもユーザー列挙耐性を伴う）。</summary>
    Task<AppAuthenticationOutcome> TryAuthenticateAsync(
        string username, string password, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IAppAdminAuthenticator.TryAuthenticateAsync"/> の結果種別。</summary>
public enum AppAuthenticationResult
{
    /// <summary>認証成功。</summary>
    Success,

    /// <summary>資格情報が誤り（またはユーザー名が存在しない——利用者応答上は区別しない）。</summary>
    InvalidCredentials,

    /// <summary>既にロックアウト中。</summary>
    LockedOut,

    /// <summary>今回の失敗でロックアウト閾値に達し、新規にロックアウトされた。</summary>
    LockedOutNow,
}

/// <param name="Result">結果種別。</param>
/// <param name="Username">試行されたユーザー名（監査記録用。security.md §4.3「試行されたユーザー名を保持する」）。</param>
/// <param name="LockoutUntilUtc"><see cref="AppAuthenticationResult.LockedOut"/>/<see cref="AppAuthenticationResult.LockedOutNow"/> の場合の解除時刻。</param>
public sealed record AppAuthenticationOutcome(AppAuthenticationResult Result, string Username, DateTimeOffset? LockoutUntilUtc);
