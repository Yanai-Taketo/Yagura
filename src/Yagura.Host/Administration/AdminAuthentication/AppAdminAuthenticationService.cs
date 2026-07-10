using Microsoft.AspNetCore.Identity;
using Yagura.Abstractions.Administration;
using Yagura.Storage.Administration;

namespace Yagura.Host.Administration.AdminAuthentication;

/// <summary>
/// アプリ独自 ID/パスワード認証の実体（ADR-0010 決定 3。委任事項 4）。
/// </summary>
/// <remarks>
/// <para>
/// <b>パスワードハッシュ（委任事項 4 のライブ検証結果）</b>: <c>Microsoft.AspNetCore.Identity.PasswordHasher&lt;TUser&gt;</c>
/// （PBKDF2 実装）は <c>Microsoft.Extensions.Identity.Core</c> アセンブリに属し、これは
/// <c>Microsoft.AspNetCore.App</c> 共有フレームワークに含まれる（Yagura.Web は Sdk.Razor から
/// 明示 <c>FrameworkReference</c> で参照済み）——<b>追加の NuGet 依存なしに利用できることを
/// 実機ビルドで確認した</b>（conventions.md の技術的主張の検証原則に従うライブ検証。
/// ASP.NET Core Identity のフル導入（<c>Microsoft.AspNetCore.Identity.EntityFrameworkCore</c>。
/// EF Core ストア）とは別物であり、本体は採用していない——ADR-0010「検討した選択肢」のとおり）。
/// <c>TUser</c> はハッシュ形式に影響しないため、意味のあるプロパティを持たないマーカー型
/// （<see cref="AdminAccountHashSubject"/>）を使う。
/// </para>
/// <para>
/// <b>ユーザー列挙耐性（委任事項 4）</b>: 存在しないユーザー名でも、実在するアカウントに対する
/// 誤パスワードと同じだけの処理時間・同じ応答（<see cref="AppAuthenticationResult.InvalidCredentials"/>）
/// を返す——存在しないユーザー名の場合も <see cref="DummyPasswordHash"/> に対してハッシュ検証を
/// 実行することで、検証コスト（PBKDF2 の反復計算）による所要時間の差を縮める。呼び出し元
/// （ログイン HTTP ハンドラ）は成功/失敗の理由を利用者応答で区別してはならない（決定 3・
/// security.md §4.3「失敗理由の種別は監査記録にのみ残す」）——理由の区別は
/// <see cref="AppAuthenticationResult"/> の詳細ではなく監査記録（呼び出し元が別途記録する）に
/// 委ねる。
/// </para>
/// <para>
/// <b>ロックアウト</b>: <see cref="AdminAuthenticationDefaults"/> の仮値を使う。ロックアウト中は
/// 実パスワードの検証を行わず即座に <see cref="AppAuthenticationResult.LockedOut"/> を返す
/// （無駄な PBKDF2 計算を避ける最適化——ロックアウト中である事実自体は秘密ではないため
/// 列挙耐性の対象外）。
/// </para>
/// <para>
/// <b>単一アカウントモデル（Phase 1）</b>: <see cref="SetAccountAsync"/> は既存アカウントの
/// パスワードを置き換える（アカウント追加ではなく「最初の管理者アカウント」の設定/変更）。
/// 複数アカウントの管理は Phase 1 のスコープ外（<see cref="IAdminAccountStore"/> の remarks 参照）。
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
    private readonly TimeProvider _timeProvider;

    public AppAdminAuthenticationService(IAdminAccountStore store, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>ログイン試行を検証する（成功/失敗いずれも列挙耐性を伴う）。</summary>
    public async Task<AppAuthenticationOutcome> TryAuthenticateAsync(
        string username, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(password);

        var now = _timeProvider.GetUtcNow();
        var account = await _store.FindByUsernameAsync(username, cancellationToken).ConfigureAwait(false);

        if (account is null)
        {
            // 列挙耐性: 実在アカウントと同等のハッシュ検証コストを払ったうえで拒否する。
            Hasher.VerifyHashedPassword(new AdminAccountHashSubject(), DummyPasswordHash, password);
            return new AppAuthenticationOutcome(AppAuthenticationResult.InvalidCredentials, username, null);
        }

        if (account.LockoutUntilUtc is { } lockoutUntil && lockoutUntil > now)
        {
            return new AppAuthenticationOutcome(AppAuthenticationResult.LockedOut, account.Username, lockoutUntil);
        }

        var verifyResult = Hasher.VerifyHashedPassword(new AdminAccountHashSubject(), account.PasswordHash, password);
        if (verifyResult is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded)
        {
            await _store.RecordSuccessfulLoginAsync(account.Username, now, cancellationToken).ConfigureAwait(false);
            return new AppAuthenticationOutcome(AppAuthenticationResult.Success, account.Username, null);
        }

        var failure = await _store.RecordFailedLoginAsync(
            account.Username,
            now,
            AdminAuthenticationDefaults.LockoutThreshold,
            AdminAuthenticationDefaults.LockoutDuration,
            cancellationToken).ConfigureAwait(false);

        return failure.LockedOutNow
            ? new AppAuthenticationOutcome(AppAuthenticationResult.LockedOutNow, account.Username, failure.LockoutUntilUtc)
            : new AppAuthenticationOutcome(AppAuthenticationResult.InvalidCredentials, account.Username, null);
    }

    /// <summary>最初の管理者アカウントを作成、またはパスワードを変更する（決定 3）。</summary>
    public async Task SetAccountAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var hash = Hasher.HashPassword(new AdminAccountHashSubject(), password);
        await _store.UpsertAsync(username, hash, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><see cref="PasswordHasher{TUser}"/> の型引数専用のマーカー型（意味のあるメンバーを持たない）。</summary>
public sealed class AdminAccountHashSubject
{
}
