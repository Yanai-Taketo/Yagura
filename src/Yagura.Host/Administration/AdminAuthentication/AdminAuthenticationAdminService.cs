using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Configuration;
using Yagura.Storage.Administration;

namespace Yagura.Host.Administration.AdminAuthentication;

/// <summary>
/// <see cref="IAdminAuthenticationAdminService"/> の実体（ADR-0010 決定 1・3。Phase 1）。
/// </summary>
/// <remarks>
/// 設定ファイルの読み書きは <see cref="YaguraConfigurationWriter"/> に、パスワードのハッシュ化は
/// <see cref="AppAdminAuthenticationService"/> に委ねる（単一責務の合成——<see cref="SetupWizardService"/>
/// と同じ「Host が結線する」パターン。<see cref="IAdminAuthenticationAdminService"/> の remarks 参照）。
/// </remarks>
public sealed class AdminAuthenticationAdminService : IAdminAuthenticationAdminService
{
    private readonly string _dataRoot;
    private readonly IAdminAccountStore _accountStore;
    private readonly AppAdminAuthenticationService _appAuthenticationService;
    private readonly IAuditRecorder _auditRecorder;
    private readonly TimeProvider _timeProvider;

    public AdminAuthenticationAdminService(
        string dataRoot,
        IAdminAccountStore accountStore,
        AppAdminAuthenticationService appAuthenticationService,
        IAuditRecorder auditRecorder,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(accountStore);
        ArgumentNullException.ThrowIfNull(appAuthenticationService);
        ArgumentNullException.ThrowIfNull(auditRecorder);

        _dataRoot = dataRoot;
        _accountStore = accountStore;
        _appAuthenticationService = appAuthenticationService;
        _auditRecorder = auditRecorder;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AdminAuthenticationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        var account = await _accountStore.GetSoleAccountAsync(cancellationToken).ConfigureAwait(false);

        return ToStatus(snapshot.Options, account);
    }

    public async Task<AdminAuthenticationStatus> ConfigureAsync(
        bool windowsAuthEnabled,
        bool kerberosOnly,
        bool appAuthEnabled,
        bool requireForLoopback,
        string? newAppUsername,
        string? newAppPassword,
        string? operatorAddress = null,
        CancellationToken cancellationToken = default)
    {
        // fail-closed 不変条件（ADR-0010 決定 1）: 起動時検証（YaguraConfigurationLoader）と
        // 同じ判定を、ウィザード画面上でも先に拒否する——「有効化を受け付けない」というオーナー
        // 決定の実装は、まず UI 層で親切に拒否し、手編集で作られた場合の最終防衛線を起動時
        // 検証が担う二段構え。
        if (requireForLoopback && !windowsAuthEnabled && !appAuthEnabled)
        {
            throw new WizardValidationException(
                "loopback 認証 opt-in を有効にするには、Windows 統合認証またはアプリ独自認証の" +
                "少なくとも一方を有効にしてください（認証方式が一つもない状態で loopback にも" +
                "認証を課すと、管理 UI に一切到達できなくなります）。");
        }

        var hasCreatingAccount = !string.IsNullOrWhiteSpace(newAppUsername) && !string.IsNullOrWhiteSpace(newAppPassword);

        if (appAuthEnabled && !hasCreatingAccount)
        {
            var hasExisting = await _accountStore.HasAnyAccountAsync(cancellationToken).ConfigureAwait(false);
            if (!hasExisting)
            {
                throw new WizardValidationException(
                    "アプリ独自認証を有効にするには、最初の管理者アカウント（ユーザー名/パスワード）を" +
                    "同時に設定してください。");
            }
        }

        string? createdUsername = null;

        if (hasCreatingAccount)
        {
            await _appAuthenticationService.SetAccountAsync(newAppUsername!, newAppPassword!, cancellationToken).ConfigureAwait(false);
            createdUsername = newAppUsername;
        }

        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        var before = snapshot.Options;
        var after = YaguraConfigurationOptionsCloner.Clone(before);

        after.Admin ??= new YaguraConfigurationOptions.AdminOptions();
        after.Admin.Authentication ??= new YaguraConfigurationOptions.AdminOptions.AuthenticationOptions();
        after.Admin.Authentication.Windows ??= new YaguraConfigurationOptions.AdminOptions.AuthenticationOptions.WindowsOptions();
        after.Admin.Authentication.App ??= new YaguraConfigurationOptions.AdminOptions.AuthenticationOptions.AppOptions();

        after.Admin.Authentication.Windows.Enabled = windowsAuthEnabled.ToString();
        after.Admin.Authentication.Windows.KerberosOnly = kerberosOnly.ToString();
        after.Admin.Authentication.App.Enabled = appAuthEnabled.ToString();
        after.Admin.Authentication.RequireForLoopback = requireForLoopback.ToString();

        YaguraConfigurationWriter.Save(_dataRoot, after, snapshot.VersionToken);

        var now = _timeProvider.GetUtcNow();

        await _auditRecorder.RecordAsync(
            new AuditEvent(
                OccurredAt: now,
                Kind: AuditEventKind.AdminAuthenticationConfigured,
                RemoteAddress: operatorAddress,
                RemotePort: null,
                Detail: $"windows={windowsAuthEnabled} kerberosOnly={kerberosOnly} app={appAuthEnabled} requireForLoopback={requireForLoopback}"),
            CancellationToken.None).ConfigureAwait(false);

        if (createdUsername is not null)
        {
            await _auditRecorder.RecordAsync(
                new AuditEvent(
                    OccurredAt: now,
                    Kind: AuditEventKind.AdminAccountCreated,
                    RemoteAddress: operatorAddress,
                    RemotePort: null,
                    Detail: $"username={createdUsername}"),
                CancellationToken.None).ConfigureAwait(false);
        }

        var account = await _accountStore.GetSoleAccountAsync(cancellationToken).ConfigureAwait(false);
        return ToStatus(after, account);
    }

    private static AdminAuthenticationStatus ToStatus(YaguraConfigurationOptions options, AdminAccountRecord? account)
    {
        var windowsEnabled = ParseBool(options.Admin?.Authentication?.Windows?.Enabled);
        var kerberosOnly = ParseBool(options.Admin?.Authentication?.Windows?.KerberosOnly);
        var appEnabled = ParseBool(options.Admin?.Authentication?.App?.Enabled);
        var requireForLoopback = ParseBool(options.Admin?.Authentication?.RequireForLoopback);

        return new AdminAuthenticationStatus(
            WindowsAuthEnabled: windowsEnabled,
            KerberosOnly: kerberosOnly,
            AppAuthEnabled: appEnabled,
            RequireForLoopback: requireForLoopback,
            HasAppAccount: account is not null,
            AppAccountUsername: account?.Username);
    }

    private static bool ParseBool(string? raw) => bool.TryParse(raw, out var value) && value;
}
