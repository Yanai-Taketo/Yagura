using System.Runtime.Versioning;
using Microsoft.Extensions.Time.Testing;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Configuration;
using Yagura.Host.Observability.ActiveNotification.Email;

namespace Yagura.Host.Tests.Observability.ActiveNotification.Email;

/// <summary>
/// <see cref="EmailNotificationAdminService"/>（ADR-0017 決定 3・4・8、委任 12。Issue #350）の
/// 単体テスト。
/// </summary>
/// <remarks>
/// 本サービスは DPAPI（Windows 専用）を使うため <c>[SupportedOSPlatform("windows")]</c> が付いており、
/// テスト側にも同じ注記が要る（<see cref="Yagura.Host.Tests.Ingestion.Tls.IngestionTlsAdminServiceTests"/>
/// と同じ扱い。CI は windows-latest）。
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class EmailNotificationAdminServiceTests : IDisposable
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-email-admin-{Guid.NewGuid():N}");
    private readonly RecordingAuditRecorder _audit = new();
    private readonly EmailNotificationQueue _queue = new(new FakeTimeProvider(Origin));

    public EmailNotificationAdminServiceTests() => Directory.CreateDirectory(_dataRoot);

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    private sealed class RecordingAuditRecorder : IAuditRecorder
    {
        internal List<AuditEvent> Events { get; } = [];

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class StubSender(EmailSendResult result) : IEmailSender
    {
        internal ResolvedEmailNotification? LastConfiguration { get; private set; }

        public Task<EmailSendResult> SendAsync(
            ResolvedEmailNotification configuration, string subject, string body, CancellationToken cancellationToken = default)
        {
            LastConfiguration = configuration;
            return Task.FromResult(result);
        }
    }

    private EmailNotificationAdminService CreateService(IEmailSender? sender = null) =>
        new(_dataRoot,
            _audit,
            _queue,
            () => null,
            sender ?? new StubSender(EmailSendResult.Success()),
            new FakeTimeProvider(Origin));

    private static EmailNotificationSettings ValidSettings(
        bool enabled = true,
        string? from = "yagura@example.com",
        IReadOnlyList<string>? to = null,
        string? host = "smtp.example.com",
        int port = 25,
        string security = "auto",
        string? username = null,
        string? password = null,
        bool clearPassword = false) =>
        new(enabled, from, to ?? ["ops@example.com"], host, port, security, username, password, clearPassword);

    /// <summary>
    /// 保存済みパスワードのある構成を DPAPI を経由せずに作る（平文は Loader が警告付きで
    /// 受理する正当な表現——§2。DPAPI に依存しないため Windows 以外でも走る）。
    /// </summary>
    private void SeedStoredPlaintextCredential(string username = "yagura", string password = "stored-secret")
    {
        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        var options = snapshot.Options;
        options.Notification = new YaguraConfigurationOptions.NotificationOptions
        {
            Email = new YaguraConfigurationOptions.NotificationOptions.EmailOptions
            {
                Enabled = "true",
                From = "yagura@example.com",
                To = ["ops@example.com"],
                Smtp = new YaguraConfigurationOptions.NotificationOptions.EmailOptions.SmtpOptions
                {
                    Host = "smtp.example.com",
                    Port = "25",
                    Security = "required",
                    Username = username,
                    Password = password,
                },
            },
        };
        YaguraConfigurationWriter.Save(_dataRoot, options, snapshot.VersionToken);
    }

    // ------------------------------------------------------------------
    // 無関係キーの不正・不正な生値からの修復（Issue #370）
    // ------------------------------------------------------------------

    /// <summary>メール通知と無関係な「起動失敗」級の不正（UDP ポート）をファイルへ仕込む。</summary>
    private void SeedUnrelatedInvalidKey()
    {
        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        var options = snapshot.Options;
        options.Ingestion ??= new YaguraConfigurationOptions.IngestionOptions();
        options.Ingestion.Udp ??= new YaguraConfigurationOptions.IngestionOptions.UdpOptions();
        options.Ingestion.Udp.Port = "not-a-port";
        YaguraConfigurationWriter.Save(_dataRoot, options, snapshot.VersionToken);
    }

    [Fact]
    public async Task GetStatusAsync_UnrelatedInvalidKey_DoesNotThrowAndReportsTheFileError()
    {
        // Loader は Udp:Port の不正で ConfigurationValidationException を投げるが、
        // メール設定画面を circuit エラーで壊さず「ファイル側の問題」として見せる（Issue #370）。
        SeedUnrelatedInvalidKey();
        var service = CreateService();

        var status = await service.GetStatusAsync();

        Assert.NotNull(status.Health.ConfigurationFileError);
        // 縮退判定は不能——誤った「送られていません」バナーは出さない。
        Assert.False(status.Health.DisabledByInvalidConfiguration);
    }

    [Fact]
    public async Task ConfigureAsync_UnrelatedInvalidKey_StillSavesAuditsAndReportsTheFileError()
    {
        // 保存と監査は成功し、即時反映だけが見送られる（保存の成否とファイルの問題を区別する）。
        SeedUnrelatedInvalidKey();
        var service = CreateService();

        var result = await service.ConfigureAsync(ValidSettings());

        Assert.NotEmpty(result.ChangedKeys);
        Assert.Single(_audit.Events);
        Assert.NotNull(result.Status.Health.ConfigurationFileError);

        var persisted = YaguraConfigurationWriter.Read(_dataRoot).Options.Notification?.Email;
        Assert.Equal("smtp.example.com", persisted?.Smtp?.Host);
    }

    [Theory]
    [InlineData("smtp")]     // 解釈不能な文字列
    [InlineData("25 25")]    // 数値として不正
    public async Task ConfigureAsync_InvalidRawPortInFile_IsRepairedEvenWhenTheFormShowsTheDefault(string rawPort)
    {
        // 実効値比較が不正な生値を既定値へ写像すると、画面の表示値（既定 25）のまま保存しても
        // no-op になり「直したのに直らない」が起きる（Issue #370）。不正 → 有効値は常に変更として扱う。
        var seedService = CreateService();
        await seedService.ConfigureAsync(ValidSettings());
        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        snapshot.Options.Notification!.Email!.Smtp!.Port = rawPort;
        YaguraConfigurationWriter.Save(_dataRoot, snapshot.Options, snapshot.VersionToken);
        _audit.Events.Clear();

        var service = CreateService();
        var result = await service.ConfigureAsync(ValidSettings(port: 25));

        Assert.Contains("Notification:Email:Smtp:Port", result.ChangedKeys);
        Assert.Equal("25", YaguraConfigurationWriter.Read(_dataRoot).Options.Notification?.Email?.Smtp?.Port);
    }

    [Fact]
    public async Task ConfigureAsync_InvalidRawEnabledInFile_IsRepairedEvenWhenSavingDisabled()
    {
        var seedService = CreateService();
        await seedService.ConfigureAsync(ValidSettings());
        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        snapshot.Options.Notification!.Email!.Enabled = "yes"; // 真偽値として不正（手編集想定）
        YaguraConfigurationWriter.Save(_dataRoot, snapshot.Options, snapshot.VersionToken);
        _audit.Events.Clear();

        var service = CreateService();
        var result = await service.ConfigureAsync(ValidSettings(enabled: false));

        Assert.Contains("Notification:Email:Enabled", result.ChangedKeys);
        Assert.Equal(bool.FalseString, YaguraConfigurationWriter.Read(_dataRoot).Options.Notification?.Email?.Enabled);
    }

    // ------------------------------------------------------------------
    // 保存前検証（起動時の縮退を、利用者が目の前にいる場面では拒否に倒す）
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(null, "差出人が空")]
    [InlineData("not-an-address", "差出人の形式が不正")]
    public async Task ConfigureAsync_InvalidFrom_IsRejected(string? from, string reason)
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<WizardValidationException>(
            () => service.ConfigureAsync(ValidSettings(from: from)));

        Assert.False(string.IsNullOrWhiteSpace(exception.Message), reason);
        Assert.Empty(_audit.Events);
    }

    [Fact]
    public async Task ConfigureAsync_NoRecipients_IsRejected()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<WizardValidationException>(
            () => service.ConfigureAsync(ValidSettings(to: [])));
    }

    [Fact]
    public async Task ConfigureAsync_OnlyOneOfUsernameAndPassword_IsRejected()
    {
        // 決定 3: 認証なしの送信へ黙って落とさない。保存の時点で止める。
        var service = CreateService();

        await Assert.ThrowsAsync<WizardValidationException>(
            () => service.ConfigureAsync(ValidSettings(username: "yagura", password: null)));
    }

    [Fact]
    public async Task ConfigureAsync_PortOutOfRange_IsRejected()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<WizardValidationException>(
            () => service.ConfigureAsync(ValidSettings(port: 70000)));
    }

    [Fact]
    public async Task ConfigureAsync_DisablingWithEmptyFields_IsAccepted()
    {
        // 無効化は必須項目の検証を要求しない——設定ごと消して止める操作を塞がない
        // （有効化時と同じ検証を掛けると「壊れた設定を直さないと止められない」ことになる）。
        var service = CreateService();
        await service.ConfigureAsync(ValidSettings(enabled: true));

        var result = await service.ConfigureAsync(ValidSettings(enabled: false, from: null, to: [], host: null));

        Assert.Contains("Notification:Email:Enabled", result.ChangedKeys);
        Assert.False((await service.GetStatusAsync()).Enabled);
    }

    // ------------------------------------------------------------------
    // 保存・差分・監査（決定 4）
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConfigureAsync_PersistsAndAuditsWithValues()
    {
        var service = CreateService();

        var result = await service.ConfigureAsync(
            ValidSettings(host: "relay.example.com", port: 587, security: "required"),
            operatorAddress: "127.0.0.1",
            operatorScheme: "Negotiate",
            operatorPrincipal: "EXAMPLE\\admin");

        Assert.Equal(ConfigurationApplyEffect.Immediate, result.RequiredEffect);
        Assert.Contains("Notification:Email:Smtp:Host", result.ChangedKeys);

        var status = await service.GetStatusAsync();
        Assert.True(status.Enabled);
        Assert.Equal("relay.example.com", status.SmtpHost);
        Assert.Equal(587, status.SmtpPort);
        Assert.Equal("required", status.Security);

        // 監査 2021 は「変更キーと新値」を残す（宛先・接続先は流出経路そのものの定義であり、
        // キー名粒度では事後に追えない）。
        var audit = Assert.Single(_audit.Events);
        Assert.Equal(AuditEventKind.EmailNotificationConfigured, audit.Kind);
        Assert.Contains("relay.example.com", audit.Detail);
        Assert.Contains("ops@example.com", audit.Detail);
        Assert.Equal("EXAMPLE\\admin", audit.AuthenticatedPrincipal);
    }

    [Fact]
    public async Task ConfigureAsync_NoChanges_IsNoOpWithoutAudit()
    {
        var service = CreateService();
        await service.ConfigureAsync(ValidSettings());
        _audit.Events.Clear();

        var result = await service.ConfigureAsync(ValidSettings());

        // 同値保存の反復で監査証跡を希釈しない。
        Assert.Empty(result.ChangedKeys);
        Assert.Empty(_audit.Events);
    }

    // ------------------------------------------------------------------
    // パスワードの扱い（決定 3・8）
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConfigureAsync_BlankPassword_KeepsTheStoredValue()
    {
        // 空欄 = 「変更しない」（UI に再表示しないため、この意味に固定する。決定 8）。
        var service = CreateService();

        await service.ConfigureAsync(ValidSettings(username: "yagura", password: "s3cret", security: "required"));
        var afterFirst = await service.GetStatusAsync();
        Assert.True(afterFirst.PasswordConfigured);

        // パスワード欄を空のまま他のキーだけ変更する。
        var result = await service.ConfigureAsync(
            ValidSettings(username: "yagura", password: null, security: "required", host: "relay2.example.com"));

        Assert.DoesNotContain("Notification:Email:Smtp:Password", result.ChangedKeys);
        Assert.True((await service.GetStatusAsync()).PasswordConfigured);
    }

    [Fact]
    public async Task ConfigureAsync_ClearPassword_RemovesTheStoredValueAndEndsAuthentication()
    {
        // 「空欄 = 変更しない」に固定した帰結として、削除には専用の口が要る——これがないと
        // 一度パスワードを保存した構成は SMTP 認証をやめられない（PR #366 レビュー対応）。
        SeedStoredPlaintextCredential();
        var service = CreateService();

        var result = await service.ConfigureAsync(
            ValidSettings(username: null, password: null, security: "required", clearPassword: true));

        Assert.Contains("Notification:Email:Smtp:Password", result.ChangedKeys);
        Assert.Contains("Notification:Email:Smtp:Username", result.ChangedKeys);

        var persisted = YaguraConfigurationWriter.Read(_dataRoot).Options.Notification?.Email?.Smtp;
        Assert.True(string.IsNullOrEmpty(persisted?.Password));
        Assert.True(string.IsNullOrEmpty(persisted?.Username));
        Assert.False((await service.GetStatusAsync()).PasswordConfigured);

        // 監査には「削除した」事実が値なしで残る。
        var audit = Assert.Single(_audit.Events);
        Assert.Contains("(削除)", audit.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("stored-secret", audit.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigureAsync_ClearPasswordWithoutAStoredValue_IsANoOpForThePasswordKey()
    {
        // 保存済みの値がなければ「削除」は変更として数えない（監査 2021 を空振りで積まない）。
        var service = CreateService();

        await service.ConfigureAsync(ValidSettings());
        _audit.Events.Clear();

        var result = await service.ConfigureAsync(ValidSettings(clearPassword: true));

        Assert.DoesNotContain("Notification:Email:Smtp:Password", result.ChangedKeys);
        Assert.Empty(result.ChangedKeys);
        Assert.Empty(_audit.Events);
    }

    [Fact]
    public async Task ConfigureAsync_ClearPasswordTogetherWithANewPassword_IsRejected()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<WizardValidationException>(() => service.ConfigureAsync(
            ValidSettings(username: "yagura", password: "new-secret", clearPassword: true)));

        Assert.Empty(_audit.Events);
    }

    [Fact]
    public async Task ConfigureAsync_ClearPasswordWhileKeepingTheUsername_IsRejected()
    {
        // 削除しても決定 3 の「両方あり/両方なし」は破れない——ユーザー名を残したままの削除は拒否。
        SeedStoredPlaintextCredential();
        var service = CreateService();

        await Assert.ThrowsAsync<WizardValidationException>(() => service.ConfigureAsync(
            ValidSettings(username: "yagura", password: null, security: "required", clearPassword: true)));
    }

    [Fact]
    public async Task SendTestAsync_ClearPassword_DoesNotFallBackToTheStoredCredential()
    {
        // 削除を指定している間は保存済み資格情報でのテスト送信を行わない——削除しようとしている
        // 資格情報で「成功」を見せない。
        SeedStoredPlaintextCredential();
        var sender = new StubSender(EmailSendResult.Success());
        var service = CreateService(sender);

        await Assert.ThrowsAsync<WizardValidationException>(() => service.SendTestAsync(
            ValidSettings(username: "yagura", password: null, security: "required", clearPassword: true)));

        Assert.Null(sender.LastConfiguration); // 送信の試行自体が行われていない
    }

    [Fact]
    public async Task SendTestAsync_CancelledByTheOperator_StillRecordsTheAuditTrail()
    {
        // 2022 を監査対象とした理由（任意ホストへの接続試行 = 到達性探査に転用し得る）は、
        // 接続開始直後の中止で証跡ゼロになるなら成立しない（PR #366 レビュー対応）。
        var service = CreateService(new CancelledSender());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SendTestAsync(ValidSettings()));

        var audit = Assert.Single(_audit.Events);
        Assert.Equal(AuditEventKind.EmailNotificationTestSent, audit.Kind);
        Assert.Contains("result=cancelled", audit.Detail, StringComparison.Ordinal);
    }

    private sealed class CancelledSender : IEmailSender
    {
        public Task<EmailSendResult> SendAsync(
            ResolvedEmailNotification configuration, string subject, string body, CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException();
    }

    [Fact]
    public async Task ConfigureAsync_PasswordIsEncryptedAtRestAndNeverAudited()
    {
        var service = CreateService();

        await service.ConfigureAsync(ValidSettings(username: "yagura", password: "s3cret", security: "required"));

        var persisted = YaguraConfigurationWriter.Read(_dataRoot).Options.Notification?.Email?.Smtp?.Password;
        Assert.NotNull(persisted);
        Assert.True(DpapiEmailPasswordProtector.IsProtected(persisted));
        Assert.DoesNotContain("s3cret", persisted!, StringComparison.Ordinal);

        // 監査には「変更した事実」だけが載る（決定 4）。
        var audit = Assert.Single(_audit.Events);
        Assert.DoesNotContain("s3cret", audit.Detail!, StringComparison.Ordinal);
        Assert.Contains("変更あり", audit.Detail!);
    }

    [Fact]
    public async Task ConfigureAsync_PasswordWithoutRequiredTls_WarnsButSaves()
    {
        // 決定 3: 運用上の選択であり得るため受理する。ただし黙って通さない。
        var service = CreateService();

        var result = await service.ConfigureAsync(
            ValidSettings(username: "yagura", password: "s3cret", security: "auto"));

        Assert.True(result.PlaintextCredentialWarning);
        Assert.NotEmpty(result.ChangedKeys);
    }

    // ------------------------------------------------------------------
    // テスト送信（決定 8・委任 12）
    // ------------------------------------------------------------------

    [Fact]
    public async Task SendTestAsync_UsesTheUnsavedValuesFromTheScreen()
    {
        // 保存前の検証こそがテスト送信の価値（決定 8）。
        var sender = new StubSender(EmailSendResult.Success());
        var service = CreateService(sender);

        var result = await service.SendTestAsync(ValidSettings(host: "unsaved.example.com", port: 2525));

        Assert.True(result.Succeeded);
        Assert.Equal("unsaved.example.com", sender.LastConfiguration!.SmtpHost);
        Assert.Equal(2525, sender.LastConfiguration.SmtpPort);

        // 保存はされていない。
        Assert.Null(YaguraConfigurationWriter.Read(_dataRoot).Options.Notification?.Email?.Smtp?.Host);
    }

    [Fact]
    public async Task SendTestAsync_BlankPasswordUsesStoredCredential_AndRecordsThatDistinction()
    {
        var sender = new StubSender(EmailSendResult.Success());
        var service = CreateService(sender);

        await service.ConfigureAsync(ValidSettings(username: "yagura", password: "s3cret", security: "required"));
        _audit.Events.Clear();

        await service.SendTestAsync(ValidSettings(username: "yagura", password: null, security: "required"));

        // 保存済みの平文が復号されて送信に使われる。
        Assert.Equal("s3cret", sender.LastConfiguration!.Password);

        // 「画面上の任意ホストへ保存済み資格情報で AUTH を試行できる」操作であることを証跡に残す。
        var audit = Assert.Single(_audit.Events);
        Assert.Equal(AuditEventKind.EmailNotificationTestSent, audit.Kind);
        Assert.Contains("storedCredentialUsed=True", audit.Detail!);
        Assert.DoesNotContain("s3cret", audit.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendTestAsync_IsAuditedEvenWhenItFails()
    {
        // 到達性探査への転用を考えると、失敗こそ記録が要る（成功だけ記録では探査が見えない）。
        var sender = new StubSender(
            EmailSendResult.Failure(EmailSendFailureKind.ConnectionFailed, "接続できません"));
        var service = CreateService(sender);

        var result = await service.SendTestAsync(ValidSettings(host: "10.0.0.1"));

        Assert.False(result.Succeeded);
        var audit = Assert.Single(_audit.Events);
        Assert.Equal(AuditEventKind.EmailNotificationTestSent, audit.Kind);
        Assert.Contains("10.0.0.1", audit.Detail!);
        Assert.Contains("failure:ConnectionFailed", audit.Detail!);
    }

    [Fact]
    public async Task SendTestAsync_AuthenticationFailure_TellsTheUserNotToRetypeThePassword()
    {
        // 委任 12 の核心: 生の応答だけを見せると、原因に辿り着けない利用者がパスワードを
        // 打ち直してアカウントをロックする——決定 3 が避けたい事態そのものになる。
        var sender = new StubSender(
            EmailSendResult.Failure(EmailSendFailureKind.AuthenticationRejected, "535 5.7.3 Authentication unsuccessful"));
        var service = CreateService(sender);

        var result = await service.SendTestAsync(ValidSettings(username: "yagura", password: "wrong"));

        Assert.False(result.Succeeded);
        Assert.Contains("ロック", result.Guidance);
        Assert.Contains("繰り返し", result.Guidance);
    }

    [Fact]
    public async Task SendTestAsync_SmtpAuthDisabledResponse_PointsAtTheRelayGuidance()
    {
        // 判定は応答文字列の部分一致に留め、日付やスケジュールには言及しない（委任 12）。
        var sender = new StubSender(EmailSendResult.Failure(
            EmailSendFailureKind.AuthenticationRejected,
            "535 5.7.139 Authentication unsuccessful, SmtpClientAuthentication is disabled for the Tenant."));
        var service = CreateService(sender);

        var result = await service.SendTestAsync(ValidSettings(username: "yagura", password: "p"));

        Assert.Contains("リレー", result.Guidance);
        Assert.Contains("解決しません", result.Guidance);
        // 日付・スケジュールを名指ししない（提供状況の変化で陳腐化するため）。
        Assert.DoesNotContain("2026", result.Guidance);
        Assert.DoesNotContain("年", result.Guidance);
    }

    [Fact]
    public async Task SendTestAsync_RequiredStartTlsUnavailable_ExplainsNoPlaintextFallbackHappened()
    {
        var sender = new StubSender(
            EmailSendResult.Failure(EmailSendFailureKind.StartTlsUnavailable, "STARTTLS 非対応"));
        var service = CreateService(sender);

        var result = await service.SendTestAsync(ValidSettings(security: "required"));

        Assert.Contains("平文", result.Guidance);
        Assert.Contains("送信は行っていません", result.Guidance);
    }
}
