using Yagura.Abstractions.Administration;

namespace Yagura.TestSupport.Fakes;

/// <summary>
/// <see cref="IAppAdminAuthenticator"/>のフェイク。固定の<paramref name="outcome"/>を返しつつ、
/// 呼び出しの有無と最後に渡された<see cref="AdminAuthAttemptContext"/>を記録する。
/// </summary>
public sealed class FakeAppAuthenticator(AppAuthenticationOutcome outcome) : IAppAdminAuthenticator
{
    public bool WasCalled { get; private set; }

    public AdminAuthAttemptContext? LastContext { get; private set; }

    public Task<AppAuthenticationOutcome> TryAuthenticateAsync(
        string username, string password, AdminAuthAttemptContext context, CancellationToken cancellationToken = default)
    {
        WasCalled = true;
        LastContext = context;
        return Task.FromResult(outcome);
    }
}
