using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Administration.ForwarderKit;
using Yagura.Host.Configuration;
using Yagura.Storage.Administration;
using Yagura.Storage.Administration.Sqlite;
using Yagura.TestSupport;

namespace Yagura.Host.Tests.Administration.ForwarderKit;

/// <summary>
/// <see cref="ForwarderMsiUploadAdminService"/>（ADR-0021 決定 4 = 委任 2）の単体テスト。
/// </summary>
/// <remarks>
/// 固定すべき不変条件は 3 つ: ①トグル操作の認可がアップロード操作と同格であること
/// （無認証 loopback から opt-in を反転できない）、②前提条件（認証方式が最低 1 つ）の
/// UI 層 fail-closed、③有効化時の切替時点検（既存アカウントの確認）。
/// </remarks>
public sealed class ForwarderMsiUploadAdminServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset TestNow = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly TestTempDirectory _tempDir = new("msiupload-admin");
    private string _dataRoot => _tempDir.Path;
    private SqliteAdminAccountStore _accountStore = null!;
    private RecordingAuditRecorder _audit = null!;
    private ForwarderMsiUploadAdminService _service = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        _accountStore = new SqliteAdminAccountStore(Path.Combine(_dataRoot, "yagura.db"));
        await _accountStore.InitializeAsync();
        _audit = new RecordingAuditRecorder();
        _service = new ForwarderMsiUploadAdminService(_dataRoot, _accountStore, _audit);
    }

    public async Task DisposeAsync()
    {
        await _accountStore.DisposeAsync();
        _tempDir.Dispose();
    }

    [Fact]
    public async Task ConfigureAsync_UnauthenticatedOperator_ThrowsAndDoesNotWrite()
    {
        // 不変条件①: 無認証 loopback から opt-in を反転できない（ADR-0021 決定 4）。
        SeedAuthentication(appAuthEnabled: true);

        var exception = await Assert.ThrowsAsync<WizardValidationException>(() =>
            _service.ConfigureAsync(enabled: true, operatorIsUploadOperationAuthenticated: false));

        Assert.Contains("サインインが必要", exception.Message);
        Assert.False((await _service.GetStatusAsync()).Enabled);
        Assert.Empty(_audit.Recorded);
    }

    [Fact]
    public async Task ConfigureAsync_EnableWithoutAnyAuthMethod_ThrowsFailClosed()
    {
        // 不変条件②: 起動時 1032 と同じ判定を書き込み前に行う（1011/1012 型の二段構え）。
        var exception = await Assert.ThrowsAsync<WizardValidationException>(() =>
            _service.ConfigureAsync(enabled: true, operatorIsUploadOperationAuthenticated: true));

        Assert.Contains("Windows 統合認証", exception.Message);
        Assert.False((await _service.GetStatusAsync()).Enabled);
    }

    [Fact]
    public async Task ConfigureAsync_EnableWithExistingAccountWithoutAcknowledgement_Throws()
    {
        // 不変条件③: 切替時点検（ADR-0021 決定 1 の事前仕込み対処。round 2 田中・クリス指摘）。
        SeedAuthentication(appAuthEnabled: true);
        await _accountStore.UpsertAsync("admin1", "hash", TestNow);

        var exception = await Assert.ThrowsAsync<WizardValidationException>(() =>
            _service.ConfigureAsync(
                enabled: true, accountInventoryAcknowledged: false, operatorIsUploadOperationAuthenticated: true));

        Assert.Contains("admin1", exception.Message);
        Assert.False((await _service.GetStatusAsync()).Enabled);
    }

    [Fact]
    public async Task ConfigureAsync_EnableWithAcknowledgement_SavesAndAudits()
    {
        SeedAuthentication(appAuthEnabled: true);
        await _accountStore.UpsertAsync("admin1", "hash", TestNow);

        var result = await _service.ConfigureAsync(
            enabled: true,
            accountInventoryAcknowledged: true,
            operatorAddress: "::1",
            operatorScheme: "app",
            operatorPrincipal: "app:admin1",
            operatorIsUploadOperationAuthenticated: true);

        Assert.True(result.Changed);
        // 反映は再起動（構造的非存在の判定が起動時固定。受信断の窓を隠さない）。
        Assert.Equal(ConfigurationApplyEffect.RestartRequired, result.RequiredEffect);
        Assert.True(result.Status.Enabled);
        Assert.True((await _service.GetStatusAsync()).Enabled);

        // 監査には「何を見て有効化したか」（提示した既存アカウントと確認の有無）が残る。
        var recorded = Assert.Single(_audit.Recorded);
        Assert.Equal(AuditEventKind.ConfigurationSaved, recorded.Kind);
        Assert.Contains("key=Admin:ForwarderKit:MsiUpload:Enabled value=True", recorded.Detail);
        Assert.Contains("existingAppAccount=admin1", recorded.Detail);
        Assert.Contains("inventoryAcknowledged=True", recorded.Detail);
        Assert.Equal("app:admin1", recorded.AuthenticatedPrincipal);
    }

    [Fact]
    public async Task ConfigureAsync_EnableWithNoExistingAccount_DoesNotRequireAcknowledgement()
    {
        // 点検の対象が無い（Windows 統合認証のみの構成）なら確認は求めない。
        SeedAuthentication(windowsAuthEnabled: true);

        var result = await _service.ConfigureAsync(
            enabled: true, accountInventoryAcknowledged: false, operatorIsUploadOperationAuthenticated: true);

        Assert.True(result.Changed);
        Assert.True(result.Status.Enabled);
        Assert.Contains("existingAppAccount=(none)", Assert.Single(_audit.Recorded).Detail);
    }

    [Fact]
    public async Task ConfigureAsync_Disable_DoesNotRequireAcknowledgement()
    {
        SeedAuthentication(appAuthEnabled: true);
        await _accountStore.UpsertAsync("admin1", "hash", TestNow);
        await _service.ConfigureAsync(
            enabled: true, accountInventoryAcknowledged: true, operatorIsUploadOperationAuthenticated: true);
        _audit.Recorded.Clear();

        // 無効化は書き込み口を閉じる方向のため点検を求めない。
        var result = await _service.ConfigureAsync(
            enabled: false, accountInventoryAcknowledged: false, operatorIsUploadOperationAuthenticated: true);

        Assert.True(result.Changed);
        Assert.False(result.Status.Enabled);
        Assert.Contains("value=False", Assert.Single(_audit.Recorded).Detail);
    }

    [Fact]
    public async Task ConfigureAsync_NoChange_IsNoOpWithoutAudit()
    {
        SeedAuthentication(appAuthEnabled: true);

        var result = await _service.ConfigureAsync(
            enabled: false, operatorIsUploadOperationAuthenticated: true);

        Assert.False(result.Changed);
        Assert.Equal(ConfigurationApplyEffect.Immediate, result.RequiredEffect);
        Assert.Empty(_audit.Recorded);
    }

    [Fact]
    public async Task GetStatusAsync_LegacyAccountWithoutTimestamps_StillExposesLastLogin()
    {
        // Issue #458: アップグレード環境の既存行は CreatedAtUtc/UpdatedAtUtc が NULL（v3 で
        // 追加した列のため）。点検が「ユーザー名だけ」に退化しないよう、旧版から記録されている
        // 最終ログインを手がかりとして提示する。
        SeedAuthentication(appAuthEnabled: true);
        await SeedLegacyAccountWithoutTimestampsAsync("legacy-admin", TestNow.AddDays(-30));

        var status = await _service.GetStatusAsync();

        Assert.True(status.HasAppAccount);
        Assert.Equal("legacy-admin", status.AppAccountUsername);
        Assert.Null(status.AppAccountCreatedAtUtc);
        Assert.Null(status.AppAccountUpdatedAtUtc);
        Assert.Equal(TestNow.AddDays(-30), status.AppAccountLastLoginAtUtc);
    }

    [Fact]
    public async Task ConfigureAsync_LegacyAccount_RecordsLastLoginInAudit()
    {
        SeedAuthentication(appAuthEnabled: true);
        await SeedLegacyAccountWithoutTimestampsAsync("legacy-admin", TestNow.AddDays(-30));

        await _service.ConfigureAsync(
            enabled: true, accountInventoryAcknowledged: true, operatorIsUploadOperationAuthenticated: true);

        var detail = Assert.Single(_audit.Recorded).Detail;
        Assert.Contains("existingAppAccount=legacy-admin", detail);
        Assert.Contains("existingAppAccountUpdatedAt=unknown", detail);
        // 作成・変更が unknown でも、何を手がかりに判断したか（最終ログイン）は残る。
        Assert.Contains("existingAppAccountLastLoginAt=2026-06-26", detail);
    }

    /// <summary>
    /// v3 より前に作られたアカウント（時刻列が NULL・最終ログインのみ記録済み）を再現する。
    /// <c>UpsertAsync</c> は必ず時刻を書くため、列を直接 NULL へ戻して状況を作る。
    /// </summary>
    private async Task SeedLegacyAccountWithoutTimestampsAsync(string username, DateTimeOffset lastLoginAtUtc)
    {
        await _accountStore.UpsertAsync(username, "legacy-hash", TestNow);
        await _accountStore.RecordSuccessfulLoginAsync(username, lastLoginAtUtc);

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_dataRoot, "yagura.db"),
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE AdminAccounts SET CreatedAtUtc = NULL, UpdatedAtUtc = NULL;";
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task GetStatusAsync_ExposesAccountTimestampsForInventoryCheck()
    {
        // 切替時点検の提示材料（ADR-0021 決定 1）。スキーマ v3 の時刻列がそのまま出ること。
        SeedAuthentication(appAuthEnabled: true);
        await _accountStore.UpsertAsync("admin1", "hash", TestNow);
        await _accountStore.UpsertAsync("admin1", "hash2", TestNow.AddHours(3));

        var status = await _service.GetStatusAsync();

        Assert.True(status.HasAppAccount);
        Assert.Equal("admin1", status.AppAccountUsername);
        Assert.Equal(TestNow, status.AppAccountCreatedAtUtc);
        Assert.Equal(TestNow.AddHours(3), status.AppAccountUpdatedAtUtc);
    }

    private void SeedAuthentication(bool windowsAuthEnabled = false, bool appAuthEnabled = false)
    {
        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        var options = snapshot.Options;
        options.Admin ??= new YaguraConfigurationOptions.AdminOptions();
        options.Admin.Authentication ??= new YaguraConfigurationOptions.AdminOptions.AuthenticationOptions();
        options.Admin.Authentication.Windows ??= new YaguraConfigurationOptions.AdminOptions.AuthenticationOptions.WindowsOptions();
        options.Admin.Authentication.App ??= new YaguraConfigurationOptions.AdminOptions.AuthenticationOptions.AppOptions();
        options.Admin.Authentication.Windows.Enabled = windowsAuthEnabled.ToString();
        options.Admin.Authentication.App.Enabled = appAuthEnabled.ToString();
        YaguraConfigurationWriter.Save(_dataRoot, options, snapshot.VersionToken);
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
