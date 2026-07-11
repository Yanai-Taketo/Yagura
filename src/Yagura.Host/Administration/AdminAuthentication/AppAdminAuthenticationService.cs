using Microsoft.AspNetCore.Identity;
using Yagura.Abstractions.Administration;
using Yagura.Storage.Administration;

namespace Yagura.Host.Administration.AdminAuthentication;

/// <summary>
/// アプリ独自 ID/パスワード認証の実体（ADR-0010 決定 3。ADR-0011 決定 2〜7 の三層防御・
/// パスワード強度要件を実装する）。
/// </summary>
/// <remarks>
/// <para>
/// <b>パスワードハッシュ</b>: <c>Microsoft.AspNetCore.Identity.PasswordHasher&lt;TUser&gt;</c>
/// （PBKDF2 実装）は <c>Microsoft.Extensions.Identity.Core</c> アセンブリに属し、これは
/// <c>Microsoft.AspNetCore.App</c> 共有フレームワークに含まれる（Yagura.Web は Sdk.Razor から
/// 明示 <c>FrameworkReference</c> で参照済み）——追加の NuGet 依存なしに利用できることを
/// 実機ビルドで確認した。<c>TUser</c> はハッシュ形式に影響しないため、意味のあるプロパティを
/// 持たないマーカー型（<see cref="AdminAccountHashSubject"/>）を使う。
/// </para>
/// <para>
/// <b>三層防御の評価順序（ADR-0011 決定 2）</b>: ①<see cref="AdminAuthFailureDefense.CheckIpRateLimit"/>
/// → ②<see cref="AdminAuthFailureDefense.CheckGlobalBucket"/> → ③アカウント単位バックオフ
/// （<see cref="AdminAuthFailureDefense.GetBackoffDelay"/> + パスワード検証 +
/// <see cref="AdminAuthFailureDefense.RecordFailure"/>）の順に評価する。①②で拒否された試行は
/// パスワード検証まで到達せず、連続失敗回数 n も進めない（決定 2 の因果）。
/// </para>
/// <para>
/// <b>ユーザー列挙耐性（ADR-0011 決定 3）</b>: 存在しないユーザー名でも、実在するアカウントに対する
/// 誤パスワードと同じだけの処理時間・同じ応答（<see cref="AppAuthenticationResult.InvalidCredentials"/>）
/// を返す——存在しないユーザー名の場合も <see cref="DummyPasswordHash"/> に対してハッシュ検証を
/// 実行することで、検証コスト（PBKDF2 の反復計算）による所要時間の差を縮める。**非実在ユーザー名には
/// 個別のバックオフ状態を持たせない**（メモリ枯渇 DoS 回避。決定 3）——①②を通過した非実在名は
/// 遅延なしで即座に <see cref="AppAuthenticationResult.InvalidCredentials"/> を返す。応答種別・
/// 待機表示の統一（決定 3・6）は呼び出し元（ログイン HTTP ハンドラ）が
/// <see cref="AppAuthenticationOutcome.DenialLayer"/> を利用者応答へ出さないことで担保する。
/// </para>
/// <para>
/// <b>バックオフ（決定 3）</b>: 実在アカウントに対する連続失敗が閾値 k
/// （<see cref="AdminAuthenticationDefaults.BackoffThreshold"/>）を超えると、パスワード検証の
/// <b>前</b>に遅延を適用する——正しいパスワードであっても遅延を負う（「待てば必ず通る」の実装）。
/// cap に達しても拒否はしない。
/// </para>
/// <para>
/// <b>単一アカウントモデル（Phase 1）</b>: <see cref="SetAccountAsync"/> は既存アカウントの
/// パスワードを置き換える（アカウント追加ではなく「最初の管理者アカウント」の設定/変更）。
/// 複数アカウントの管理は Phase 1 のスコープ外（<see cref="IAdminAccountStore"/> の remarks 参照）。
/// パスワード強度要件（最小長・ブロックリスト突合。ADR-0011 決定 7）はここで検証する。
/// </para>
/// </remarks>
public sealed class AppAdminAuthenticationService : IAppAdminAuthenticator
{
    private static readonly PasswordHasher<AdminAccountHashSubject> Hasher = new();

    // ダミーハッシュ: 列挙耐性のため、アカウント不在時にも同等のハッシュ検証コストを払う
    // （固定パスワードのハッシュ値を起動時に 1 回計算して使い回す——実際の資格情報とは無関係）。
    private static readonly string DummyPasswordHash = Hasher.HashPassword(
        new AdminAccountHashSubject(), "yagura-enumeration-resistance-dummy-2026-07-10");

    private readonly IAdminAccountStore _store;
    private readonly AdminAuthFailureDefense _defense;
    private readonly TimeProvider _timeProvider;

    public AppAdminAuthenticationService(
        IAdminAccountStore store,
        AdminAuthFailureDefense? defense = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _defense = defense ?? new AdminAuthFailureDefense(_timeProvider);
    }

