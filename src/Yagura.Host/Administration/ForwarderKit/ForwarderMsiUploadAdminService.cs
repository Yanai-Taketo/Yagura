using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Configuration;
using Yagura.Storage.Administration;

namespace Yagura.Host.Administration.ForwarderKit;

/// <summary>
/// <see cref="IForwarderMsiUploadAdminService"/> の実体（ADR-0021 決定 4 = 委任 2）。
/// </summary>
/// <remarks>
/// 設定ファイルの読み書きは <see cref="YaguraConfigurationWriter"/> に委ねる
/// （<c>AdminRemoteAccessAdminService</c> と同じ「読み込み → 変更 → 検証 → 保存 → 監査」の
/// 共通規約。configuration.md §3・security.md §4.1）。
/// </remarks>
public sealed class ForwarderMsiUploadAdminService : IForwarderMsiUploadAdminService
{
    private const string EnabledKey = "Admin:ForwarderKit:MsiUpload:Enabled";

    private readonly string _dataRoot;
    private readonly IAdminAccountStore _accountStore;
    private readonly IAuditRecorder _auditRecorder;
    private readonly TimeProvider _timeProvider;

    public ForwarderMsiUploadAdminService(
        string dataRoot,
        IAdminAccountStore accountStore,
        IAuditRecorder auditRecorder,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(accountStore);
        ArgumentNullException.ThrowIfNull(auditRecorder);

        _dataRoot = dataRoot;
        _accountStore = accountStore;
        _auditRecorder = auditRecorder;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ForwarderMsiUploadSettingStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var options = YaguraConfigurationWriter.Read(_dataRoot).Options;
        var account = await _accountStore.GetSoleAccountAsync(cancellationToken).ConfigureAwait(false);

        return ToStatus(options, account);
    }

