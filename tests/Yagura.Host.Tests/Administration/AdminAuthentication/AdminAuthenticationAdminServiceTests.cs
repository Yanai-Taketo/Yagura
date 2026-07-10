using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Administration.AdminAuthentication;
using Yagura.Host.Configuration;
using Yagura.Storage.Administration.Sqlite;

namespace Yagura.Host.Tests.Administration.AdminAuthentication;

/// <summary>
/// <see cref="AdminAuthenticationAdminService"/> の単体テスト（ADR-0010 決定 1・3。Phase 1）。
/// </summary>
public sealed class AdminAuthenticationAdminServiceTests : IAsyncLifetime
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-authadmin-test-{Guid.NewGuid():N}");
    private readonly string _databasePath;
    private SqliteAdminAccountStore _accountStore = null!;
    private AppAdminAuthenticationService _appAuthService = null!;
    private RecordingAuditRecorder _audit = null!;
    private AdminAuthenticationAdminService _service = null!;

    public AdminAuthenticationAdminServiceTests()
    {
        _databasePath = Path.Combine(_dataRoot, "yagura.db");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        _accountStore = new SqliteAdminAccountStore(_databasePath);
        await _accountStore.InitializeAsync();
        _appAuthService = new AppAdminAuthenticationService(_accountStore);
        _audit = new RecordingAuditRecorder();
        _service = new AdminAuthenticationAdminService(_dataRoot, _accountStore, _appAuthService, _audit);
    }

    public async Task DisposeAsync()
    {
        await _accountStore.DisposeAsync();

        if (Directory.Exists(_dataRoot))
        {
            try
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
            catch (IOException)
            {
                // ベストエフォート。
            }
        }
    }

    [Fact]
    public async Task GetStatusAsync_NoConfigurationYet_ReturnsAllDisabled()
    {
        var status = await _service.GetStatusAsync();

        Assert.False(status.WindowsAuthEnabled);
        Assert.False(status.AppAuthEnabled);
        Assert.False(status.RequireForLoopback);
        Assert.False(status.HasAppAccount);
        Assert.Null(status.AppAccountUsername);
    }

    [Fact]
    public async Task ConfigureAsync_RequireForLoopbackWithoutAnyAuthMethod_ThrowsValidationException()
    {
        // UI 層での親切な拒否(起動時 fail-closed と同じ判定。ADR-0010 決定 1)。
        await Assert.ThrowsAsync<WizardValidationException>(() =>
            _service.ConfigureAsync(
                windowsAuthEnabled: false,
                kerberosOnly: false,
                appAuthEnabled: false,
                requireForLoopback: true,
                newAppUsername: null,
                newAppPassword: null));
    }

    [Fact]
    public async Task ConfigureAsync_EnableAppAuthWithoutAnyAccountAndWithoutCreatingOne_ThrowsValidationException()
    {
        await Assert.ThrowsAsync<WizardValidationException>(() =>
            _service.ConfigureAsync(
                windowsAuthEnabled: false,
                kerberosOnly: false,
                appAuthEnabled: true,
                requireForLoopback: false,
                newAppUsername: null,
                newAppPassword: null));
    }

    [Fact]
    public async Task ConfigureAsync_EnableAppAuthWithNewAccount_CreatesAccountAndSavesConfiguration()
    {
        var status = await _service.ConfigureAsync(
            windowsAuthEnabled: false,
            kerberosOnly: false,
            appAuthEnabled: true,
            requireForLoopback: false,
            newAppUsername: "admin1",
            newAppPassword: "correct-horse-battery-staple",
            operatorAddress: "127.0.0.1",
            operatorScheme: "windows",
            operatorPrincipal: "CONTOSO\\jdoe");

        Assert.True(status.AppAuthEnabled);
        Assert.True(status.HasAppAccount);
        Assert.Equal("admin1", status.AppAccountUsername);

        // 設定ファイルへ反映されていること。
        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        Assert.Equal("True", snapshot.Options.Admin?.Authentication?.App?.Enabled);

        // アカウントで実際に認証できること。
        var outcome = await _appAuthService.TryAuthenticateAsync("admin1", "correct-horse-battery-staple");
        Assert.Equal(Yagura.Abstractions.Administration.AppAuthenticationResult.Success, outcome.Result);

        // 監査記録(2006 設定変更 + 2007 アカウント作成)——「誰が」欄（AuthenticationScheme/
        // AuthenticatedPrincipal。ADR-0010 決定 6）が両方の記録に伝播していること
        // （PR #217 レビュー指摘 1 の (b)(d)）。
        var configured = Assert.Single(_audit.Recorded, e => e.Kind == AuditEventKind.AdminAuthenticationConfigured);
        Assert.Equal("windows", configured.AuthenticationScheme);
        Assert.Equal("CONTOSO\\jdoe", configured.AuthenticatedPrincipal);
        Assert.Equal("127.0.0.1", configured.RemoteAddress);

        var accountCreated = Assert.Single(_audit.Recorded, e => e.Kind == AuditEventKind.AdminAccountCreated);
        Assert.Equal("windows", accountCreated.AuthenticationScheme);
        Assert.Equal("CONTOSO\\jdoe", accountCreated.AuthenticatedPrincipal);
        // パスワードそのものはいかなる監査記録にも現れない（決定 3）。
        Assert.All(_audit.Recorded, e => Assert.DoesNotContain("correct-horse-battery-staple", e.Detail ?? string.Empty));
    }

    [Fact]
    public async Task ConfigureAsync_UnauthenticatedOperator_RecordsAuditWithNullSchemeAndPrincipal()
    {
        // 既定（loopback 認証 opt-in 無効）の未認証操作: 「誰が」欄は接続元のみが実効値
        // （security.md §4.1 の射程限定——ADR-0010 決定 6）。
        await _service.ConfigureAsync(
            windowsAuthEnabled: true,
            kerberosOnly: false,
            appAuthEnabled: false,
            requireForLoopback: false,
            newAppUsername: null,
            newAppPassword: null,
            operatorAddress: "127.0.0.1");

        var configured = Assert.Single(_audit.Recorded, e => e.Kind == AuditEventKind.AdminAuthenticationConfigured);
        Assert.Null(configured.AuthenticationScheme);
        Assert.Null(configured.AuthenticatedPrincipal);
        Assert.Equal("127.0.0.1", configured.RemoteAddress);
    }

    [Fact]
    public async Task ConfigureAsync_EnableWindowsAuthOnly_SucceedsWithoutAccount()
    {
        var status = await _service.ConfigureAsync(
            windowsAuthEnabled: true,
            kerberosOnly: true,
            appAuthEnabled: false,
            requireForLoopback: true,
            newAppUsername: null,
            newAppPassword: null);

        Assert.True(status.WindowsAuthEnabled);
        Assert.True(status.KerberosOnly);
        Assert.True(status.RequireForLoopback);
        Assert.False(status.HasAppAccount);
    }

    [Fact]
    public async Task ConfigureAsync_ExistingAccount_CanChangePasswordWithoutRecreating()
    {
        await _service.ConfigureAsync(
            windowsAuthEnabled: false, kerberosOnly: false, appAuthEnabled: true, requireForLoopback: false,
            newAppUsername: "admin1", newAppPassword: "first-password");

        var status = await _service.ConfigureAsync(
            windowsAuthEnabled: false, kerberosOnly: false, appAuthEnabled: true, requireForLoopback: false,
            newAppUsername: "admin1", newAppPassword: "second-password");

        Assert.True(status.HasAppAccount);

        var withOld = await _appAuthService.TryAuthenticateAsync("admin1", "first-password");
        var withNew = await _appAuthService.TryAuthenticateAsync("admin1", "second-password");

        Assert.Equal(Yagura.Abstractions.Administration.AppAuthenticationResult.InvalidCredentials, withOld.Result);
        Assert.Equal(Yagura.Abstractions.Administration.AppAuthenticationResult.Success, withNew.Result);
    }

    private sealed class RecordingAuditRecorder : IAuditRecorder
    {
        public List<AuditEvent> Recorded { get; } = [];

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Recorded.Add(auditEvent);
            return Task.CompletedTask;
        }
    }
}