    /// <summary>ログイン試行を検証する（成功/失敗いずれも列挙耐性を伴う。ADR-0011 決定 2〜4）。</summary>
    public async Task<AppAuthenticationOutcome> TryAuthenticateAsync(
        string username, string password, AdminAuthAttemptContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(context);

        // 評価順序 ①IP レート制限（決定 2・4・5）。
        var ipDecision = _defense.CheckIpRateLimit(context.RemoteAddress, context.IsLoopback);
        if (!ipDecision.Allowed)
        {
            return new AppAuthenticationOutcome(
                AppAuthenticationResult.Denied, username, CeilingSeconds(ipDecision.RetryAfter), AdminAuthDenialLayer.IpRateLimit);
        }

        // 評価順序 ②グローバルトークンバケット（決定 2・4・5.1）。
        var bucketDecision = _defense.CheckGlobalBucket(context.IsLoopback, context.RemoteAddress);
        if (!bucketDecision.Allowed)
        {
            return new AppAuthenticationOutcome(
                AppAuthenticationResult.Denied, username, CeilingSeconds(bucketDecision.RetryAfter), AdminAuthDenialLayer.GlobalBucket);
        }

        var now = _timeProvider.GetUtcNow();
        var account = await _store.FindByUsernameAsync(username, cancellationToken).ConfigureAwait(false);

        if (account is null)
        {
            // 列挙耐性: 実在アカウントと同等のハッシュ検証コストを払ったうえで拒否する。
            // 非実在ユーザー名には個別のバックオフ状態を持たせない（決定 3）ため遅延はない。
            Hasher.VerifyHashedPassword(new AdminAccountHashSubject(), DummyPasswordHash, password);
            return new AppAuthenticationOutcome(AppAuthenticationResult.InvalidCredentials, username, null, AdminAuthDenialLayer.None);
        }

        // 評価順序 ③アカウント単位バックオフ: パスワード検証の前に遅延を適用する（決定 3）。
        var delay = _defense.GetBackoffDelay(account.Username, context.IsLoopback);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        var verifyResult = Hasher.VerifyHashedPassword(new AdminAccountHashSubject(), account.PasswordHash, password);
        if (verifyResult is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded)
        {
            _defense.RecordSuccess(account.Username, context.IsLoopback);
            await _store.RecordSuccessfulLoginAsync(account.Username, now, cancellationToken).ConfigureAwait(false);
            return new AppAuthenticationOutcome(AppAuthenticationResult.Success, account.Username, null, AdminAuthDenialLayer.None);
        }

        var failure = _defense.RecordFailure(account.Username, context.IsLoopback, context.RemoteAddress);

        if (delay > TimeSpan.Zero)
        {
            // 今回の試行はバックオフ待機を伴った失敗——決定 3・6 の統一応答（Denied）を返す。
            return new AppAuthenticationOutcome(
                AppAuthenticationResult.Denied, account.Username, CeilingSeconds(delay), AdminAuthDenialLayer.Backoff, failure.CapReachedThisAttempt);
        }

        // バックオフ猶予閾値 k 未満——通常の失敗ログインとして扱う（待機なし）。
        return new AppAuthenticationOutcome(AppAuthenticationResult.InvalidCredentials, account.Username, null, AdminAuthDenialLayer.None);
    }

    /// <summary>
    /// 最初の管理者アカウントを作成、またはパスワードを変更する（ADR-0010 決定 3。ADR-0011 決定 7
    /// のパスワード強度要件を検証する）。
    /// </summary>
    /// <exception cref="AdminPasswordPolicyViolationException">
    /// パスワードが最小長未満、または既知漏洩パスワード・頻出パターンのブロックリストに一致する場合。
    /// </exception>
    public async Task SetAccountAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        ValidatePasswordPolicy(password);

        var hash = Hasher.HashPassword(new AdminAccountHashSubject(), password);
        await _store.UpsertAsync(username, hash, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// パスワード強度要件（ADR-0011 決定 7）を検証する: 最小長 12 文字以上・既知漏洩パスワード/
    /// 頻出パターンのブロックリスト突合（大文字小文字を区別しない）。文字種別の複雑性ルールは
    /// 課さない（決定 7 が明示的に却下——予測可能なパターンへ誘導し実効強度を下げるため）。
    /// </summary>
    private static void ValidatePasswordPolicy(string password)
    {
        if (password.Length < AdminAuthenticationDefaults.MinimumPasswordLength)
        {
            throw new AdminPasswordPolicyViolationException(
                $"パスワードは {AdminAuthenticationDefaults.MinimumPasswordLength} 文字以上にしてください。");
        }

        if (AdminPasswordBlocklist.IsBlocked(password))
        {
            throw new AdminPasswordPolicyViolationException(
                "このパスワードは既知の漏洩パスワード・頻出パターンのブロックリストに一致するため使用できません。" +
                "別のパスワードを指定してください。");
        }
    }

    private static int CeilingSeconds(TimeSpan value) => (int)Math.Ceiling(value.TotalSeconds);
}

/// <summary><see cref="PasswordHasher{TUser}"/> の型引数専用のマーカー型（意味のあるメンバーを持たない）。</summary>
public sealed class AdminAccountHashSubject
{
}

/// <summary>
/// パスワード強度要件（ADR-0011 決定 7）に違反した場合の例外。ウィザード検証例外と同様、
/// 利用者向けの日本語メッセージをそのまま持つ。
/// </summary>
public sealed class AdminPasswordPolicyViolationException(string message) : Exception(message)
{
}