    public async Task<ForwarderMsiUploadSettingResult> ConfigureAsync(
        bool enabled,
        bool accountInventoryAcknowledged = false,
        string? operatorAddress = null,
        string? operatorScheme = null,
        string? operatorPrincipal = null,
        bool operatorIsUploadOperationAuthenticated = false,
        CancellationToken cancellationToken = default)
    {
        // 認可はアップロード操作と同格（ADR-0021 決定 4）——無認証 loopback から opt-in を
        // 反転できてはならない。画面が UI を出し分けたうえで、ここが最終防衛線になる。
        if (!operatorIsUploadOperationAuthenticated)
        {
            throw new WizardValidationException(
                "フォワーダ MSI アップロード機能の有効化・無効化にはサインインが必要です" +
                "（MSI の書き込み口を出現させる/消す操作であり、アップロード操作と同じ認可を要します" +
                "——ADR-0021 決定 4）。サインインしてからやり直してください。");
        }

        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        var before = snapshot.Options;
        var account = await _accountStore.GetSoleAccountAsync(cancellationToken).ConfigureAwait(false);
        var currentStatus = ToStatus(before, account);

        if (currentStatus.Enabled == enabled)
        {
            // no-op（保存も監査もしない。AdminRemoteAccessAdminService と同じ早期 return）。
            return new ForwarderMsiUploadSettingResult(
                Changed: false, ConfigurationApplyEffect.Immediate, currentStatus);
        }

        if (enabled)
        {
            // 前提条件（ADR-0021 決定 2）: 認証方式が最低 1 つ。起動時 1032 検証と同じ判定を
            // 書き込み前に行う（1011/1012 と同型の二段構え）。
            if (!currentStatus.WindowsAuthEnabled && !currentStatus.AppAuthEnabled)
            {
                throw new WizardValidationException(
                    "アップロード機能を有効化するには、管理 UI 認証（Windows 統合認証または" +
                    "アプリ独自認証）の少なくとも一方を先に有効にしてください。" +
                    "アップロード・削除は実際にサインインした管理者に限定されるため、" +
                    "サインインの手段が無い構成では有効化できません（この設定のまま再起動すると" +
                    "fail-closed 検証〔イベント 1032〕でサービスが起動できません）。");
            }

            // 切替時点検（ADR-0021 決定 1 の事前仕込み対処）:
            // opt-in が無効な間は無認証 loopback からアプリアカウントを作成できるため、
            // 有効化の時点で既存アカウントが「自分の管理下にあるか」を確認させる。
            if (currentStatus.HasAppAccount && !accountInventoryAcknowledged)
            {
                throw new WizardValidationException(
                    "有効化する前に、既存のアプリ独自認証アカウント" +
                    $"（{currentStatus.AppAccountUsername}）が自分の管理下にあることを確認してください。" +
                    "アップロード機能が無効な間は、このサーバ上のプロセスがサインインなしで" +
                    "アカウントを作成できるためです（作成・変更の記録は監査に残ります）。");
            }
        }

        var after = YaguraConfigurationOptionsCloner.Clone(before);
        after.Admin ??= new YaguraConfigurationOptions.AdminOptions();
        after.Admin.ForwarderKit ??= new YaguraConfigurationOptions.AdminOptions.ForwarderKitOptions();
        after.Admin.ForwarderKit.MsiUpload ??= new YaguraConfigurationOptions.AdminOptions.ForwarderKitOptions.MsiUploadOptions();
        after.Admin.ForwarderKit.MsiUpload.Enabled = enabled.ToString();

        YaguraConfigurationWriter.Save(_dataRoot, after, snapshot.VersionToken);

        // 設定変更の監査（security.md §4.1。既存の設定変更と同じ 2001 = ConfigurationSaved を使う
        // ——本操作は設定キー 1 つの変更であり、専用 ID を増やす理由がない）。切替時点検の確認有無・
        // 提示した既存アカウントの情報も残す——事後に「何を見て有効化したか」を追跡できるようにする。
        var detail =
            $"key={EnabledKey} value={enabled}" +
            $" existingAppAccount={(currentStatus.HasAppAccount ? currentStatus.AppAccountUsername : "(none)")}" +
            $" existingAppAccountUpdatedAt={FormatTimestamp(currentStatus.AppAccountUpdatedAtUtc)}" +
            // 最終ログインはアップグレード環境（作成・変更時刻が unknown）でも値が入るため、
            // 監査側にも残す——「何を見て有効化したか」の再現に必要。
            $" existingAppAccountLastLoginAt={FormatTimestamp(currentStatus.AppAccountLastLoginAtUtc)}" +
            $" inventoryAcknowledged={accountInventoryAcknowledged}";

        await _auditRecorder.RecordAsync(
            new AuditEvent(
                OccurredAt: _timeProvider.GetUtcNow(),
                Kind: AuditEventKind.ConfigurationSaved,
                RemoteAddress: operatorAddress,
                RemotePort: null,
                Detail: detail,
                AuthenticationScheme: operatorScheme,
                AuthenticatedPrincipal: operatorPrincipal),
            CancellationToken.None).ConfigureAwait(false);

        return new ForwarderMsiUploadSettingResult(
            Changed: true,
            // エンドポイントの条件付き登録（構造的非存在）は起動時に固定されるため常に再起動。
            ConfigurationApplyEffect.RestartRequired,
            ToStatus(after, account));
    }

    private static ForwarderMsiUploadSettingStatus ToStatus(YaguraConfigurationOptions options, AdminAccountRecord? account) =>
        new(
            Enabled: ParseBool(options.Admin?.ForwarderKit?.MsiUpload?.Enabled),
            WindowsAuthEnabled: ParseBool(options.Admin?.Authentication?.Windows?.Enabled),
            AppAuthEnabled: ParseBool(options.Admin?.Authentication?.App?.Enabled),
            HasAppAccount: account is not null,
            AppAccountUsername: account?.Username,
            AppAccountCreatedAtUtc: account?.CreatedAtUtc,
            AppAccountUpdatedAtUtc: account?.UpdatedAtUtc,
            // アップグレード環境では作成・変更時刻が NULL（v3 で追加した列のため）。最終ログインは
            // 旧版から記録されているため、点検の手がかりとして併記する。
            AppAccountLastLoginAtUtc: account?.LastLoginAtUtc);

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("O") ?? "unknown";

    private static bool ParseBool(string? raw) => bool.TryParse(raw, out var value) && value;
}
