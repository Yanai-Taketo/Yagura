using Microsoft.Extensions.Time.Testing;
using Yagura.Abstractions.Administration;
using Yagura.Host.Administration.AdminAuthentication;
using Yagura.Storage.Administration;
using Yagura.Storage.Administration.Sqlite;

namespace Yagura.Host.Tests.Administration.AdminAuthentication;

/// <summary>
/// <see cref="AppAdminAuthenticationService"/> の単体テスト（ADR-0010 決定 3。委任事項 4）。
/// </summary>
/// <remarks>
/// 検証観点: (1) 正しい資格情報での成功、(2) 誤パスワードでの失敗、(3) 存在しないユーザー名でも
/// 同一の <see cref="AppAuthenticationResult.InvalidCredentials"/> を返す（ユーザー列挙耐性）、
/// (4) ロックアウト閾値到達での <see cref="AppAuthenticationResult.LockedOutNow"/>、
/// (5) ロックアウト中は資格情報の正誤に関わらず <see cref="AppAuthenticationResult.LockedOut"/>、
/// (6) 成功でロックアウトカウンタがリセットされる、(7) パスワード変更後は旧パスワードで失敗する。
/// </remarks>
public sealed class AppAdminAuthenticationServiceTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"yagura-app-auth-test-{Guid.NewGuid():N}.db");
    private SqliteAdminAccountStore _store = null!;
    private AppAdminAuthenticationService _service = null!;
    private FakeTimeProvider _timeProvider = null!;

    public async Task InitializeAsync()
    {
        _store = new SqliteAdminAccountStore(_databasePath);
        await _store.InitializeAsync();

        _timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-10T00:00:00Z"));
        _service = new AppAdminAuthenticationService(_store, _timeProvider);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();

        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task TryAuthenticateAsync_CorrectCredentials_Succeeds()
    {
        await _service.SetAccountAsync("admin1", "correct-horse-battery-staple");

        var outcome = await _service.TryAuthenticateAsync("admin1", "correct-horse-battery-staple");

        Assert.Equal(AppAuthenticationResult.Success, outcome.Result);
        Assert.Equal("admin1", outcome.Username);
    }

    [Fact]
    public async Task TryAuthenticateAsync_WrongPassword_ReturnsInvalidCredentials()
    {
        await _service.SetAccountAsync("admin1", "correct-password");

        var outcome = await _service.TryAuthenticateAsync("admin1", "wrong-password");

        Assert.Equal(AppAuthenticationResult.InvalidCredentials, outcome.Result);
    }

    [Fact]
    public async Task TryAuthenticateAsync_UnknownUsername_ReturnsSameResultAsWrongPassword_EnumerationResistance()
    {
        // ユーザー列挙耐性(ADR-0010 決定 3・委任事項 4): 存在しないユーザー名でも
        // 実在アカウントの誤パスワードと同一の結果種別を返す(応答内容で区別しない)。
        await _service.SetAccountAsync("admin1", "correct-password");

        var unknownUserOutcome = await _service.TryAuthenticateAsync("no-such-user", "whatever");
        var wrongPasswordOutcome = await _service.TryAuthenticateAsync("admin1", "wrong-password");

        Assert.Equal(AppAuthenticationResult.InvalidCredentials, unknownUserOutcome.Result);
        Assert.Equal(AppAuthenticationResult.InvalidCredentials, wrongPasswordOutcome.Result);
    }

    [Fact]
    public async Task TryAuthenticateAsync_ReachesLockoutThreshold_LocksOutAndSubsequentAttemptsFail()
    {
        await _service.SetAccountAsync("admin1", "correct-password");

        AppAuthenticationOutcome? lastOutcome = null;
        for (var i = 0; i < Yagura.Host.Administration.AdminAuthenticationDefaults.LockoutThreshold; i++)
        {
            lastOutcome = await _service.TryAuthenticateAsync("admin1", "wrong-password");
        }

        Assert.Equal(AppAuthenticationResult.LockedOutNow, lastOutcome!.Result);

        // ロックアウト中は正しいパスワードでも拒否される。
        var duringLockout = await _service.TryAuthenticateAsync("admin1", "correct-password");
        Assert.Equal(AppAuthenticationResult.LockedOut, duringLockout.Result);
    }

    [Fact]
    public async Task TryAuthenticateAsync_LockoutExpires_AllowsRetryAfterDuration()
    {
        await _service.SetAccountAsync("admin1", "correct-password");

        for (var i = 0; i < Yagura.Host.Administration.AdminAuthenticationDefaults.LockoutThreshold; i++)
        {
            await _service.TryAuthenticateAsync("admin1", "wrong-password");
        }

        _timeProvider.Advance(Yagura.Host.Administration.AdminAuthenticationDefaults.LockoutDuration + TimeSpan.FromSeconds(1));

        var afterExpiry = await _service.TryAuthenticateAsync("admin1", "correct-password");
        Assert.Equal(AppAuthenticationResult.Success, afterExpiry.Result);
    }

    [Fact]
    public async Task TryAuthenticateAsync_SuccessResetsFailedAttemptCounter()
    {
        await _service.SetAccountAsync("admin1", "correct-password");

        await _service.TryAuthenticateAsync("admin1", "wrong-password");
        await _service.TryAuthenticateAsync("admin1", "correct-password");

        var account = await _store.FindByUsernameAsync("admin1");
        Assert.Equal(0, account!.FailedAttemptCount);
    }

    [Fact]
    public async Task SetAccountAsync_ChangesPassword_OldPasswordNoLongerWorks()
    {
        await _service.SetAccountAsync("admin1", "old-password");
        await _service.SetAccountAsync("admin1", "new-password");

        var withOldPassword = await _service.TryAuthenticateAsync("admin1", "old-password");
        var withNewPassword = await _service.TryAuthenticateAsync("admin1", "new-password");

        Assert.Equal(AppAuthenticationResult.InvalidCredentials, withOldPassword.Result);
        Assert.Equal(AppAuthenticationResult.Success, withNewPassword.Result);
    }
}
