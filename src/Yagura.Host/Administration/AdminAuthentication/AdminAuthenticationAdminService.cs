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

    /// <summary>
    /// フォワーダ MSI アップロード機能（<c>Admin:ForwarderKit:MsiUpload:Enabled</c>）の
    /// **稼働中プロセスにおける**実効値（起動時解決値。Program が結線する）。
    /// <see langword="true"/> の場合、本サービスは資格情報の発行口（ADR-0021 決定 1）として
    /// 実認証済みの操作者のみを受け付ける。
    /// </summary>
    private readonly bool _forwarderMsiUploadEnabled;

    public AdminAuthenticationAdminService(
        string dataRoot,
        IAdminAccountStore accountStore,
        AppAdminAuthenticationService appAuthenticationService,
        IAuditRecorder auditRecorder,
        TimeProvider? timeProvider = null,
        bool forwarderMsiUploadEnabled = false)
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
        _forwarderMsiUploadEnabled = forwarderMsiUploadEnabled;
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
        string? operatorScheme = null,
        string? operatorPrincipal = null,
        bool operatorIsUploadOperationAuthenticated = false,
        CancellationToken cancellationToken = default)
    {
        // 資格情報の発行口の統制（ADR-0021 決定 1）: アップロード機能が有効な稼働構成では、
        // 認証設定の変更・アプリアカウントの作成/変更に実認証済みの管理セッションを要求する。
        // 無認証 loopback でアカウントを自己発行 → その資格情報でサインイン → アップロード系
        // 操作の専用ポリシーを通過、というブートストラップ迂回を閉じるための最終防衛線
        // （UI 側の出し分けと同じ判定——AdminAuthSetupScreen が単一実装で判定した結果を受け取る）。
        // 機能無効の構成では従来どおり（ADR-0010 決定 3 の bootstrap 設計は不変）。
        if (_forwarderMsiUploadEnabled && !operatorIsUploadOperationAuthenticated)
        {
            throw new WizardValidationException(
                "フォワーダ MSI アップロード機能（Admin:ForwarderKit:MsiUpload:Enabled）が有効な" +
                "構成では、認証設定・管理者アカウントの変更にはサインインが必要です" +
                "（未サインインで作成した資格情報による MSI 書き込み口への迂回を防ぐため——" +
                "ADR-0021 決定 1）。サインインしてからやり直してください。");
        }

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

        // Phase 2 fail-closed（ADR-0010 Phase 2 決定 1）: 管理リスナのリモートバインドが有効な
        // 構成で認証方式を両方とも無効にすると、次回起動時に起動時 fail-closed 検証（イベント 1012）が
        // 起動を拒否し、syslog 受信ごと停止してしまう。その状態に陥る設定変更を UI 層で先に拒否する
        // （起動時検証と対称の二段構え）。本画面は RemoteBinding を変更しない（認証フラグのみ）ため、
        // 現在値は設定ファイルから読む。
        var currentOptions = YaguraConfigurationWriter.Read(_dataRoot).Options;
        var currentRemoteBindingEnabled = ParseBool(currentOptions.Admin?.RemoteBinding?.Enabled);
        if (currentRemoteBindingEnabled && !windowsAuthEnabled && !appAuthEnabled)
        {
            throw new WizardValidationException(
                "管理リスナのリモートバインド（Admin:RemoteBinding:Enabled）が有効な状態では、" +
                "認証方式（Windows 統合認証・アプリ独自認証）を両方とも無効にできません。" +
                "この設定のまま再起動すると、認証を欠いたリモート公開を防ぐ fail-closed 検証により" +
                "サービスが起動できなくなり、syslog 受信も停止します。少なくとも一方の認証方式を" +
                "有効に保つか、先に Admin:RemoteBinding:Enabled を false に戻してください。");
        }

        // fail-closed（UI）の対称防御（ADR-0021 決定 2——ADR-0020 決定 1 の (i)(iii) 側の判定）:
        // アップロード機能が設定ファイル上有効なまま認証方式を両方無効にすると、次回起動時に
        // 1032 が起動を拒否し syslog 受信ごと停止する。その状態に陥る設定変更を UI 層で先に拒否する
        // （1011/1012 型の判定と同じ二段構え。現在値は設定ファイルから読む——本画面は
        // MsiUpload:Enabled を変更しない）。
        var currentMsiUploadEnabled = ParseBool(currentOptions.Admin?.ForwarderKit?.MsiUpload?.Enabled);
        if (currentMsiUploadEnabled && !windowsAuthEnabled && !appAuthEnabled)
        {
            throw new WizardValidationException(
                "フォワーダ MSI アップロード機能（Admin:ForwarderKit:MsiUpload:Enabled）が有効な" +
                "状態では、認証方式（Windows 統合認証・アプリ独自認証）を両方とも無効にできません。" +
                "この設定のまま再起動すると、fail-closed 検証（イベント 1032）によりサービスが" +
                "起動できなくなり、syslog 受信も停止します。少なくとも一方の認証方式を有効に保つか、" +
                "先にアップロード機能を無効化してください。");
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
            try
            {
                await _appAuthenticationService.SetAccountAsync(newAppUsername!, newAppPassword!, cancellationToken).ConfigureAwait(false);
            }
            catch (AdminPasswordPolicyViolationException ex)
            {
                // ADR-0011 決定 7 のパスワード強度要件違反を、ウィザード画面が既に扱う
                // WizardValidationException（メッセージをそのまま画面表示。sealed のためラップする）
                // へ変換する——ConfigureAsync の他の検証（fail-closed 不変条件等）と同じ経路に揃える。
                throw new WizardValidationException(ex.Message);
            }

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
                Detail: $"windows={windowsAuthEnabled} kerberosOnly={kerberosOnly} app={appAuthEnabled} requireForLoopback={requireForLoopback}",
                AuthenticationScheme: operatorScheme,
                AuthenticatedPrincipal: operatorPrincipal),
            CancellationToken.None).ConfigureAwait(false);

        if (createdUsername is not null)
        {
            await _auditRecorder.RecordAsync(
                new AuditEvent(
                    OccurredAt: now,
                    Kind: AuditEventKind.AdminAccountCreated,
                    RemoteAddress: operatorAddress,
                    RemotePort: null,
                    Detail: $"username={createdUsername}",
                    AuthenticationScheme: operatorScheme,
                    AuthenticatedPrincipal: operatorPrincipal),
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
